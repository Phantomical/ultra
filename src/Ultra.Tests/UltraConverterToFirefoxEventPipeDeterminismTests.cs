// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Ultra.Core;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Regression test for the parallelization of the per-thread conversion loop in
/// <see cref="UltraConverterToFirefoxEventPipe"/>: converting the same synthetic session with
/// <see cref="UltraProfilerOptions.MaxDegreeOfParallelism"/> forced to <c>1</c> (fully sequential) and,
/// repeatedly, to a value that genuinely oversubscribes this machine, must produce byte-identical
/// serialized profiles every time. Any ordering that leaks from thread-completion order instead of thread
/// index would show up here as a mismatch.
/// </summary>
/// <remarks>
/// Coverage notes (please keep this honest as the test evolves):
/// <list type="bullet">
/// <item>
/// Both native modules point at minimal, real Mach-O files on disk (see <see cref="WriteMinimalMachO"/>),
/// so <c>MachOSymbolReader.TryRead</c> actually succeeds and the native-symbol resolution path - including
/// the per-thread <c>MangledNames</c> collection and its merge (in deterministic thread-index order) into
/// the shared, process-wide mangled-name set - is genuinely exercised, concurrently, by this test.
/// </item>
/// <item>
/// This does NOT exercise <c>CppDemangler.Demangle</c>'s actual <c>c++filt</c> positional pairing (the one
/// place a merge-order bug could silently swap two names' demangled output): that requires the <c>c++filt</c>
/// executable, which is not guaranteed to be installed on the machine running this test (it returns null
/// gracefully when the process fails to start, and the mangled names are kept as-is). What this test does
/// guarantee is that the *set* fed into that pairing - <c>_mangledNames</c> - is built up identically
/// regardless of scheduling, which is the parallel-work-specific risk; a `c++filt`-dependent test of the
/// pairing itself belongs elsewhere and would only run where that tool exists.
/// </item>
/// </list>
/// </remarks>
[TestClass]
public class UltraConverterToFirefoxEventPipeDeterminismTests
{
    private const int ProcessId = 777;
    private const ulong CoreClrModuleBase = 0x0000_1000_0000_0000UL;
    private const ulong NativeModuleBase = 0x0000_2000_0000_0000UL;

    [TestMethod]
    public void ParallelConversionIsDeterministic()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("UltraDeterminismTest_");
        try
        {
            var coreClrModulePath = Path.Combine(tempDirectory.FullName, "libcoreclr.dylib");
            var nativeModulePath = Path.Combine(tempDirectory.FullName, "libnative.dylib");

            // One mangled (C++) and one plain (C) symbol, each placed exactly at its module's base address:
            // TryResolve's binary search then attributes every code address the test generates (all within
            // a few KB of the base - see BuildSession) to that single symbol.
            WriteMinimalMachO(coreClrModulePath, CoreClrModuleBase, [("__ZN3WKS7gc_heap15garbage_collectEi", CoreClrModuleBase)]);
            WriteMinimalMachO(nativeModulePath, NativeModuleBase, [("_NativeWork", NativeModuleBase)]);

            var sequentialJson = ConvertToJson(maxDegreeOfParallelism: 1, coreClrModulePath, nativeModulePath);

            // Sanity check that the Mach-O files above were actually parsed and resolved - i.e. that this
            // test is genuinely exercising MachOSymbolReader/native-symbol resolution/mangled-name
            // collection, not silently falling back to the "moduleName!0xADDRESS" unresolved-symbol format.
            StringAssert.Contains(sequentialJson, "NativeWork");
            StringAssert.Contains(sequentialJson, "garbage_collect");

            // CI can have as few as 2 processors, which would barely oversubscribe the parallel region at
            // MaxDegreeOfParallelism = Environment.ProcessorCount; force genuine oversubscription instead of
            // relying on the local machine's core count, and repeat several times since a scheduling-order
            // bug does not necessarily reproduce on every single run.
            var parallelMaxDegreeOfParallelism = Environment.ProcessorCount * 4;
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var parallelJson = ConvertToJson(parallelMaxDegreeOfParallelism, coreClrModulePath, nativeModulePath);
                Assert.AreEqual(sequentialJson, parallelJson, $"Mismatch on iteration {iteration}");
            }
        }
        finally
        {
            Directory.Delete(tempDirectory.FullName, recursive: true);
        }
    }

    private static string ConvertToJson(int maxDegreeOfParallelism, string coreClrModulePath, string nativeModulePath)
    {
        var session = BuildSession(coreClrModulePath, nativeModulePath, out var process);

        var options = new UltraProfilerOptions
        {
            // The default depends on OperatingSystem.IsWindows(); pin it so the two runs are comparable.
            CpuSamplingIntervalInMs = 1.0f,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        };

        using var converter = new UltraConverterToFirefoxEventPipe(options);
        var profile = converter.ConvertSessionForTests(session, new List<int> { process.ProcessID });

        // Meta.Abi is populated from the live machine's RuntimeInformation.RuntimeIdentifier; pin it so
        // both runs (which execute on the same machine anyway) compare on everything else.
        profile.Meta.Abi = "test-abi";

        var jsonOptions = new JsonSerializerOptions(FirefoxProfiler.JsonProfilerContext.Default.Options)
        {
            WriteIndented = true
        };

        return JsonSerializer.Serialize(profile, jsonOptions);
    }

    /// <summary>
    /// Hand-builds a minimal but valid non-fat Mach-O 64-bit file: a <c>mach_header_64</c>, one
    /// <c>LC_SEGMENT_64</c> load command naming the <c>__TEXT</c> segment (so
    /// <see cref="Ultra.Core.MachO.MachOSymbolReader"/> has a text-segment virtual address to compute
    /// relative addresses from), and one <c>LC_SYMTAB</c> load command whose symbol/string tables hold
    /// exactly <paramref name="symbols"/>. See <c>MachOSymbolReader.TryRead</c> for the exact layout this
    /// must match.
    /// </summary>
    private static void WriteMinimalMachO(string filePath, ulong textSegmentVirtualAddress, (string Name, ulong Value)[] symbols)
    {
        const uint MachOMagic64 = 0xFEEDFACF;
        const uint LcSegment64 = 0x19;
        const uint LcSymtab = 0x2;
        const int HeaderSize = 32;
        const int SegmentCommandSize = 72;
        const int SymtabCommandSize = 24;
        const int Nlist64Size = 16;

        var symbolTableOffset = HeaderSize + SegmentCommandSize + SymtabCommandSize;
        var symbolTableSize = symbols.Length * Nlist64Size;
        var stringTableOffset = symbolTableOffset + symbolTableSize;

        // Conventional leading NUL (index 0 is reserved for "no name"), then each name NUL-terminated.
        var stringTableBytes = new List<byte> { 0 };
        var nameOffsets = new int[symbols.Length];
        for (var i = 0; i < symbols.Length; i++)
        {
            nameOffsets[i] = stringTableBytes.Count;
            stringTableBytes.AddRange(Encoding.ASCII.GetBytes(symbols[i].Name));
            stringTableBytes.Add(0);
        }

        var data = new byte[stringTableOffset + stringTableBytes.Count];
        var span = data.AsSpan();

        // mach_header_64
        BinaryPrimitives.WriteUInt32LittleEndian(span, MachOMagic64);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), 0x0100000C); // cputype (arm64; unused for non-fat files)
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), 0);          // cpusubtype
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 2);        // filetype: MH_EXECUTE
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), 2);        // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), SegmentCommandSize + SymtabCommandSize); // sizeofcmds
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), 0);        // flags
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(28), 0);        // reserved

        // segment_command_64 (__TEXT)
        const int segmentOffset = HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(segmentOffset), LcSegment64);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(segmentOffset + 4), SegmentCommandSize);
        Encoding.ASCII.GetBytes("__TEXT").AsSpan().CopyTo(span.Slice(segmentOffset + 8, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(segmentOffset + 24), textSegmentVirtualAddress);
        // vmsize, fileoff, filesize, maxprot, initprot, nsects, flags: left zero (unused by the reader).

        // symtab_command
        var symtabOffset = segmentOffset + SegmentCommandSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset), LcSymtab);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 4), SymtabCommandSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 8), (uint)symbolTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 12), (uint)symbols.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 16), (uint)stringTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(symtabOffset + 20), (uint)stringTableBytes.Count);

        // nlist_64 entries
        for (var i = 0; i < symbols.Length; i++)
        {
            var entryOffset = symbolTableOffset + i * Nlist64Size;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOffset), (uint)nameOffsets[i]);
            span[entryOffset + 4] = 0x0F; // n_type: N_SECT | N_EXT
            span[entryOffset + 5] = 1;    // n_sect
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(entryOffset + 6), 0); // n_desc
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entryOffset + 8), symbols[i].Value);
        }

        stringTableBytes.CopyTo(data, stringTableOffset);

        File.WriteAllBytes(filePath, data);
    }

    /// <summary>
    /// Builds a synthetic session with enough threads and cross-thread sharing to exercise every
    /// determinism concern of the parallel conversion:
    /// <list type="bullet">
    /// <item>Many threads (more than any realistic core count) with different CPU times, so the parallel
    /// region has more work items than workers and threads are genuinely reordered by the scheduler.</item>
    /// <item>All threads sample code addresses belonging to the same two native modules - each backed by a
    /// real Mach-O file on disk (<paramref name="coreClrModulePath"/>/<paramref name="nativeModulePath"/>,
    /// see <see cref="WriteMinimalMachO"/>) - so the shared <c>MachOSymbolReader</c> cache, the module
    /// address-range lookup, and the per-thread mangled-name collection/merge are all hit concurrently.</item>
    /// <item>Every thread reports a GCHeapStats marker at the exact same timestamp, which stresses the
    /// tie-breaking behavior of the final (non-stable) sort: only a deterministic (thread-index-ordered)
    /// merge before that sort keeps ties resolved the same way regardless of completion order.</item>
    /// </list>
    /// </summary>
    private static UTraceSession BuildSession(string coreClrModulePath, string nativeModulePath, out UTraceProcess process)
    {
        var session = new UTraceSession
        {
            NumberOfProcessors = 8,
            CpuSpeedMHz = 3000,
            StartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Duration = UTimeSpan.FromMilliseconds(1000),
        };

        process = new UTraceProcess
        {
            ProcessID = ProcessId,
            FilePath = "detapp.exe",
            RuntimeIdentifier = "osx-arm64",
            OSDescription = "macOS 14.5 (Test Fixture)",
        };
        session.Processes.Add(process);

        process.Modules.GetOrCreateNativeModule(CoreClrModuleBase, 0x10_0000UL, coreClrModulePath);
        process.Modules.GetOrCreateNativeModule(NativeModuleBase, 0x8_0000UL, nativeModulePath);

        const int threadCount = 32;
        for (var threadOrdinal = 0; threadOrdinal < threadCount; threadOrdinal++)
        {
            var thread = process.Threads.GetOrCreateThread((ulong)(2000 + threadOrdinal));
            thread.Name = $"Worker{threadOrdinal}";
            thread.StartTime = UTimeSpan.FromMilliseconds(0);

            // Vary the sample/CPU-time count per thread so thread ordering (by CPU time) is non-trivial.
            var sampleCount = 1 + (threadOrdinal % 5);
            for (var s = 0; s < sampleCount; s++)
            {
                // Alternate between the two native modules across threads/samples.
                var moduleBase = (s + threadOrdinal) % 2 == 0 ? CoreClrModuleBase : NativeModuleBase;
                var codeAddr = process.CodeAddresses.GetOrCreateAddress(moduleBase + 0x1000UL + (ulong)s, managedMethodIndex: -1);
                var stack = process.CallStacks.InsertCallStack([codeAddr]);
                thread.Samples.Add(new UTraceSample(stack, UTimeSpan.FromMilliseconds(s + 1), UTimeSpan.FromMilliseconds(1 + (threadOrdinal % 3))));
            }

            // Every thread reports a GCHeapStats marker at the SAME timestamp: when threads are merged in
            // thread-index order before the final (unstable) sort, ties always resolve the same way
            // regardless of which thread the parallel region happened to finish converting first.
            thread.Markers.Add(new GCHeapStatsTraceMarker
            {
                StartTime = UTimeSpan.FromMilliseconds(5),
                TotalHeapSize = 1000 + threadOrdinal,
                TotalPromoted = 0,
                GenerationSize0 = 0,
                TotalPromotedSize0 = 0,
                GenerationSize1 = 0,
                TotalPromotedSize1 = 0,
                GenerationSize2 = 0,
                TotalPromotedSize2 = 0,
                GenerationSize3 = 0,
                TotalPromotedSize3 = 0,
                GenerationSize4 = 0,
                TotalPromotedSize4 = 0,
                FinalizationPromotedSize = 0,
                FinalizationPromotedCount = 0,
                PinnedObjectCount = 0,
                SinkBlockCount = 0,
                GCHandleCount = 0,
            });
        }

        return session;
    }
}

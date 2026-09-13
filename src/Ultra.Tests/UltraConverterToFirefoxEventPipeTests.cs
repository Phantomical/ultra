// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// End-to-end regression test for <see cref="UltraConverterToFirefoxEventPipe"/>: builds a synthetic
/// <see cref="UTraceSession"/> entirely in memory (no trace file on disk, no <c>EventPipeEventSource</c>
/// involved) and snapshots the resulting <see cref="FirefoxProfiler.Profile"/> with Verify.
/// </summary>
/// <remarks>
/// This is the safety net for the parallelization refactor of <see cref="UltraConverterToFirefoxEventPipe"/>:
/// any behavior change to the sample/stack/marker conversion logic should perturb this snapshot.
/// </remarks>
[TestClass]
public class UltraConverterToFirefoxEventPipeTests : VerifyBase
{
    private const int ProcessId = 4242;

    [TestMethod]
    public async Task ConvertsSyntheticSessionToFirefoxProfile()
    {
        var session = BuildSession(out var process);

        var options = new UltraProfilerOptions
        {
            // The default depends on OperatingSystem.IsWindows(); pin it so the snapshot is
            // reproducible on every platform that runs this test.
            CpuSamplingIntervalInMs = 1.0f,
        };

        using var converter = new UltraConverterToFirefoxEventPipe(options);
        var profile = converter.ConvertSessionForTests(session, new List<int> { process.ProcessID });

        // Meta.Abi is populated from the live machine's RuntimeInformation.RuntimeIdentifier
        // (see UltraConverterToFirefoxEventPipe.CreateProfile), so it is never actually
        // deterministic across machines/CI. Pin it here for a reproducible snapshot.
        profile.Meta.Abi = "test-abi";

        var jsonOptions = new JsonSerializerOptions(FirefoxProfiler.JsonProfilerContext.Default.Options)
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(profile, jsonOptions);

        await Verify(json);
    }

    /// <summary>
    /// Builds a synthetic session with a single process covering, in one pass:
    /// <list type="bullet">
    /// <item>3 threads with different CPU times, one below and two above <see cref="UltraProfilerOptions.MinimumCpuTimeBeforeThreadIsVisibleInMs"/>,
    /// so thread ordering, <c>InitialVisibleThreads</c> and <c>InitialSelectedThreads</c> are exercised.</item>
    /// <item>Samples sharing call-stack prefixes (stack-table interning), plus one sample pointing at the
    /// reserved root call stack (index 0), which must be skipped by the converter.</item>
    /// <item>Native modules named <c>libcoreclr.dylib</c> and <c>libclrjit.dylib</c> (to cover the CLR/JIT
    /// category branches) plus a plain native module (generic native category).</item>
    /// <item>A managed method resolved from two different code addresses (managed category, func dedup).</item>
    /// <item>A code address belonging to no module and no managed method (<c>ConvertUnknownAddress</c>).</item>
    /// <item>One marker of every marker type, with 2 GCHeapStats markers (different <c>TotalHeapSize</c>,
    /// added out of chronological order) to exercise the memory-counter delta and its sort.</item>
    /// </list>
    /// Does NOT cover: multiple processes, thread/module unload events, recompiled/collectible managed
    /// methods (code address recycling), or native symbol resolution (module files intentionally point at
    /// paths that do not exist on disk, so <c>MachOSymbolReader.TryRead</c> deterministically returns false).
    /// </summary>
    private static UTraceSession BuildSession(out UTraceProcess process)
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
            FilePath = "myapp.exe",
            CommandLine = "myapp.exe --run",
            ProcessArchitecture = Architecture.Arm64,
            RuntimeIdentifier = "osx-arm64",
            OSDescription = "macOS 14.5 (Test Fixture)",
        };
        session.Processes.Add(process);

        // Native modules: one CoreCLR, one CLR JIT, and one plain native module.
        process.Modules.GetOrCreateNativeModule(0x0000_1000_0000_0000UL, 0x10_0000UL, "/fake/libcoreclr.dylib");
        process.Modules.GetOrCreateNativeModule(0x0000_2000_0000_0000UL, 0x8_0000UL, "/fake/libclrjit.dylib");
        process.Modules.GetOrCreateNativeModule(0x0000_3000_0000_0000UL, 0x4_0000UL, "/fake/libSystem.Native.dylib");

        // Managed module + methods.
        process.Modules.GetOrCreateManagedModule(moduleID: 1, assemblyId: 1, moduleFilePath: "/fake/MyApp.dll");
        var mainMethodIndex = AddManagedMethod(process, moduleId: 1, methodId: 1, methodNamespace: "MyApp", methodName: "Program.Main", startAddress: 0x9000_0000_1000UL);
        var doWorkMethodIndex = AddManagedMethod(process, moduleId: 1, methodId: 2, methodNamespace: "MyApp", methodName: "Worker.DoWork", startAddress: 0x9000_0000_2000UL);

        // Code addresses. Two different addresses resolve to the same managed method (DoWork), to
        // exercise func-level dedup independently of frame-level dedup.
        var codeAddrMain = process.CodeAddresses.GetOrCreateAddress(0x9000_0000_1000UL, mainMethodIndex);
        var codeAddrDoWorkA = process.CodeAddresses.GetOrCreateAddress(0x9000_0000_2000UL, doWorkMethodIndex);
        var codeAddrDoWorkB = process.CodeAddresses.GetOrCreateAddress(0x9000_0000_2020UL, doWorkMethodIndex);
        var codeAddrCoreClr = process.CodeAddresses.GetOrCreateAddress(0x0000_1000_0000_2000UL, managedMethodIndex: -1);
        var codeAddrClrJit = process.CodeAddresses.GetOrCreateAddress(0x0000_2000_0000_3000UL, managedMethodIndex: -1);
        var codeAddrNativeLib = process.CodeAddresses.GetOrCreateAddress(0x0000_3000_0000_1000UL, managedMethodIndex: -1);
        // Belongs to no module and no managed method -> ConvertUnknownAddress.
        var codeAddrUnknown = process.CodeAddresses.GetOrCreateAddress(0xFFFF_FFFF_FFFF_0000UL, managedMethodIndex: -1);

        // Call stacks, leaf-first. DoWorkA/DoWorkB/CoreClr/ClrJit/NativeLib all share the "Main" prefix,
        // exercising stack-table prefix interning; Unknown is a single-frame stack directly under the root.
        var stackDoWorkA = process.CallStacks.InsertCallStack([codeAddrDoWorkA, codeAddrMain]);
        var stackDoWorkB = process.CallStacks.InsertCallStack([codeAddrDoWorkB, codeAddrMain]);
        var stackCoreClr = process.CallStacks.InsertCallStack([codeAddrCoreClr, codeAddrMain]);
        var stackClrJit = process.CallStacks.InsertCallStack([codeAddrClrJit, codeAddrMain]);
        var stackNativeLib = process.CallStacks.InsertCallStack([codeAddrNativeLib, codeAddrMain]);
        var stackUnknown = process.CallStacks.InsertCallStack([codeAddrUnknown]);
        // The reserved root call stack (index 0): samples pointing at it must be skipped by the converter.
        var rootStack = (UCallStackIndex)0;

        // Thread A: heaviest CPU consumer (50ms), above the visibility threshold, will end up selected.
        // Also has an explicit StopTime (exercises the "explicit stop time" branch).
        var threadA = process.Threads.GetOrCreateThread(1001);
        threadA.Name = "WorkerA";
        threadA.StartTime = UTimeSpan.FromMilliseconds(0);
        threadA.StopTime = UTimeSpan.FromMilliseconds(900);
        threadA.Samples.Add(new UTraceSample(stackDoWorkA, UTimeSpan.FromMilliseconds(1), UTimeSpan.FromMilliseconds(10)));
        threadA.Samples.Add(new UTraceSample(stackDoWorkA, UTimeSpan.FromMilliseconds(2), UTimeSpan.FromMilliseconds(10)));
        threadA.Samples.Add(new UTraceSample(stackDoWorkB, UTimeSpan.FromMilliseconds(3), UTimeSpan.FromMilliseconds(10)));
        threadA.Samples.Add(new UTraceSample(rootStack, UTimeSpan.FromMilliseconds(4), UTimeSpan.FromMilliseconds(0)));
        threadA.Samples.Add(new UTraceSample(stackCoreClr, UTimeSpan.FromMilliseconds(5), UTimeSpan.FromMilliseconds(10)));
        threadA.Samples.Add(new UTraceSample(stackDoWorkA, UTimeSpan.FromMilliseconds(6), UTimeSpan.FromMilliseconds(10)));

        threadA.Markers.Add(new JitCompileTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(1),
            Duration = UTimeSpan.FromMilliseconds(2),
            FullName = "MyApp.Worker.DoWork",
            MethodNamespace = "MyApp",
            MethodName = "Worker.DoWork",
            MethodSignature = "()",
            MethodILSize = 42,
        });
        threadA.Markers.Add(new GCTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(3),
            Duration = UTimeSpan.FromMilliseconds(1),
            Reason = "AllocSmall",
            Count = 1,
            Depth = 0,
            GCType = "NonConcurrent",
        });
        threadA.Markers.Add(new GCSuspendExecutionEngineTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(3),
            Duration = UTimeSpan.FromMilliseconds(0.5),
            Reason = "SuspendForGC",
            Count = 1,
        });
        threadA.Markers.Add(new GCRestartExecutionEngineTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(4),
        });
        threadA.Markers.Add(new GCAllocationTickTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(4.5),
            AllocationAmount = 1024,
            AllocationKind = "Small",
            TypeName = "System.String",
            HeapIndex = 0,
        });
        // Processed first (thread with the most CPU time), but with a LATER timestamp than thread B's
        // GCHeapStats marker below: exercises that the final list is actually sorted by time, not by
        // thread-processing order.
        threadA.Markers.Add(new GCHeapStatsTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(5),
            TotalHeapSize = 5000,
            TotalPromoted = 100,
            GenerationSize0 = 1000,
            TotalPromotedSize0 = 10,
            GenerationSize1 = 2000,
            TotalPromotedSize1 = 20,
            GenerationSize2 = 3000,
            TotalPromotedSize2 = 30,
            GenerationSize3 = 4000,
            TotalPromotedSize3 = 40,
            GenerationSize4 = 0,
            TotalPromotedSize4 = 0,
            FinalizationPromotedSize = 0,
            FinalizationPromotedCount = 0,
            PinnedObjectCount = 0,
            SinkBlockCount = 0,
            GCHandleCount = 10,
        });

        // Thread B: above the visibility threshold, but not the max-CPU thread (20ms). Left with a default
        // StopTime (exercises the "fallback to session duration" branch).
        var threadB = process.Threads.GetOrCreateThread(1002);
        threadB.Name = "WorkerB";
        threadB.StartTime = UTimeSpan.FromMilliseconds(0);
        threadB.Samples.Add(new UTraceSample(stackClrJit, UTimeSpan.FromMilliseconds(1), UTimeSpan.FromMilliseconds(10)));
        threadB.Samples.Add(new UTraceSample(stackUnknown, UTimeSpan.FromMilliseconds(2), UTimeSpan.FromMilliseconds(10)));
        threadB.Markers.Add(new GCHeapStatsTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(2),
            TotalHeapSize = 2000,
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

        // Thread C: below the visibility threshold and not the max-CPU thread (2ms) -> stays invisible.
        // Left with an empty Name (exercises the "Thread ({id})" fallback naming branch).
        var threadC = process.Threads.GetOrCreateThread(1003);
        threadC.StartTime = UTimeSpan.FromMilliseconds(0);
        threadC.Samples.Add(new UTraceSample(stackNativeLib, UTimeSpan.FromMilliseconds(1), UTimeSpan.FromMilliseconds(2)));

        return session;
    }

    private static int AddManagedMethod(UTraceProcess process, long moduleId, long methodId, string methodNamespace, string methodName, ulong startAddress)
    {
        var methodIndex = process.ManagedMethods.Count;
        process.ManagedMethods.GetOrCreateManagedMethod(
            threadID: 0,
            moduleID: moduleId,
            methodID: methodId,
            methodNamespace: methodNamespace,
            methodName: methodName,
            methodSignature: "()",
            methodToken: 0,
            methodFlags: MethodFlags.Jitted,
            methodStartAddress: startAddress,
            methodSize: 0x100UL,
            loadTime: UTimeSpan.FromMilliseconds(0));
        return methodIndex;
    }
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Tests that <see cref="UltraConverterToFirefoxEventPipe"/> categorizes a native frame as
/// <see cref="UltraConverterToFirefox.CategoryGc"/> when its resolved symbol identifies it as belonging to
/// a garbage collector - CoreCLR's workstation/server GC or Mono's SGen collector - and leaves other native
/// frames alone.
/// </summary>
[TestClass]
public class UltraConverterToFirefoxEventPipeGcCategoryTests
{
    private const int ProcessId = 42;
    private const ulong ModuleBase = 0x0000_3000_0000_0000UL;

    [TestMethod]
    public void MonoSGenSymbolsAreCategorizedAsGc() =>
        // Unlike CoreCLR's WKS::/SVR:: namespaces, sgen_* is recognized regardless of which module it
        // resolves in - Mono's runtime is never CoreCLR's module - so this uses an unrelated module name.
        AssertSymbolCategory("libmonosgen.dylib", "sgen_major_collector_scan_object", "sgen_major_collector_scan_object", UltraConverterToFirefox.CategoryGc);

    [TestMethod]
    public void CoreClrWorkstationGcSymbolsAreCategorizedAsGc() =>
        AssertSymbolCategory("libcoreclr.dylib", "__ZN3WKS7gc_heap15garbage_collectEi", "WKS", UltraConverterToFirefox.CategoryGc);

    [TestMethod]
    public void CoreClrServerGcSymbolsAreCategorizedAsGc() =>
        AssertSymbolCategory("libcoreclr.dylib", "__ZN3SVR7gc_heap15garbage_collectEi", "SVR", UltraConverterToFirefox.CategoryGc);

    [TestMethod]
    public void UnrelatedNativeSymbolsAreNotCategorizedAsGc() =>
        AssertSymbolCategory("libtest.dylib", "_NativeWork", "NativeWork", UltraConverterToFirefox.CategoryNative);

    private static void AssertSymbolCategory(string moduleFileName, string exportedSymbolName, string resolvedNameContains, int expectedCategory)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("UltraGcCategoryTest_");
        try
        {
            var modulePath = Path.Combine(tempDirectory.FullName, moduleFileName);
            MachOTestFixtures.WriteMinimalMachO(modulePath, ModuleBase, [(exportedSymbolName, ModuleBase)]);

            var session = new UTraceSession
            {
                NumberOfProcessors = 1,
                CpuSpeedMHz = 3000,
                StartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Duration = UTimeSpan.FromMilliseconds(10),
            };

            var process = new UTraceProcess
            {
                ProcessID = ProcessId,
                FilePath = "testapp.exe",
                RuntimeIdentifier = "osx-arm64",
                OSDescription = "macOS 14.5 (Test Fixture)",
            };
            session.Processes.Add(process);

            process.Modules.GetOrCreateNativeModule(ModuleBase, 0x1000UL, modulePath);

            var thread = process.Threads.GetOrCreateThread(1);
            thread.Name = "Worker";
            thread.StartTime = UTimeSpan.FromMilliseconds(0);

            var codeAddr = process.CodeAddresses.GetOrCreateAddress(ModuleBase, managedMethodIndex: -1);
            var stack = process.CallStacks.InsertCallStack([codeAddr]);
            thread.Samples.Add(new UTraceSample(stack, UTimeSpan.FromMilliseconds(1), UTimeSpan.FromMilliseconds(1)));

            var options = new UltraProfilerOptions
            {
                CpuSamplingIntervalInMs = 1.0f,
                MaxDegreeOfParallelism = 1,
            };

            using var converter = new UltraConverterToFirefoxEventPipe(options);
            var profile = converter.ConvertSessionForTests(session, new List<int> { process.ProcessID });

            var profileThread = profile.Threads.Single();
            var funcIndex = profileThread.FuncTable.Name.FindIndex(nameIndex => profileThread.StringArray[nameIndex].Contains(resolvedNameContains, StringComparison.Ordinal));
            Assert.AreNotEqual(-1, funcIndex, $"No func found containing `{resolvedNameContains}`");

            var frameIndex = profileThread.FrameTable.Func.IndexOf(funcIndex);
            Assert.AreNotEqual(-1, frameIndex, "No frame found referencing the resolved func");

            Assert.AreEqual(expectedCategory, profileThread.FrameTable.Category[frameIndex]);
        }
        finally
        {
            Directory.Delete(tempDirectory.FullName, recursive: true);
        }
    }
}

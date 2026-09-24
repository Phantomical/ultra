// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Tests that <see cref="UltraConverterToFirefoxEventPipe"/> categorizes a native frame as
/// <see cref="UltraConverterToFirefox.CategoryGc"/> when its resolved symbol identifies it as belonging to
/// a garbage collector - CoreCLR's workstation/server GC or Mono's SGen collector - leaves other native
/// frames alone, and propagates the categorization to the entire callee subtree of a GC-subtree root function
/// (an SGen collection/worker entry point, Unity's unused-asset collection) even where a callee's own symbol
/// carries no <c>sgen_</c> name.
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

    [TestMethod]
    public void DescendantsOfSgenThreadFuncAreCategorizedAsGc() =>
        AssertDescendantOfGcRootIsCategorizedAsGc("sgen_thread_func");

    [TestMethod]
    public void DescendantsOfSgenPerformCollectionInnerAreCategorizedAsGc() =>
        AssertDescendantOfGcRootIsCategorizedAsGc("sgen_perform_collection_inner");

    /// <summary>
    /// Mono builds that inline <c>sgen_perform_collection_inner</c> resolve the collection under the outer
    /// routine's name instead.
    /// </summary>
    [TestMethod]
    public void DescendantsOfSgenPerformCollectionAreCategorizedAsGc() =>
        AssertDescendantOfGcRootIsCategorizedAsGc("sgen_perform_collection");

    [TestMethod]
    public void DescendantsOfSgenGcCollectAreCategorizedAsGc() =>
        AssertDescendantOfGcRootIsCategorizedAsGc("sgen_gc_collect");

    /// <summary>
    /// Unity's <c>Resources.UnloadUnusedAssets</c> walks the managed heap and destroys the assets it no longer
    /// finds referenced - collection work with no <c>sgen_</c> symbol anywhere under it.
    /// </summary>
    [TestMethod]
    public void DescendantsOfUnityGarbageCollectSharedAssetsAreCategorizedAsGc() =>
        AssertDescendantOfGcRootIsCategorizedAsGc("GarbageCollectSharedAssets");

    /// <summary>
    /// Only the collection routines are subtree roots: an <c>sgen_</c> function of its own is GC, but its
    /// callees are not attributed to GC on its account (e.g. the copy behind a write barrier).
    /// </summary>
    [TestMethod]
    public void DescendantsOfOtherSgenFunctionsAreNotCategorizedAsGc() =>
        AssertDescendantOfNonRootIsNotCategorizedAsGc("sgen_card_table_wbarrier_value_copy");

    /// <summary>
    /// Builds a module containing <paramref name="rootFunctionName"/> plus an unrelated helper symbol, then
    /// samples the helper three times: twice called from under <paramref name="rootFunctionName"/> (directly,
    /// and recursively) and once on its own. Only the first two call paths should be categorized as GC -
    /// confirming the propagation is scoped to the call stack it actually occurs on, not to every occurrence
    /// of that code address. The Firefox Profiler derives a stack's category from its frame alone, and frames
    /// are cached per code address (see <c>ConvertFrame</c>), so the GC call paths must get a GC-categorized
    /// clone of the helper's frame (shared between them, and sharing the plain frame's func) rather than
    /// either a category written only on the stack node or a mutation of the shared, cached frame.
    /// </summary>
    private static void AssertDescendantOfGcRootIsCategorizedAsGc(string rootFunctionName)
    {
        var profileThread = ConvertHelperSampledUnderAndWithout(rootFunctionName);

        var underRootStackIndex = profileThread.Samples.Stack[0]!.Value;
        var underRootRecursiveStackIndex = profileThread.Samples.Stack[1]!.Value;
        var withoutRootStackIndex = profileThread.Samples.Stack[2]!.Value;

        Assert.AreEqual(UltraConverterToFirefox.CategoryGc, profileThread.StackTable.Category[underRootStackIndex]);
        Assert.AreEqual(UltraConverterToFirefox.CategoryGc, profileThread.StackTable.Category[underRootRecursiveStackIndex]);
        Assert.AreEqual(UltraConverterToFirefox.CategoryNative, profileThread.StackTable.Category[withoutRootStackIndex]);

        var frameTable = profileThread.FrameTable;
        var underRootFrameIndex = profileThread.StackTable.Frame[underRootStackIndex];
        var underRootRecursiveFrameIndex = profileThread.StackTable.Frame[underRootRecursiveStackIndex];
        var withoutRootFrameIndex = profileThread.StackTable.Frame[withoutRootStackIndex];

        Assert.AreEqual(UltraConverterToFirefox.CategoryGc, frameTable.Category[underRootFrameIndex]);
        Assert.AreEqual(UltraConverterToFirefox.CategoryNative, frameTable.Category[withoutRootFrameIndex]);
        Assert.AreNotEqual(withoutRootFrameIndex, underRootFrameIndex);
        Assert.AreEqual(underRootFrameIndex, underRootRecursiveFrameIndex);
        Assert.AreEqual(frameTable.Func[withoutRootFrameIndex], frameTable.Func[underRootFrameIndex]);
        Assert.AreEqual(frameTable.Address[withoutRootFrameIndex], frameTable.Address[underRootFrameIndex]);
    }

    private static void AssertDescendantOfNonRootIsNotCategorizedAsGc(string functionName)
    {
        var profileThread = ConvertHelperSampledUnderAndWithout(functionName);

        var underFunctionStackIndex = profileThread.Samples.Stack[0]!.Value;
        var withoutFunctionStackIndex = profileThread.Samples.Stack[2]!.Value;

        // The helper keeps its own category under the function, and shares one frame with its stand-alone call path.
        Assert.AreEqual(UltraConverterToFirefox.CategoryNative, profileThread.StackTable.Category[underFunctionStackIndex]);
        Assert.AreEqual(profileThread.StackTable.Frame[withoutFunctionStackIndex], profileThread.StackTable.Frame[underFunctionStackIndex]);

        // The function itself is still GC when its own symbol says so.
        var functionStackIndex = profileThread.StackTable.Prefix[underFunctionStackIndex]!.Value;
        var expectedFunctionCategory = functionName.StartsWith("sgen_", StringComparison.Ordinal) ? UltraConverterToFirefox.CategoryGc : UltraConverterToFirefox.CategoryNative;
        Assert.AreEqual(expectedFunctionCategory, profileThread.StackTable.Category[functionStackIndex]);
    }

    /// <summary>
    /// Builds a module containing <paramref name="rootFunctionName"/> plus an unrelated helper symbol, then
    /// samples the helper three times: twice called from under <paramref name="rootFunctionName"/> (directly,
    /// and recursively) and once on its own, and converts the session. Only the first two call paths should
    /// be categorized as GC when <paramref name="rootFunctionName"/> is a GC-subtree root - confirming the
    /// propagation is scoped to the call stack it actually occurs on, not to every occurrence of that code
    /// address. The Firefox Profiler derives a stack's category from its frame alone, and frames are cached
    /// per code address (see <c>ConvertFrame</c>), so the GC call paths must get a GC-categorized clone of the
    /// helper's frame (shared between them, and sharing the plain frame's func) rather than either a
    /// category written only on the stack node or a mutation of the shared, cached frame.
    /// </summary>
    private static FirefoxProfiler.Thread ConvertHelperSampledUnderAndWithout(string rootFunctionName)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("UltraGcPropagationTest_");
        try
        {
            const ulong rootOffset = 0;
            const ulong helperOffset = 0x100;

            var modulePath = Path.Combine(tempDirectory.FullName, "libmonosgen.dylib");
            MachOTestFixtures.WriteMinimalMachO(modulePath, ModuleBase,
            [
                (rootFunctionName, ModuleBase + rootOffset),
                ("shared_helper", ModuleBase + helperOffset),
            ]);

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
            thread.Name = "SgenWorker";
            thread.StartTime = UTimeSpan.FromMilliseconds(0);

            var rootAddr = process.CodeAddresses.GetOrCreateAddress(ModuleBase + rootOffset, managedMethodIndex: -1);
            var helperAddr = process.CodeAddresses.GetOrCreateAddress(ModuleBase + helperOffset, managedMethodIndex: -1);

            // InsertCallStack takes the leaf frame first and the outermost/root frame last.
            var stackUnderRoot = process.CallStacks.InsertCallStack([helperAddr, rootAddr]);
            var stackUnderRootRecursive = process.CallStacks.InsertCallStack([helperAddr, helperAddr, rootAddr]);
            var stackWithoutRoot = process.CallStacks.InsertCallStack([helperAddr]);

            thread.Samples.Add(new UTraceSample(stackUnderRoot, UTimeSpan.FromMilliseconds(1), UTimeSpan.FromMilliseconds(1)));
            thread.Samples.Add(new UTraceSample(stackUnderRootRecursive, UTimeSpan.FromMilliseconds(2), UTimeSpan.FromMilliseconds(1)));
            thread.Samples.Add(new UTraceSample(stackWithoutRoot, UTimeSpan.FromMilliseconds(3), UTimeSpan.FromMilliseconds(1)));

            var options = new UltraProfilerOptions
            {
                CpuSamplingIntervalInMs = 1.0f,
                MaxDegreeOfParallelism = 1,
            };

            using var converter = new UltraConverterToFirefoxEventPipe(options);
            var profile = converter.ConvertSessionForTests(session, new List<int> { process.ProcessID });

            return profile.Threads.Single();
        }
        finally
        {
            Directory.Delete(tempDirectory.FullName, recursive: true);
        }
    }

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

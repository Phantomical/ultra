// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="ThreadVisibilityPlanner"/>, the pure helper extracted from
/// <c>UltraConverterToFirefoxEtw</c> that decides (a) the CPU-descending thread order, (b) each thread's
/// assigned profile-thread index, (c) the contents and order of the default-visible-threads list, and (d)
/// the default-selected-thread (max-CPU) fallback. These are exactly the outputs most likely to silently
/// reorder JSON arrays in the serialized profile, and they need no <c>TraceLog</c> to test.
/// </summary>
[TestClass]
public class ThreadVisibilityPlannerTests
{
    [TestMethod]
    public void ThreadsAboveAndBelowThresholdAreOrderedAndPartiallyVisible()
    {
        // Original (pre-sort) order deliberately not CPU-descending.
        var cpuTimesInMs = new double[] { 5.0, 50.0, 20.0, 1.0 };

        var plan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, baseProfileThreadIndex: 0, minimumCpuTimeBeforeThreadIsVisibleInMs: 10.0);

        // CPU-descending order of original indices: 1 (50), 2 (20), 0 (5), 3 (1).
        CollectionAssert.AreEqual(new[] { 1, 2, 0, 3 }, plan.SortOrder);

        // Profile-thread index is base + position in the sorted order.
        CollectionAssert.AreEqual(new[] { 2, 0, 1, 3 }, plan.ProfileThreadIndexByOriginalIndex);

        // Only threads strictly above the threshold (50 and 20) are visible, in CPU-descending order.
        CollectionAssert.AreEqual(new[] { 0, 1 }, plan.InitialVisibleThreads);

        // The max-CPU thread (original index 1, profile-thread index 0) is selected.
        Assert.AreEqual(0, plan.ThreadIndexWithMaxCpuTime);
        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialSelectedThreads);
    }

    [TestMethod]
    public void AllThreadsBelowThresholdStillMakeTopThreadVisible()
    {
        var cpuTimesInMs = new double[] { 1.0, 4.0, 2.0 };

        var plan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, baseProfileThreadIndex: 0, minimumCpuTimeBeforeThreadIsVisibleInMs: 10.0);

        // None of the threads are above the threshold, so the per-thread loop contributes nothing...
        // ...but the top-CPU thread (original index 1, profile-thread index 0) is still added as a fallback.
        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialVisibleThreads);
        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialSelectedThreads);
        Assert.AreEqual(0, plan.ThreadIndexWithMaxCpuTime);
    }

    [TestMethod]
    public void SingleThreadAboveThresholdIsVisibleOnceNotDuplicated()
    {
        var cpuTimesInMs = new double[] { 42.0 };

        var plan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, baseProfileThreadIndex: 0, minimumCpuTimeBeforeThreadIsVisibleInMs: 10.0);

        CollectionAssert.AreEqual(new[] { 0 }, plan.SortOrder);
        CollectionAssert.AreEqual(new[] { 0 }, plan.ProfileThreadIndexByOriginalIndex);

        // Above-threshold in the per-thread loop AND the max-CPU thread: must appear exactly once.
        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialVisibleThreads);
        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialSelectedThreads);
        Assert.AreEqual(0, plan.ThreadIndexWithMaxCpuTime);
    }

    [TestMethod]
    public void SingleThreadBelowThresholdIsStillMadeVisibleByFallback()
    {
        var cpuTimesInMs = new double[] { 0.5 };

        var plan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, baseProfileThreadIndex: 0, minimumCpuTimeBeforeThreadIsVisibleInMs: 10.0);

        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialVisibleThreads);
        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialSelectedThreads);
    }

    [TestMethod]
    public void ZeroThreadsProduceEmptyPlanAndNoMaxCpuThread()
    {
        var plan = ThreadVisibilityPlanner.Plan(Array.Empty<double>(), baseProfileThreadIndex: 0, minimumCpuTimeBeforeThreadIsVisibleInMs: 10.0);

        Assert.AreEqual(0, plan.SortOrder.Length);
        Assert.AreEqual(0, plan.ProfileThreadIndexByOriginalIndex.Length);
        Assert.AreEqual(0, plan.InitialVisibleThreads.Count);
        Assert.AreEqual(0, plan.InitialSelectedThreads.Count);
        Assert.AreEqual(-1, plan.ThreadIndexWithMaxCpuTime);
    }

    [TestMethod]
    public void NonZeroBaseProfileThreadIndexOffsetsEveryResult()
    {
        // Simulates the multi-process case: this process's threads land after another process's threads
        // already occupy profile-thread indices [0, 5).
        const int baseProfileThreadIndex = 5;
        var cpuTimesInMs = new double[] { 1.0, 30.0, 15.0 };

        var plan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, baseProfileThreadIndex, minimumCpuTimeBeforeThreadIsVisibleInMs: 10.0);

        // CPU-descending order of original indices: 1 (30), 2 (15), 0 (1).
        CollectionAssert.AreEqual(new[] { 1, 2, 0 }, plan.SortOrder);
        CollectionAssert.AreEqual(new[] { 7, 5, 6 }, plan.ProfileThreadIndexByOriginalIndex);

        CollectionAssert.AreEqual(new[] { 5, 6 }, plan.InitialVisibleThreads);
        CollectionAssert.AreEqual(new[] { 5 }, plan.InitialSelectedThreads);
        Assert.AreEqual(5, plan.ThreadIndexWithMaxCpuTime);
    }

    [TestMethod]
    public void ThreadAtExactThresholdIsNotVisibleViaTheLoop()
    {
        // The comparison is strictly-greater-than, matching the pre-existing per-thread visibility check.
        var cpuTimesInMs = new double[] { 10.0, 20.0 };

        var plan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, baseProfileThreadIndex: 0, minimumCpuTimeBeforeThreadIsVisibleInMs: 10.0);

        // Only the 20ms thread (profile-thread index 0) qualifies via the loop; the 10ms thread
        // (profile-thread index 1) is exactly at the threshold and is not the max-CPU thread either.
        CollectionAssert.AreEqual(new[] { 0 }, plan.InitialVisibleThreads);
    }
}

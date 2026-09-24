// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="GcCollectionTracker{TThread,TPayload}"/>, the pure helper both converters use to pair
/// GCStart/GCEnd across the threads of a process and to derive the concurrent phase of a background
/// collection from the stop-the-world pauses around it. The event sequences below are the ones Mono's SGen
/// collector and CoreCLR actually produce; a per-thread pairing loses every collection that ends on another
/// thread than it started on, and nothing else in the profile reveals that loss.
/// </summary>
[TestClass]
public class GcCollectionTrackerTests
{
    private sealed class FakeThread(string name)
    {
        public string Name { get; } = name;
    }

    [TestMethod]
    public void NurseryCollectionInsideOnePauseHasNoConcurrentPhase()
    {
        var tracker = new GcCollectionTracker<FakeThread, string>();
        var thread = new FakeThread("main");

        // Suspend, collect, restart - all on the same thread, all inside one pause.
        tracker.OnSuspendEEStart(10.0);
        tracker.OnGcStart(thread, count: 1370, depth: 0, timeInMs: 12.0, payload: "nursery");
        var collection = tracker.OnGcEnd(count: 1370, depth: 0, timeInMs: 17.0);
        tracker.OnRestartEEStop(18.0);

        Assert.IsNotNull(collection);
        Assert.AreSame(thread, collection.Value.Thread);
        Assert.AreEqual("nursery", collection.Value.Payload);
        Assert.AreEqual(12.0, collection.Value.StartTimeInMs);
        Assert.AreEqual(17.0, collection.Value.EndTimeInMs);
        Assert.IsFalse(collection.Value.HasConcurrentPhase);
        Assert.IsNull(collection.Value.ConcurrentStartTimeInMs);
        Assert.IsNull(collection.Value.ConcurrentEndTimeInMs);
    }

    [TestMethod]
    public void ConcurrentMajorCollectionEndingOnAnotherThreadIsPairedAndReportedOnItsStartingThread()
    {
        // Mono SGen: the thread whose allocation triggers a concurrent major collection runs its initial
        // pause - a nursery collection followed by the major GCStart - then restarts the world. Other threads
        // run nursery collections in their own pauses while the collector marks concurrently. The final pause
        // runs on whichever thread triggers the next collection, and raises the major GCEnd there.
        var tracker = new GcCollectionTracker<FakeThread, string>();
        var starter = new FakeThread("thread 15");
        var other = new FakeThread("thread 0");
        var finisher = new FakeThread("thread 14");

        // Initial pause on the starting thread.
        tracker.OnSuspendEEStart(100.0);
        tracker.OnGcStart(starter, count: 1365, depth: 0, timeInMs: 102.0, payload: "nursery 1365");
        var nurseryInInitialPause = tracker.OnGcEnd(count: 1365, depth: 0, timeInMs: 104.0);
        tracker.OnGcStart(starter, count: 56, depth: 1, timeInMs: 105.0, payload: "major 56");
        tracker.OnRestartEEStop(160.0);

        // A nursery collection on another thread during the concurrent phase.
        tracker.OnSuspendEEStart(300.0);
        tracker.OnGcStart(other, count: 1366, depth: 0, timeInMs: 302.0, payload: "nursery 1366");
        var nurseryDuringConcurrentPhase = tracker.OnGcEnd(count: 1366, depth: 0, timeInMs: 306.0);
        tracker.OnRestartEEStop(307.0);

        // Final pause on a third thread.
        tracker.OnSuspendEEStart(900.0);
        var major = tracker.OnGcEnd(count: 56, depth: 1, timeInMs: 950.0);
        tracker.OnRestartEEStop(1000.0);

        Assert.IsNotNull(nurseryInInitialPause);
        Assert.IsFalse(nurseryInInitialPause.Value.HasConcurrentPhase);
        Assert.IsNotNull(nurseryDuringConcurrentPhase);
        Assert.IsFalse(nurseryDuringConcurrentPhase.Value.HasConcurrentPhase);
        Assert.AreSame(other, nurseryDuringConcurrentPhase.Value.Thread);

        Assert.IsNotNull(major);
        Assert.AreSame(starter, major.Value.Thread);
        Assert.AreEqual("major 56", major.Value.Payload);
        Assert.AreEqual(105.0, major.Value.StartTimeInMs);
        Assert.AreEqual(950.0, major.Value.EndTimeInMs);
        Assert.IsTrue(major.Value.HasConcurrentPhase);
        // From the restart closing the initial pause to the suspend opening the final pause - the nursery
        // pause in between is part of the concurrent phase, not its end.
        Assert.AreEqual(160.0, major.Value.ConcurrentStartTimeInMs);
        Assert.AreEqual(900.0, major.Value.ConcurrentEndTimeInMs);
    }

    [TestMethod]
    public void BackgroundCollectionEndingAfterItsFinalPauseEndsItsConcurrentPhaseAtThatPause()
    {
        // CoreCLR raises a background GC's GCEnd from the background GC thread once its concurrent sweep is
        // done, i.e. after the final pause has already restarted the world.
        var tracker = new GcCollectionTracker<FakeThread, string>();
        var trigger = new FakeThread("user thread");

        tracker.OnSuspendEEStart(10.0);
        tracker.OnGcStart(trigger, count: 7, depth: 2, timeInMs: 11.0, payload: "bgc");
        tracker.OnRestartEEStop(15.0);

        tracker.OnSuspendEEStart(80.0);
        tracker.OnRestartEEStop(85.0);

        var collection = tracker.OnGcEnd(count: 7, depth: 2, timeInMs: 120.0);

        Assert.IsNotNull(collection);
        Assert.IsTrue(collection.Value.HasConcurrentPhase);
        Assert.AreEqual(15.0, collection.Value.ConcurrentStartTimeInMs);
        Assert.AreEqual(80.0, collection.Value.ConcurrentEndTimeInMs);
        Assert.AreEqual(120.0, collection.Value.EndTimeInMs);
    }

    [TestMethod]
    public void CollectionEndingAfterARestartWithNoFurtherPauseEndsItsConcurrentPhaseAtTheGcEnd()
    {
        var tracker = new GcCollectionTracker<FakeThread, string>();
        var thread = new FakeThread("main");

        tracker.OnSuspendEEStart(10.0);
        tracker.OnGcStart(thread, count: 3, depth: 1, timeInMs: 11.0, payload: "major");
        tracker.OnRestartEEStop(15.0);

        var collection = tracker.OnGcEnd(count: 3, depth: 1, timeInMs: 40.0);

        Assert.IsNotNull(collection);
        Assert.IsTrue(collection.Value.HasConcurrentPhase);
        Assert.AreEqual(15.0, collection.Value.ConcurrentStartTimeInMs);
        Assert.AreEqual(40.0, collection.Value.ConcurrentEndTimeInMs);
    }

    [TestMethod]
    public void GcEndWithoutAPendingGcStartIsIgnored()
    {
        // A trace that starts mid-collection sees the GCEnd alone.
        var tracker = new GcCollectionTracker<FakeThread, string>();

        tracker.OnSuspendEEStart(10.0);
        Assert.IsNull(tracker.OnGcEnd(count: 55, depth: 1, timeInMs: 12.0));
        tracker.OnRestartEEStop(14.0);

        // And does not disturb the pairing of what follows.
        var thread = new FakeThread("main");
        tracker.OnSuspendEEStart(20.0);
        tracker.OnGcStart(thread, count: 56, depth: 1, timeInMs: 21.0, payload: "major 56");
        var collection = tracker.OnGcEnd(count: 56, depth: 1, timeInMs: 25.0);
        tracker.OnRestartEEStop(26.0);

        Assert.IsNotNull(collection);
        Assert.AreEqual("major 56", collection.Value.Payload);
        Assert.IsFalse(collection.Value.HasConcurrentPhase);
    }

    [TestMethod]
    public void SameCountAtDifferentDepthsAreDistinctCollections()
    {
        // Mono's profiler counts collections per generation, so a nursery and a major collection can share
        // a count while the major one is still pending.
        var tracker = new GcCollectionTracker<FakeThread, string>();
        var thread = new FakeThread("main");

        tracker.OnSuspendEEStart(10.0);
        tracker.OnGcStart(thread, count: 5, depth: 1, timeInMs: 11.0, payload: "major 5");
        tracker.OnRestartEEStop(12.0);

        tracker.OnSuspendEEStart(20.0);
        tracker.OnGcStart(thread, count: 5, depth: 0, timeInMs: 21.0, payload: "nursery 5");
        var nursery = tracker.OnGcEnd(count: 5, depth: 0, timeInMs: 22.0);
        tracker.OnRestartEEStop(23.0);

        tracker.OnSuspendEEStart(30.0);
        var major = tracker.OnGcEnd(count: 5, depth: 1, timeInMs: 31.0);

        Assert.IsNotNull(nursery);
        Assert.AreEqual("nursery 5", nursery.Value.Payload);
        Assert.IsFalse(nursery.Value.HasConcurrentPhase);

        Assert.IsNotNull(major);
        Assert.AreEqual("major 5", major.Value.Payload);
        Assert.AreEqual(11.0, major.Value.StartTimeInMs);
        Assert.AreEqual(12.0, major.Value.ConcurrentStartTimeInMs);
        Assert.AreEqual(30.0, major.Value.ConcurrentEndTimeInMs);
    }

    [TestMethod]
    public void RepeatedGcStartReplacesTheStaleCollection()
    {
        // The GCEnd of the first collection was lost; the runtime never overlaps two collections of the same
        // (count, depth), so the later GCStart is the one the GCEnd belongs to.
        var tracker = new GcCollectionTracker<FakeThread, string>();
        var first = new FakeThread("first");
        var second = new FakeThread("second");

        tracker.OnGcStart(first, count: 9, depth: 0, timeInMs: 1.0, payload: "stale");
        tracker.OnGcStart(second, count: 9, depth: 0, timeInMs: 5.0, payload: "current");
        var collection = tracker.OnGcEnd(count: 9, depth: 0, timeInMs: 6.0);

        Assert.IsNotNull(collection);
        Assert.AreSame(second, collection.Value.Thread);
        Assert.AreEqual("current", collection.Value.Payload);
        Assert.AreEqual(5.0, collection.Value.StartTimeInMs);
        Assert.IsNull(tracker.OnGcEnd(count: 9, depth: 0, timeInMs: 7.0));
    }
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for the process/thread history of <see cref="EtlTraceModel"/>, which mirrors <c>TraceLog</c>'s
/// <c>TraceProcesses</c>/<c>TraceThreads</c> (ids are reused by the OS, so one id can have several records), and
/// for its time conversions and <see cref="EtlProcess.Name"/>.
/// </summary>
[TestClass]
public class EtlTraceModelTests
{
    // --- Processes ---

    [TestMethod]
    public void GetOrCreateProcessCreatesOnce()
    {
        var model = new EtlTraceModel();

        var a = model.GetOrCreateProcess(100, 50);
        var b = model.GetOrCreateProcess(100, 60);

        Assert.AreSame(a, b);
        Assert.HasCount(1, model.Processes);
        Assert.AreEqual(0, a.Index);
        Assert.AreEqual(50, a.FirstEventSeenQPC);
        Assert.IsNull(a.Previous);
    }

    [TestMethod]
    public void FindProcessReturnsNullForUnknownPid()
    {
        var model = new EtlTraceModel();
        model.GetOrCreateProcess(100, 50);

        Assert.IsNull(model.FindProcess(200, 50));
        Assert.IsNull(model.LastProcessWithId(200));
    }

    [TestMethod]
    public void StartEventFillsInLazilyCreatedProcess()
    {
        var model = new EtlTraceModel();
        var lazy = model.GetOrCreateProcess(100, 50);

        var started = model.GetOrCreateProcess(100, 80, isProcessStartEvent: true);

        Assert.AreSame(lazy, started);
        Assert.HasCount(1, model.Processes);
    }

    [TestMethod]
    public void StartEventOnAlreadyStartedProcessCreatesNewIncarnation()
    {
        var model = new EtlTraceModel();
        var first = model.GetOrCreateProcess(100, 50, isProcessStartEvent: true);
        first.StartTimeQPC = 50;

        var second = model.GetOrCreateProcess(100, 500, isProcessStartEvent: true);
        second.StartTimeQPC = 500;

        Assert.AreNotSame(first, second);
        Assert.AreSame(first, second.Previous);
        Assert.AreEqual(1, second.Index);
        Assert.AreSame(second, model.LastProcessWithId(100));
    }

    [TestMethod]
    public void StartEventOnEndedProcessCreatesNewIncarnation()
    {
        var model = new EtlTraceModel();
        var first = model.GetOrCreateProcess(100, 50);
        first.EndTimeQPC = 400;

        var second = model.GetOrCreateProcess(100, 500, isProcessStartEvent: true);

        Assert.AreNotSame(first, second);
        Assert.AreSame(first, second.Previous);
    }

    [TestMethod]
    public void FindProcessReturnsIncarnationAliveAtTime()
    {
        var model = new EtlTraceModel();
        var first = model.GetOrCreateProcess(100, 50, isProcessStartEvent: true);
        first.StartTimeQPC = 50;
        first.EndTimeQPC = 400;
        var second = model.GetOrCreateProcess(100, 500, isProcessStartEvent: true);
        second.StartTimeQPC = 500;

        Assert.AreSame(first, model.FindProcess(100, 300));
        Assert.AreSame(first, model.FindProcess(100, 499));
        Assert.AreSame(second, model.FindProcess(100, 500));
        Assert.AreSame(second, model.FindProcess(100, 10_000));

        // A plain (non start) event of the old incarnation's time keeps landing on the old record.
        Assert.AreSame(first, model.GetOrCreateProcess(100, 300));
    }

    [TestMethod]
    public void IsTargetIsDecidedAtCreationFromTargetProcessIds()
    {
        var model = new EtlTraceModel();
        model.TargetProcessIds.Add(100);

        var target = model.GetOrCreateProcess(100, 50);
        var other = model.GetOrCreateProcess(200, 50);

        Assert.IsTrue(target.IsTarget);
        Assert.IsFalse(other.IsTarget);
    }

    [TestMethod]
    public void IsTargetIsNotRecomputedForExistingProcess()
    {
        var model = new EtlTraceModel();
        model.GetOrCreateProcess(100, 50);

        model.TargetProcessIds.Add(100);

        Assert.IsFalse(model.GetOrCreateProcess(100, 60).IsTarget);
    }

    [TestMethod]
    public void NewIncarnationOfTargetPidIsTarget()
    {
        var model = new EtlTraceModel();
        model.TargetProcessIds.Add(100);
        var first = model.GetOrCreateProcess(100, 50, isProcessStartEvent: true);
        first.StartTimeQPC = 50;

        var second = model.GetOrCreateProcess(100, 500, isProcessStartEvent: true);

        Assert.IsTrue(second.IsTarget);
    }

    [TestMethod]
    public void KernelProcessIsPidZeroAndStable()
    {
        var model = new EtlTraceModel();

        var kernel = model.KernelProcess;

        Assert.AreEqual(0, kernel.ProcessId);
        Assert.AreSame(kernel, model.KernelProcess);
        Assert.AreSame(kernel, model.FindProcess(0, 0));
    }

    // --- Threads ---

    [TestMethod]
    public void GetOrCreateThreadWithoutCreateEventLeavesStartTimeZero()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 50);

        var thread = model.GetOrCreateThread(7, 100, process);

        Assert.AreEqual(0, thread.StartTimeQPC);
        Assert.AreEqual(100, thread.HistoryStartQPC);
        Assert.AreSame(process, thread.Process);
        CollectionAssert.Contains(process.Threads, thread);
        Assert.AreSame(thread, model.Threads[thread.Index]);
    }

    [TestMethod]
    public void GetOrCreateThreadWithCreateEventSetsStartTime()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 50);

        var thread = model.GetOrCreateThread(7, 100, process, isThreadCreateEvent: true);

        Assert.AreEqual(100, thread.StartTimeQPC);
    }

    [TestMethod]
    public void GetOrCreateThreadReturnsExistingRecordForSameProcess()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 50);
        var thread = model.GetOrCreateThread(7, 100, process);

        Assert.AreSame(thread, model.GetOrCreateThread(7, 200, process));
        Assert.AreSame(thread, model.GetOrCreateThread(7, 300, null));
        Assert.HasCount(1, model.Threads);
        Assert.HasCount(1, process.Threads);
    }

    [TestMethod]
    public void FindThreadReturnsNullBeforeAnyRecord()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 50);
        model.GetOrCreateThread(7, 100, process);

        Assert.IsNull(model.FindThread(7, 99));
        Assert.IsNull(model.FindThread(8, 1000));
    }

    [TestMethod]
    public void FindThreadReturnsRecordFromHistoryStart()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 50);
        var thread = model.GetOrCreateThread(7, 100, process);

        Assert.AreSame(thread, model.FindThread(7, 100));
        Assert.AreSame(thread, model.FindThread(7, long.MaxValue));
    }

    [TestMethod]
    public void CreateEventForExistingTidCreatesNewRecord()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 50);
        var first = model.GetOrCreateThread(7, 100, process, isThreadCreateEvent: true);

        var second = model.GetOrCreateThread(7, 500, process, isThreadCreateEvent: true);

        Assert.AreNotSame(first, second);
        Assert.AreSame(first, second.Previous);
        Assert.AreEqual(500, second.StartTimeQPC);
        Assert.AreSame(first, model.FindThread(7, 300));
        Assert.AreSame(first, model.FindThread(7, 499));
        Assert.AreSame(second, model.FindThread(7, 500));
        Assert.AreSame(second, model.FindThread(7, 1000));
        CollectionAssert.AreEqual(new[] { first, second }, process.Threads);
    }

    [TestMethod]
    public void ThreadOfUnknownProcessIsRehomedToRealProcess()
    {
        var model = new EtlTraceModel();
        var thread = model.GetOrCreateThread(7, 100, null);
        var unknown = thread.Process;
        Assert.AreEqual(-1, unknown.ProcessId);
        CollectionAssert.Contains(unknown.Threads, thread);

        var real = model.GetOrCreateProcess(100, 200);
        var rehomed = model.GetOrCreateThread(7, 200, real);

        Assert.AreSame(thread, rehomed);
        Assert.AreSame(real, thread.Process);
        CollectionAssert.Contains(real.Threads, thread);
        CollectionAssert.DoesNotContain(unknown.Threads, thread);
        Assert.HasCount(1, model.Threads);
    }

    [TestMethod]
    public void ThreadsOfUnknownProcessShareOneUnknownProcess()
    {
        var model = new EtlTraceModel();

        var a = model.GetOrCreateThread(7, 100, null);
        var b = model.GetOrCreateThread(8, 110, null);

        Assert.AreSame(a.Process, b.Process);
        Assert.AreEqual(-1, a.Process.ProcessId);
    }

    [TestMethod]
    public void ThreadFoundForDifferentRealProcessYieldsNewRecord()
    {
        var model = new EtlTraceModel();
        var processA = model.GetOrCreateProcess(100, 50);
        var processB = model.GetOrCreateProcess(200, 50);
        var first = model.GetOrCreateThread(7, 100, processA);

        var second = model.GetOrCreateThread(7, 200, processB);

        Assert.AreNotSame(first, second);
        Assert.AreSame(processB, second.Process);
        Assert.AreSame(first, second.Previous);
        Assert.AreEqual(0, second.StartTimeQPC);
        Assert.AreSame(processA, first.Process);
        Assert.AreSame(first, model.FindThread(7, 150));
        Assert.AreSame(second, model.FindThread(7, 200));
        CollectionAssert.DoesNotContain(processA.Threads, second);
        CollectionAssert.Contains(processB.Threads, second);
    }

    // --- Time conversions ---

    [TestMethod]
    public void QPCToRelativeMSecConvertsWithFrequency()
    {
        var model = new EtlTraceModel { SessionStartTimeQPC = 1000, QPCFreq = 10_000_000 };

        Assert.AreEqual(0.0, model.QPCToRelativeMSec(1000));
        Assert.AreEqual(1.0, model.QPCToRelativeMSec(1000 + 10_000));
        Assert.AreEqual(1500.0, model.QPCToRelativeMSec(1000 + 15_000_000));
    }

    [TestMethod]
    public void QPCToRelativeMSecUsesCustomFrequency()
    {
        var model = new EtlTraceModel { SessionStartTimeQPC = 0, QPCFreq = 1000 };

        Assert.AreEqual(5.0, model.QPCToRelativeMSec(5));
    }

    [TestMethod]
    public void QPCToRelativeMSecClampsBeforeSessionStartToZero()
    {
        var model = new EtlTraceModel { SessionStartTimeQPC = 1000, QPCFreq = 10_000_000 };

        Assert.AreEqual(0.0, model.QPCToRelativeMSec(500));
        Assert.AreEqual(0.0, model.QPCToRelativeMSec(long.MinValue));
    }

    [TestMethod]
    public void QPCToDateTimeUtcOfMaxValueIsDateTimeMaxValue()
    {
        var model = new EtlTraceModel();

        Assert.AreEqual(DateTime.MaxValue, model.QPCToDateTimeUtc(long.MaxValue));
    }

    [TestMethod]
    public void QPCToDateTimeUtcOffsetsFromSyncTime()
    {
        var sync = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var model = new EtlTraceModel { QPCFreq = 10_000_000, SyncTimeQPC = 1000, SyncTimeUtc = sync };

        var time = model.QPCToDateTimeUtc(1000 + 10_000_000);

        Assert.AreEqual(sync.AddSeconds(1), time);
        Assert.AreEqual(DateTimeKind.Utc, time.Kind);
    }

    // --- Process names ---

    [TestMethod]
    public void ProcessNameDerivesFromImageFileName()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(1234, 50);
        process.ImageFileName = "KSP_x64.exe";

        Assert.AreEqual("KSP_x64", process.Name);
    }

    [TestMethod]
    public void ProcessNameFallsBackToPidForRealProcess()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(1234, 50);

        Assert.AreEqual("Process(1234)", process.Name);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void ProcessNameIsEmptyForIdleAndUnknownProcesses(int processId)
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(processId, 50);

        Assert.AreEqual(string.Empty, process.Name);
    }
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests the capture loop of <see cref="UltraProfiler.Run"/> in Paused mode with a profiler whose sessions are fakes,
/// so that it runs without administrator rights.
/// </summary>
[TestClass]
public class UltraProfilerPausedSaveTests
{
    [TestMethod]
    public async Task NextCaptureCanStartWhileASynchronousSaveIsRunning()
    {
        // The user-mode runner's save has nothing to await before the (synchronous) conversion, so the save ran on
        // the capture loop and no capture could start until it was converted.
        using var saveRelease = new ManualResetEventSlim(false);
        var releasedByTheLoop = false;
        using var profiler = new FakeProfiler(() =>
        {
            releasedByTheLoop = saveRelease.Wait(TimeSpan.FromSeconds(10));
            return [];
        });

        var startRequests = 0;
        var options = new UltraProfilerOptions
        {
            Paused = true,
            DurationInSeconds = 60,
            CheckDeltaTimeInMs = 1,
            ShouldStartProfiling = () =>
            {
                if (Interlocked.Increment(ref startRequests) == 1)
                {
                    return true;
                }

                // Waiting for the second capture: the first one is still saving. Let it finish and end the session.
                saveRelease.Set();
                profiler.Cancel();
                return false;
            },
            PollProfilingControlAction = () => ProfilingControlAction.Stop,
        };
        options.ProcessIds.Add(Environment.ProcessId);

        await profiler.Run(options).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(releasedByTheLoop, "The capture loop waited for the save of the previous capture before offering to start a new one");
        Assert.AreEqual(1, profiler.SavesCompleted);
    }

    private sealed class FakeProfiler(Func<List<UltraProfilerTraceFile>> finishFileToConvert) : UltraProfiler
    {
        private int _savesCompleted;

        public int SavesCompleted => Volatile.Read(ref _savesCompleted);

        private protected override void DisposeImpl()
        {
        }

        private protected override ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess) =>
            new(baseName)
            {
                OnStart = () => Task.CompletedTask,
                OnEnablingProfiling = () => Task.CompletedTask,
                OnProfiling = () => 0,
                OnStop = () => Task.CompletedTask,
                OnCatch = () => Task.CompletedTask,
                OnFinally = () => Task.CompletedTask,
                // Synchronous on purpose, like the user-mode runner when no merged ETL is kept.
                FinishFileToConvert = () => Task.FromResult(finishFileToConvert()),
                OnFinalCleanup = () =>
                {
                    Interlocked.Increment(ref _savesCompleted);
                    return Task.CompletedTask;
                },
            };
    }
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="PausedSessionPlanner"/>, the pure decision logic extracted from
/// <c>UltraProfiler.Run</c> for whether an interactive (Paused) session should wait for another
/// capture after finishing one, or end the whole profiling session.
/// </summary>
[TestClass]
public class PausedSessionPlannerTests
{
    [TestMethod]
    public void NotPausedNeverWaitsForAnotherCapture()
    {
        Assert.IsFalse(PausedSessionPlanner.ShouldWaitForAnotherCapture(paused: false, ProfilingControlAction.None, cancelRequested: false, allProfiledProcessesExited: false));
    }

    [TestMethod]
    public void StoppingACaptureWaitsForAnotherOne()
    {
        Assert.IsTrue(PausedSessionPlanner.ShouldWaitForAnotherCapture(paused: true, ProfilingControlAction.Stop, cancelRequested: false, allProfiledProcessesExited: false));
    }

    [TestMethod]
    public void ReachingDurationWithNoneActionWaitsForAnotherOne()
    {
        // Hitting --duration mid-capture surfaces as ProfilingControlAction.None (it isn't a user key press);
        // it should only end the current capture, not the whole session.
        Assert.IsTrue(PausedSessionPlanner.ShouldWaitForAnotherCapture(paused: true, ProfilingControlAction.None, cancelRequested: false, allProfiledProcessesExited: false));
    }

    [TestMethod]
    public void QuitActionEndsTheSession()
    {
        Assert.IsFalse(PausedSessionPlanner.ShouldWaitForAnotherCapture(paused: true, ProfilingControlAction.Quit, cancelRequested: false, allProfiledProcessesExited: false));
    }

    [TestMethod]
    public void CancelRequestedEndsTheSessionEvenIfPaused()
    {
        Assert.IsFalse(PausedSessionPlanner.ShouldWaitForAnotherCapture(paused: true, ProfilingControlAction.Stop, cancelRequested: true, allProfiledProcessesExited: false));
    }

    [TestMethod]
    public void AllProcessesExitedEndsTheSession()
    {
        Assert.IsFalse(PausedSessionPlanner.ShouldWaitForAnotherCapture(paused: true, ProfilingControlAction.Stop, cancelRequested: false, allProfiledProcessesExited: true));
    }
}

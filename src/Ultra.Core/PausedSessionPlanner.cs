// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// Pure decision logic for whether an interactive (<see cref="UltraProfilerOptions.Paused"/>) profiling session
/// should wait for another capture to start, extracted from <see cref="UltraProfiler.Run"/> so it can be tested
/// without a real process or tracing session.
/// </summary>
internal static class PausedSessionPlanner
{
    /// <summary>
    /// Decides whether a paused profiling session should wait for another capture after finishing one.
    /// </summary>
    /// <param name="paused">Whether the session is running in interactive (<see cref="UltraProfilerOptions.Paused"/>) mode.</param>
    /// <param name="lastCycleAction">The action that ended the capture that just finished.</param>
    /// <param name="cancelRequested">Whether a CTRL+C cancellation was requested during the capture that just finished.</param>
    /// <param name="allProfiledProcessesExited">Whether every profiled process has now exited.</param>
    /// <returns><c>true</c> if the session should wait for another capture to start; <c>false</c> if it should end.</returns>
    public static bool ShouldWaitForAnotherCapture(bool paused, ProfilingControlAction lastCycleAction, bool cancelRequested, bool allProfiledProcessesExited)
    {
        if (!paused) return false;
        if (cancelRequested) return false;
        if (lastCycleAction == ProfilingControlAction.Quit) return false;
        if (allProfiledProcessesExited) return false;
        return true;
    }
}

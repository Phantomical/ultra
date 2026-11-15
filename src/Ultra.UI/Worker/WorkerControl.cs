// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using Ultra.Core;

namespace Ultra.UI.Worker;

/// <summary>
/// Queues the start/stop/quit requests received from the UI for the paused profiling loop of the worker, which polls
/// them through <see cref="UltraProfilerOptions.ShouldStartProfiling"/> and
/// <see cref="UltraProfilerOptions.PollProfilingControlAction"/>.
/// </summary>
internal sealed class WorkerControl
{
    private readonly BlockingCollection<ProfilingControlAction> _actions = new();
    private volatile bool _quitRequested;

    /// <summary>
    /// Gets a value indicating whether the session was asked to end.
    /// </summary>
    public bool QuitRequested => _quitRequested;

    /// <summary>
    /// Queues a request.
    /// </summary>
    /// <param name="action">The request.</param>
    public void Post(ProfilingControlAction action)
    {
        if (action == ProfilingControlAction.Quit)
        {
            _quitRequested = true;
        }

        if (action != ProfilingControlAction.None)
        {
            _actions.Add(action);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a request to start a capture, while no capture is profiling. Stop
    /// requests are ignored (there is nothing to stop).
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns><c>true</c> to start a capture; <c>false</c> if none was requested, or the session was asked to end
    /// (see <see cref="QuitRequested"/>).</returns>
    public bool TryTakeStart(TimeSpan timeout)
    {
        if (_quitRequested)
        {
            return false;
        }

        while (_actions.TryTake(out var action, timeout))
        {
            if (action == ProfilingControlAction.Start)
            {
                return true;
            }

            if (action == ProfilingControlAction.Quit)
            {
                return false;
            }

            timeout = TimeSpan.Zero;
        }

        return false;
    }

    /// <summary>
    /// Returns the pending stop or quit request for the capture that is profiling, if any. Start requests are ignored
    /// (a capture is already profiling).
    /// </summary>
    /// <returns>The request, or <see cref="ProfilingControlAction.None"/>.</returns>
    public ProfilingControlAction Poll()
    {
        if (_quitRequested)
        {
            return ProfilingControlAction.Quit;
        }

        while (_actions.TryTake(out var action))
        {
            if (action != ProfilingControlAction.Start)
            {
                return action;
            }
        }

        return ProfilingControlAction.None;
    }
}

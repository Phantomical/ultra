// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// The action requested by the user to control an interactive (<see cref="UltraProfilerOptions.Paused"/>) profiling session.
/// </summary>
public enum ProfilingControlAction
{
    /// <summary>
    /// No action was requested; keep the current state.
    /// </summary>
    None,

    /// <summary>
    /// Start a new capture.
    /// </summary>
    Start,

    /// <summary>
    /// Stop the current capture, save it, and wait for a new one to be started.
    /// </summary>
    Stop,

    /// <summary>
    /// Stop the current capture (if any), save it, and end the profiling session entirely.
    /// </summary>
    Quit,
}

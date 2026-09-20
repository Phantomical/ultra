// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// Builds the base output file name for a single profiling capture.
/// </summary>
internal static class CaptureBaseNameBuilder
{
    /// <summary>
    /// Builds the base output file name for a capture started at <paramref name="timestamp"/>.
    /// </summary>
    /// <param name="processName">The name of the profiled process, or <c>null</c> when profiling multiple unnamed processes.</param>
    /// <param name="timestamp">The time at which this capture is starting.</param>
    /// <param name="pid">The pid of the single profiled process to append to the name, or <c>null</c> when not yet known or not applicable.</param>
    public static string Build(string? processName, DateTime timestamp, int? pid)
    {
        var baseName = processName != null ? $"ultra_{processName}_{timestamp:yyyy-MM-dd_HH_mm_ss}" : $"ultra_{timestamp:yyyy-MM-dd_HH_mm_ss}";

        if (pid is { } validPid)
        {
            baseName = $"{baseName}_pid_{validPid}";
        }

        return baseName;
    }
}

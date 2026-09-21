// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Markers;

/// <summary>
/// Represents the concurrent phase of a background GC for Firefox Profiler: the span during which the
/// collector runs alongside the mutator, between the two stop-the-world pauses a background GC brackets it
/// with.
/// </summary>
public class GCConcurrentEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for the GC concurrent phase event.
    /// </summary>
    public const string TypeId = "dotnet.gc.concurrent";

    /// <summary>
    /// Initializes a new instance of the <see cref="GCConcurrentEvent"/> class.
    /// </summary>
    public GCConcurrentEvent()
    {
        Type = TypeId;
    }

    /// <summary>
    /// Returns the schema for the GC concurrent phase event.
    /// </summary>
    /// <returns>The schema for the GC concurrent phase event.</returns>
    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "GC Concurrent",
            TableLabel = "GC Concurrent",
            Display =
            {
                FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
                FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            }
        };
}

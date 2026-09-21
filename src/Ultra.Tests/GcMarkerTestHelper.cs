// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Shared helper for tests that only need to check how a single <see cref="UTraceMarker"/> gets converted,
/// with no samples or native modules involved.
/// </summary>
internal static class GcMarkerTestHelper
{
    /// <summary>
    /// Builds a synthetic session with a single thread carrying only <paramref name="marker"/> and converts
    /// it, since marker conversion doesn't depend on samples or native modules.
    /// </summary>
    public static FirefoxProfiler.Thread BuildThreadWithOneMarker(int processId, UTraceMarker marker)
    {
        var session = new UTraceSession
        {
            NumberOfProcessors = 1,
            CpuSpeedMHz = 3000,
            StartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Duration = UTimeSpan.FromMilliseconds(10),
        };

        var process = new UTraceProcess
        {
            ProcessID = processId,
            FilePath = "testapp.exe",
            RuntimeIdentifier = "osx-arm64",
            OSDescription = "macOS 14.5 (Test Fixture)",
        };
        session.Processes.Add(process);

        var thread = process.Threads.GetOrCreateThread(1);
        thread.Name = "GCWorker";
        thread.StartTime = UTimeSpan.FromMilliseconds(0);
        thread.Markers.Add(marker);

        var options = new UltraProfilerOptions
        {
            CpuSamplingIntervalInMs = 1.0f,
            MaxDegreeOfParallelism = 1,
        };

        using var converter = new UltraConverterToFirefoxEventPipe(options);
        var profile = converter.ConvertSessionForTests(session, new List<int> { process.ProcessID });

        return profile.Threads.Single();
    }
}

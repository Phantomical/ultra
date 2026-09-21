// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Markers;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Tests that <see cref="UltraConverterToFirefoxEventPipe"/> converts a <see cref="GCConcurrentTraceMarker"/>
/// to a <see cref="GCConcurrentEvent"/>.
/// </summary>
[TestClass]
public class UltraConverterToFirefoxEventPipeGcConcurrentMarkerTests
{
    private const int ProcessId = 99;

    [TestMethod]
    public void ConcurrentGCPhaseBecomesAGCConcurrentEvent()
    {
        var thread = GcMarkerTestHelper.BuildThreadWithOneMarker(ProcessId, new GCConcurrentTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(1),
            Duration = UTimeSpan.FromMilliseconds(4),
        });

        Assert.AreEqual(1, thread.Markers.Length);
        var payload = thread.Markers.Data[0] as GCConcurrentEvent;
        Assert.IsNotNull(payload, "Expected the marker payload to be a GCConcurrentEvent");
        Assert.AreEqual(GCConcurrentEvent.TypeId, payload.Type);
        Assert.AreEqual(1.0, thread.Markers.StartTime[0]);
        Assert.AreEqual(5.0, thread.Markers.EndTime[0]);
    }
}

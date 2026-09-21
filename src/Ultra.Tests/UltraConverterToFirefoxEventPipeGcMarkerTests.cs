// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;
using Ultra.Core.Markers;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Tests that <see cref="UltraConverterToFirefoxEventPipe"/> converts a <see cref="GCTraceMarker"/> to a
/// <see cref="GCEvent"/> typed as <see cref="GCEvent.TypeIdMinor"/> at generation depth 0 (nursery) and
/// <see cref="GCEvent.TypeIdMajor"/> at any other depth.
/// </summary>
[TestClass]
public class UltraConverterToFirefoxEventPipeGcMarkerTests
{
    private const int ProcessId = 99;

    [TestMethod]
    public void NurseryCollectionBecomesAMinorGCEvent() => AssertGCEventTypeForDepth(depth: 0, GCEvent.TypeIdMinor);

    [TestMethod]
    public void Gen1CollectionBecomesAMajorGCEvent() => AssertGCEventTypeForDepth(depth: 1, GCEvent.TypeIdMajor);

    [TestMethod]
    public void Gen2CollectionBecomesAMajorGCEvent() => AssertGCEventTypeForDepth(depth: 2, GCEvent.TypeIdMajor);

    private static void AssertGCEventTypeForDepth(int depth, string expectedTypeId)
    {
        var thread = GcMarkerTestHelper.BuildThreadWithOneMarker(ProcessId, new GCTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(1),
            Duration = UTimeSpan.FromMilliseconds(2),
            Reason = "AllocSmall",
            Count = 1,
            Depth = depth,
            GCType = "NonConcurrentGC",
        });

        var payload = thread.Markers.Data[0] as GCEvent;
        Assert.IsNotNull(payload, "Expected the marker payload to be a GCEvent");
        Assert.AreEqual(expectedTypeId, payload.Type);
    }
}

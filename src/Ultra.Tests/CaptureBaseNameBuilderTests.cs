// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="CaptureBaseNameBuilder"/>, the pure base-name formatting extracted from
/// <c>UltraProfiler.Run</c> so that each capture of a multi-capture (Paused) session gets its own,
/// distinct base name without needing a real process or clock.
/// </summary>
[TestClass]
public class CaptureBaseNameBuilderTests
{
    private static readonly DateTime Timestamp = new(2026, 9, 20, 10, 30, 45);

    [TestMethod]
    public void WithProcessNameAndPid()
    {
        Assert.AreEqual("ultra_myapp_2026-09-20_10_30_45_pid_1234", CaptureBaseNameBuilder.Build("myapp", Timestamp, 1234));
    }

    [TestMethod]
    public void WithProcessNameAndNoPid()
    {
        Assert.AreEqual("ultra_myapp_2026-09-20_10_30_45", CaptureBaseNameBuilder.Build("myapp", Timestamp, null));
    }

    [TestMethod]
    public void WithNoProcessNameAndNoPid()
    {
        Assert.AreEqual("ultra_2026-09-20_10_30_45", CaptureBaseNameBuilder.Build(null, Timestamp, null));
    }

    [TestMethod]
    public void TwoCapturesOneSecondApartAreDistinct()
    {
        var first = CaptureBaseNameBuilder.Build("myapp", Timestamp, 1234);
        var second = CaptureBaseNameBuilder.Build("myapp", Timestamp.AddSeconds(1), 1234);

        Assert.AreNotEqual(first, second);
    }
}

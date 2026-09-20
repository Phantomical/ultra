// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="UltraProfilerOptions.Clone"/>, used by <c>UltraProfiler.Run</c> to give each
/// capture of a Paused session its own reporting delegates (see <see cref="UltraProfilerOptions.CaptureStatusChanged"/>)
/// while keeping every other setting in sync with the original options.
/// </summary>
[TestClass]
public class UltraProfilerOptionsCloneTests
{
    [TestMethod]
    public void CloneCopiesSettings()
    {
        var original = new UltraProfilerOptions
        {
            DurationInSeconds = 42,
            BaseOutputFileName = "foo",
            Paused = true,
        };

        var clone = original.Clone();

        Assert.AreEqual(42, clone.DurationInSeconds);
        Assert.AreEqual("foo", clone.BaseOutputFileName);
        Assert.IsTrue(clone.Paused);
    }

    [TestMethod]
    public void OverridingADelegateOnTheCloneDoesNotAffectTheOriginal()
    {
        var calls = new List<string>();
        var original = new UltraProfilerOptions
        {
            LogProgress = s => calls.Add($"original:{s}"),
        };

        var clone = original.Clone();
        clone.LogProgress = s => calls.Add($"clone:{s}");

        original.LogProgress?.Invoke("a");
        clone.LogProgress?.Invoke("b");

        CollectionAssert.AreEqual(new[] { "original:a", "clone:b" }, calls);
    }
}

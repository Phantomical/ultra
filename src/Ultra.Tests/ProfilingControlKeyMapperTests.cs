// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="ProfilingControlKeyMapper"/>, the pure key -> action mapping that drives
/// starting/stopping/quitting multiple captures during an interactive (Paused) profiling session.
/// </summary>
[TestClass]
public class ProfilingControlKeyMapperTests
{
    [TestMethod]
    [DataRow(ConsoleKey.Spacebar)]
    [DataRow(ConsoleKey.Enter)]
    public void SpaceOrEnterStartsProfiling(ConsoleKey key)
    {
        Assert.AreEqual(ProfilingControlAction.Start, ProfilingControlKeyMapper.Map(key, ConsoleModifiers.None));
    }

    [TestMethod]
    public void SKeyStopsTheCurrentCapture()
    {
        Assert.AreEqual(ProfilingControlAction.Stop, ProfilingControlKeyMapper.Map(ConsoleKey.S, ConsoleModifiers.None));
    }

    [TestMethod]
    public void QKeyQuits()
    {
        Assert.AreEqual(ProfilingControlAction.Quit, ProfilingControlKeyMapper.Map(ConsoleKey.Q, ConsoleModifiers.None));
    }

    [TestMethod]
    public void CtrlCQuits()
    {
        Assert.AreEqual(ProfilingControlAction.Quit, ProfilingControlKeyMapper.Map(ConsoleKey.C, ConsoleModifiers.Control));
    }

    [TestMethod]
    public void PlainCDoesNothing()
    {
        Assert.AreEqual(ProfilingControlAction.None, ProfilingControlKeyMapper.Map(ConsoleKey.C, ConsoleModifiers.None));
    }

    [TestMethod]
    [DataRow(ConsoleKey.A)]
    [DataRow(ConsoleKey.Escape)]
    [DataRow(ConsoleKey.Tab)]
    public void UnrelatedKeysDoNothing(ConsoleKey key)
    {
        Assert.AreEqual(ProfilingControlAction.None, ProfilingControlKeyMapper.Map(key, ConsoleModifiers.None));
    }
}

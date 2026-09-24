// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtlRecentSamples"/>, the 256-entry ring used to match a stack walk to its sample by QPC.
/// </summary>
[TestClass]
public class EtlRecentSamplesTests
{
    [TestMethod]
    public void FindOnEmptyReturnsMinusOne()
    {
        Assert.AreEqual(-1, new EtlRecentSamples().Find(0));
        Assert.AreEqual(-1, new EtlRecentSamples().Find(12345));
    }

    [TestMethod]
    public void FindUnknownQpcReturnsMinusOne()
    {
        var samples = new EtlRecentSamples();
        samples.Add(100, 0);
        samples.Add(200, 1);

        Assert.AreEqual(-1, samples.Find(150));
    }

    [TestMethod]
    public void FindKnownQpcReturnsSampleIndex()
    {
        var samples = new EtlRecentSamples();
        samples.Add(100, 7);
        samples.Add(200, 8);
        samples.Add(300, 9);

        Assert.AreEqual(7, samples.Find(100));
        Assert.AreEqual(8, samples.Find(200));
        Assert.AreEqual(9, samples.Find(300));
    }

    [TestMethod]
    public void DuplicateQpcResolvesToMostRecent()
    {
        var samples = new EtlRecentSamples();
        samples.Add(100, 1);
        samples.Add(100, 2);
        samples.Add(200, 3);

        Assert.AreEqual(2, samples.Find(100));
    }

    [TestMethod]
    public void EntriesOlderThanCapacityAreForgotten()
    {
        var samples = new EtlRecentSamples();
        const int Added = 300;
        for (var i = 0; i < Added; i++)
        {
            samples.Add(1000 + i, i);
        }

        for (var i = 0; i < Added - 256; i++)
        {
            Assert.AreEqual(-1, samples.Find(1000 + i), $"sample {i} should have been forgotten");
        }

        for (var i = Added - 256; i < Added; i++)
        {
            Assert.AreEqual(i, samples.Find(1000 + i));
        }
    }

    [TestMethod]
    public void ExactlyCapacityEntriesAreAllFound()
    {
        var samples = new EtlRecentSamples();
        for (var i = 0; i < 256; i++)
        {
            samples.Add(1000 + i, i);
        }

        Assert.AreEqual(0, samples.Find(1000));
        Assert.AreEqual(255, samples.Find(1255));
    }
}

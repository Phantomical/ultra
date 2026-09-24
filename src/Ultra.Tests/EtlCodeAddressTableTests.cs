// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtlCodeAddressTable"/>: a code address is a unique (process, address) pair.
/// </summary>
[TestClass]
public class EtlCodeAddressTableTests
{
    [TestMethod]
    public void SameProcessAndAddressReturnsSameIndex()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);

        var a = model.CodeAddresses.GetOrCreate(process, 0x1000);
        var b = model.CodeAddresses.GetOrCreate(process, 0x1000);

        Assert.AreEqual(a, b);
        Assert.AreEqual(1, model.CodeAddresses.Count);
    }

    [TestMethod]
    public void DifferentProcessReturnsDifferentIndex()
    {
        var model = new EtlTraceModel();
        var p1 = model.GetOrCreateProcess(100, 10);
        var p2 = model.GetOrCreateProcess(200, 10);

        var a = model.CodeAddresses.GetOrCreate(p1, 0x1000);
        var b = model.CodeAddresses.GetOrCreate(p2, 0x1000);

        Assert.AreNotEqual(a, b);
        Assert.AreEqual(p1.Index, model.CodeAddresses.ProcessIndex[a]);
        Assert.AreEqual(p2.Index, model.CodeAddresses.ProcessIndex[b]);
    }

    [TestMethod]
    public void DifferentAddressReturnsDifferentIndex()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);

        Assert.AreNotEqual(model.CodeAddresses.GetOrCreate(process, 0x1000), model.CodeAddresses.GetOrCreate(process, 0x1008));
    }

    [TestMethod]
    public void NewEntryIsUnresolved()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);

        var index = model.CodeAddresses.GetOrCreate(process, 0x1234);

        Assert.AreEqual(0x1234UL, model.CodeAddresses.Address[index]);
        Assert.AreEqual(-1, model.CodeAddresses.ModuleFileIndex[index]);
        Assert.AreEqual(-1, model.CodeAddresses.MethodIndex[index]);
        Assert.AreEqual(index, process.CodeAddressByAddress[0x1234]);
    }

    [TestMethod]
    public void NewEntryIsAppendedToUnresolvedListAndResetsSortedFlag()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);
        var first = model.CodeAddresses.GetOrCreate(process, 0x2000);
        process.UnresolvedCodeAddressesSorted = true;

        var second = model.CodeAddresses.GetOrCreate(process, 0x1000);

        CollectionAssert.AreEqual(new[] { first, second }, process.UnresolvedCodeAddresses);
        Assert.IsFalse(process.UnresolvedCodeAddressesSorted);
    }

    [TestMethod]
    public void ExistingEntryDoesNotTouchUnresolvedListOrSortedFlag()
    {
        var model = new EtlTraceModel();
        var process = model.GetOrCreateProcess(100, 10);
        model.CodeAddresses.GetOrCreate(process, 0x2000);
        process.UnresolvedCodeAddressesSorted = true;

        model.CodeAddresses.GetOrCreate(process, 0x2000);

        Assert.HasCount(1, process.UnresolvedCodeAddresses);
        Assert.IsTrue(process.UnresolvedCodeAddressesSorted);
    }
}

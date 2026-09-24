// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtlMethodTable"/>, appended to from several threads during symbol resolution.
/// </summary>
[TestClass]
public class EtlMethodTableTests
{
    [TestMethod]
    public void AddReturnsConsecutiveIndices()
    {
        var table = new EtlMethodTable();

        Assert.AreEqual(0, table.Add("a", 1));
        Assert.AreEqual(1, table.Add("b", 2));
        Assert.AreEqual(2, table.Add("c", 3));
        Assert.AreEqual(3, table.Count);
    }

    [TestMethod]
    public void AddRangeReturnsIndexOfFirstAdded()
    {
        var table = new EtlMethodTable();
        table.Add("a", 0);

        var first = table.AddRange([new EtlMethod("b", 1), new EtlMethod("c", 2)]);

        Assert.AreEqual(1, first);
        Assert.AreEqual(3, table.Count);
        Assert.AreEqual(3, table.Add("d", 3));
    }

    [TestMethod]
    public void ToArrayReflectsEverything()
    {
        var table = new EtlMethodTable();
        table.Add("a", 0);
        table.AddRange([new EtlMethod("b", 1), new EtlMethod("c", 2)]);

        CollectionAssert.AreEqual(
            new[] { new EtlMethod("a", 0), new EtlMethod("b", 1), new EtlMethod("c", 2) },
            table.ToArray());
    }

    [TestMethod]
    public void ToArrayIsASnapshot()
    {
        var table = new EtlMethodTable();
        table.Add("a", 0);
        var snapshot = table.ToArray();

        table.Add("b", 1);

        Assert.HasCount(1, snapshot);
    }

    [TestMethod]
    public void ConcurrentAddYieldsAllEntriesWithUniqueIndices()
    {
        var table = new EtlMethodTable();
        const int Threads = 8;
        const int PerThread = 5000;
        var indices = new int[Threads][];

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
        {
            var mine = new int[PerThread];
            for (var i = 0; i < PerThread; i++)
            {
                mine[i] = table.Add($"m{t}_{i}", t);
            }

            indices[t] = mine;
        });

        Assert.AreEqual(Threads * PerThread, table.Count);
        var all = indices.SelectMany(x => x).ToArray();
        Assert.AreEqual(Threads * PerThread, all.Distinct().Count());
        Assert.AreEqual(0, all.Min());
        Assert.AreEqual(Threads * PerThread - 1, all.Max());

        // Each returned index holds the method its caller added.
        var methods = table.ToArray();
        for (var t = 0; t < Threads; t++)
        {
            for (var i = 0; i < PerThread; i++)
            {
                if (methods[indices[t][i]].FullName != $"m{t}_{i}")
                {
                    Assert.Fail($"Index {indices[t][i]} holds {methods[indices[t][i]].FullName}, expected m{t}_{i}.");
                }
            }
        }
    }
}

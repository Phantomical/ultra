// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Etw;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="EtlSymbolResolver.ThrottledSymbolServer"/>: a symbol server lookup that never completes (a
/// stalled PDB download) must not hang the conversion or hold its slot.
/// </summary>
[TestClass]
public class ThrottledSymbolServerTests
{
    // No remote element: the lookups below never touch the reader, they only stand in for one.
    private const string SymbolPath = "";

    [TestMethod]
    public async Task LookupReturnsThePathFound()
    {
        using var server = new EtlSymbolResolver.ThrottledSymbolServer(SymbolPath, maxConcurrentLookups: 1);
        var result = await server.LookupAsync(_ => @"C:\symbols\foo.pdb", TimeSpan.FromSeconds(30));
        Assert.AreEqual(new EtlSymbolResolver.ThrottledSymbolServer.LookupResult(@"C:\symbols\foo.pdb", false), result);
    }

    [TestMethod]
    public async Task LookupThatThrowsFindsNothingWithoutTimingOut()
    {
        using var server = new EtlSymbolResolver.ThrottledSymbolServer(SymbolPath, maxConcurrentLookups: 1);
        var result = await server.LookupAsync(_ => throw new IOException("boom"), TimeSpan.FromSeconds(30));
        Assert.AreEqual(new EtlSymbolResolver.ThrottledSymbolServer.LookupResult(null, false), result);
    }

    [TestMethod]
    public async Task HungLookupTimesOutAndReleasesItsSlot()
    {
        using var hang = new ManualResetEventSlim(false);
        using var server = new EtlSymbolResolver.ThrottledSymbolServer(SymbolPath, maxConcurrentLookups: 1);
        try
        {
            object? hungReader = null;
            var hung = await server.LookupAsync(reader =>
            {
                hungReader = reader;
                hang.Wait();
                return "too late";
            }, TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(new EtlSymbolResolver.ThrottledSymbolServer.LookupResult(null, true), hung);

            // The only slot is free again while the first lookup is still stuck, and gets another reader: a reader is
            // not thread safe, and the hung lookup still uses its own.
            object? nextReader = null;
            var next = await server.LookupAsync(reader =>
            {
                nextReader = reader;
                return "found";
            }, TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(new EtlSymbolResolver.ThrottledSymbolServer.LookupResult("found", false), next);
            Assert.IsNotNull(hungReader);
            Assert.IsNotNull(nextReader);
            Assert.AreNotSame(hungReader, nextReader);
        }
        finally
        {
            hang.Set();
        }
    }
}

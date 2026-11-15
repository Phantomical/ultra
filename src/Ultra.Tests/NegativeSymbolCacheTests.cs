// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="NegativeSymbolCache"/>, used by <c>UltraConverterToFirefoxEtw</c> to skip remote
/// symbol-server round trips for PDBs already confirmed missing within its TTL.
/// </summary>
[TestClass]
public class NegativeSymbolCacheTests
{
    private const string Server = "https://msdl.microsoft.com/download/symbols";

    [TestMethod]
    public void RecordMissingMarksKeyAsKnownMissingImmediately()
    {
        var cache = NegativeSymbolCache.Load(TempFilePath());
        var key = NegativeSymbolCache.MakeKey("foo.pdb", Guid.NewGuid(), 1, Server);

        Assert.IsFalse(cache.IsKnownMissing(key));

        cache.RecordMissing(key);

        Assert.IsTrue(cache.IsKnownMissing(key));
    }

    [TestMethod]
    public void SaveThenLoadRoundTripsAFreshEntry()
    {
        var filePath = TempFilePath();
        var key = NegativeSymbolCache.MakeKey("bar.pdb", Guid.NewGuid(), 2, Server);

        var cache = NegativeSymbolCache.Load(filePath);
        cache.RecordMissing(key);
        cache.Save();

        var reloaded = NegativeSymbolCache.Load(filePath);

        Assert.IsTrue(reloaded.IsKnownMissing(key));
    }

    [TestMethod]
    public void LoadDropsEntriesOlderThanTheTtl()
    {
        var filePath = TempFilePath();
        var key = NegativeSymbolCache.MakeKey("baz.pdb", Guid.NewGuid(), 3, Server);
        var expiredTimestamp = DateTime.UtcNow - NegativeSymbolCache.Ttl - TimeSpan.FromHours(1);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllLines(filePath, [$"{expiredTimestamp:O}\t{key}"]);

        var cache = NegativeSymbolCache.Load(filePath);

        Assert.IsFalse(cache.IsKnownMissing(key));
    }

    [TestMethod]
    public void MakeKeyDistinguishesDifferentSignaturesForTheSameName()
    {
        var key1 = NegativeSymbolCache.MakeKey("same.pdb", Guid.NewGuid(), 1, Server);
        var key2 = NegativeSymbolCache.MakeKey("same.pdb", Guid.NewGuid(), 1, Server);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void AMissOnOneServerSaysNothingAboutAnother()
    {
        var cache = NegativeSymbolCache.Load(TempFilePath());
        var signature = Guid.NewGuid();
        cache.RecordMissing(NegativeSymbolCache.MakeKey("UnityPlayer.pdb", signature, 1, Server));

        Assert.IsTrue(cache.IsKnownMissing(NegativeSymbolCache.MakeKey("UnityPlayer.pdb", signature, 1, Server)));
        Assert.IsFalse(cache.IsKnownMissing(NegativeSymbolCache.MakeKey("UnityPlayer.pdb", signature, 1, "http://symbolserver.unity3d.com/")));
    }

    [TestMethod]
    public void ServerIsComparedWithoutCaseOrTrailingSlash()
    {
        var signature = Guid.NewGuid();

        Assert.AreEqual(
            NegativeSymbolCache.MakeKey("a.pdb", signature, 1, "http://SymbolServer.Unity3d.com/"),
            NegativeSymbolCache.MakeKey("a.pdb", signature, 1, "http://symbolserver.unity3d.com"));
    }

    [TestMethod]
    public void EntriesWithoutAServerAreIgnored()
    {
        // The format before misses were remembered per server: a miss on unknown servers must not hide the PDB
        // from the servers of the current symbol path.
        var filePath = TempFilePath();
        var signature = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllLines(filePath, [$"{DateTime.UtcNow:O}\t{signature:N}|1|UnityPlayer.pdb"]);

        var cache = NegativeSymbolCache.Load(filePath);

        Assert.IsFalse(cache.IsKnownMissing(NegativeSymbolCache.MakeKey("UnityPlayer.pdb", signature, 1, Server)));
    }

    [TestMethod]
    public void GetOrCreateReturnsTheSameInstanceForTheSameDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ultra-tests", Guid.NewGuid().ToString());

        var first = NegativeSymbolCache.GetOrCreate(directory);
        var second = NegativeSymbolCache.GetOrCreate(directory);

        Assert.AreSame(first, second);
    }

    private static string TempFilePath() =>
        Path.Combine(Path.GetTempPath(), "ultra-tests", Guid.NewGuid().ToString(), "negative_symbol_cache.txt");
}

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
    [TestMethod]
    public void RecordMissingMarksKeyAsKnownMissingImmediately()
    {
        var cache = NegativeSymbolCache.Load(TempFilePath());
        var key = NegativeSymbolCache.MakeKey("foo.pdb", Guid.NewGuid(), 1);

        Assert.IsFalse(cache.IsKnownMissing(key));

        cache.RecordMissing(key);

        Assert.IsTrue(cache.IsKnownMissing(key));
    }

    [TestMethod]
    public void SaveThenLoadRoundTripsAFreshEntry()
    {
        var filePath = TempFilePath();
        var key = NegativeSymbolCache.MakeKey("bar.pdb", Guid.NewGuid(), 2);

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
        var key = NegativeSymbolCache.MakeKey("baz.pdb", Guid.NewGuid(), 3);
        var expiredTimestamp = DateTime.UtcNow - NegativeSymbolCache.Ttl - TimeSpan.FromHours(1);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllLines(filePath, [$"{expiredTimestamp:O}\t{key}"]);

        var cache = NegativeSymbolCache.Load(filePath);

        Assert.IsFalse(cache.IsKnownMissing(key));
    }

    [TestMethod]
    public void MakeKeyDistinguishesDifferentSignaturesForTheSameName()
    {
        var key1 = NegativeSymbolCache.MakeKey("same.pdb", Guid.NewGuid(), 1);
        var key2 = NegativeSymbolCache.MakeKey("same.pdb", Guid.NewGuid(), 1);

        Assert.AreNotEqual(key1, key2);
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

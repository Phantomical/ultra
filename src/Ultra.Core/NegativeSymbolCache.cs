// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Globalization;

namespace Ultra.Core;

/// <summary>
/// Tracks which PDBs were confirmed missing from every remote symbol server, so that
/// <see cref="UltraConverterToFirefoxEtw"/> can skip the round trip to those servers for a PDB already
/// known to be unpublished, until <see cref="Ttl"/> elapses and it is retried in case the symbol was
/// published since. Entries are persisted to a file inside the on-disk symbol cache directory
/// (<see cref="MakeKey"/> identifies a PDB independently of any particular trace or process), so the
/// benefit carries across separate captures and separate <c>ultra</c> invocations on the same machine.
/// </summary>
internal sealed class NegativeSymbolCache
{
    /// <summary>
    /// How long a confirmed miss is trusted before it is retried against the remote servers.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(1);

    private const string FileName = "ultra_negative_symbol_cache.txt";

    private static readonly ConcurrentDictionary<string, NegativeSymbolCache> InstancesByDirectory = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, DateTime> _missingSinceUtc;
    private readonly object _saveLock = new();

    private NegativeSymbolCache(string filePath, ConcurrentDictionary<string, DateTime> missingSinceUtc)
    {
        _filePath = filePath;
        _missingSinceUtc = missingSinceUtc;
    }

    /// <summary>
    /// Gets the instance for <paramref name="cacheDirectory"/>, loading it from disk on first use and
    /// sharing it with every other caller in this process that names the same directory - which happens
    /// when captures of a <see cref="UltraProfilerOptions.Paused"/> session save concurrently.
    /// </summary>
    public static NegativeSymbolCache GetOrCreate(string cacheDirectory) =>
        InstancesByDirectory.GetOrAdd(Path.GetFullPath(cacheDirectory), static dir => Load(Path.Combine(dir, FileName)));

    /// <summary>
    /// Loads the cache from <paramref name="filePath"/>, dropping any entry older than <see cref="Ttl"/>.
    /// An unreadable or missing file yields an empty cache rather than failing the conversion.
    /// </summary>
    internal static NegativeSymbolCache Load(string filePath)
    {
        var missingSinceUtc = new ConcurrentDictionary<string, DateTime>();

        try
        {
            var now = DateTime.UtcNow;
            foreach (var line in File.ReadAllLines(filePath))
            {
                var tab = line.IndexOf('\t');
                if (tab < 0 || !DateTime.TryParse(line.AsSpan(0, tab), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var recordedAtUtc))
                {
                    continue;
                }

                if (now - recordedAtUtc < Ttl)
                {
                    missingSinceUtc[line[(tab + 1)..]] = recordedAtUtc;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new NegativeSymbolCache(filePath, missingSinceUtc);
    }

    /// <summary>
    /// Builds the key identifying a PDB, matching the identity a symbol server lookup is made against.
    /// </summary>
    public static string MakeKey(string pdbName, Guid pdbSignature, int pdbAge) =>
        $"{pdbSignature:N}|{pdbAge}|{pdbName}";

    /// <summary>
    /// Returns <c>true</c> if <paramref name="key"/> was recorded missing within the last <see cref="Ttl"/>.
    /// </summary>
    public bool IsKnownMissing(string key) =>
        _missingSinceUtc.TryGetValue(key, out var recordedAtUtc) && DateTime.UtcNow - recordedAtUtc < Ttl;

    /// <summary>
    /// Records that <paramref name="key"/> was just confirmed missing from every remote server.
    /// </summary>
    public void RecordMissing(string key) => _missingSinceUtc[key] = DateTime.UtcNow;

    /// <summary>
    /// Persists the current entries to disk, dropping any that have since expired. Best-effort: a failed
    /// write just means a later run retries those PDBs against the server instead of skipping them.
    /// </summary>
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var now = DateTime.UtcNow;
                var lines = new List<string>(_missingSinceUtc.Count);
                foreach (var (key, recordedAtUtc) in _missingSinceUtc)
                {
                    if (now - recordedAtUtc < Ttl)
                    {
                        lines.Add($"{recordedAtUtc:O}\t{key}");
                    }
                }

                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllLines(_filePath, lines);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

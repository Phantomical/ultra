// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Dia2Lib;
using Microsoft.Diagnostics.Symbols;

namespace Ultra.Core.Etw;

/// <summary>
/// Resolves the native code addresses of an <see cref="EtlTraceModel"/> to symbols: finds the PDB of every module
/// file that has unresolved addresses (locally, then on the symbol servers, with the same per-server throttling
/// and negative cache as before), then looks the addresses up in the PDBs, one module per worker.
/// </summary>
internal sealed class EtlSymbolResolver
{
    // Concurrent lookups allowed against any one remote symbol server. Local elements are not throttled.
    private const int MaxConcurrentLookupsPerSymbolServer = 32;

    private readonly EtlTraceModel _model;
    private readonly string _symbolPathText;
    private readonly int _maxDegreeOfParallelism;
    private readonly Action<string>? _logStepProgress;
    private readonly object _progressLock = new();

    private EtlSymbolResolver(EtlTraceModel model, string symbolPathText, int maxDegreeOfParallelism, Action<string>? logStepProgress)
    {
        _model = model;
        _symbolPathText = symbolPathText;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _logStepProgress = logStepProgress;
    }

    /// <summary>
    /// Resolves the unresolved native code addresses of <paramref name="model"/>.
    /// </summary>
    /// <param name="model">The model whose code addresses to resolve.</param>
    /// <param name="symbolPathText">The symbol path (<c>_NT_SYMBOL_PATH</c> syntax) to search.</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of modules resolved concurrently.</param>
    /// <param name="logStepProgress">Receives progress messages, if not <c>null</c>.</param>
    public static void Resolve(EtlTraceModel model, string symbolPathText, int maxDegreeOfParallelism, Action<string>? logStepProgress)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(symbolPathText);
        new EtlSymbolResolver(model, symbolPathText, Math.Max(1, maxDegreeOfParallelism), logStepProgress).ResolveImpl();
    }

    /// <summary>Creates a <see cref="SymbolReader"/> over <paramref name="symbolPathText"/>. Readers are not thread safe; one is created per worker.</summary>
    internal static SymbolReader CreateSymbolReader(string symbolPathText)
        => new(TextWriter.Null, symbolPathText)
        {
            Options = SymbolReaderOptions.None,
            SecurityCheck = static _ => true,
        };

    private sealed class ModuleWork
    {
        public required EtlModuleFile File { get; init; }
        public required List<int> CodeAddresses { get; init; }
        public string? PdbPath { get; set; }

        /// <summary>The symbol name found for each entry of <see cref="CodeAddresses"/> (null: none).</summary>
        public string?[]? Names { get; set; }
    }

    /// <summary>A slice of the addresses of one module, looked up by one worker.</summary>
    private readonly record struct ModuleChunk(ModuleWork Work, int Start, int Count);

    // The addresses of a module are looked up in slices of at most this many, so that one big module (a runtime
    // with hundreds of thousands of sampled addresses) is spread over the workers instead of ending the phase alone.
    private const int MaxAddressesPerChunk = 8192;

    // DIA lookups were measured to stop scaling beyond a few concurrent sessions on the same PDB.
    private const int MaxSymbolLookupWorkers = 4;

    private void ResolveImpl()
    {
        var model = _model;
        var codeAddresses = model.CodeAddresses;
        var files = model.ModuleFiles;

        // The unresolved code addresses of each module file.
        var workByFile = new Dictionary<int, ModuleWork>();
        for (var i = 0; i < codeAddresses.Count; i++)
        {
            if (codeAddresses.MethodIndex[i] != -1)
            {
                continue;
            }

            var fileIndex = codeAddresses.ModuleFileIndex[i];
            if (fileIndex < 0)
            {
                continue;
            }

            if (!workByFile.TryGetValue(fileIndex, out var work))
            {
                work = new ModuleWork { File = files[fileIndex], CodeAddresses = new List<int>() };
                workByFile[fileIndex] = work;
            }

            work.CodeAddresses.Add(i);
        }

        if (workByFile.Count == 0)
        {
            return;
        }

        var works = workByFile.Values.OrderBy(w => w.File.Index).ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        FindPdbPaths(works);
        ReportProgress($"Located PDBs for {works.Count} modules in {stopwatch.Elapsed.TotalSeconds:0.0}s");
        stopwatch.Restart();

        // Biggest modules first, in slices, so the phase ends with many small pieces rather than one big one.
        var toLoad = works.Where(w => w.PdbPath is not null).OrderByDescending(w => w.CodeAddresses.Count).ToList();
        var chunks = new List<ModuleChunk>();
        var addresses = codeAddresses.Address;
        foreach (var work in toLoad)
        {
            work.CodeAddresses.Sort((x, y) => addresses[x].CompareTo(addresses[y]));
            work.Names = new string?[work.CodeAddresses.Count];
            for (var start = 0; start < work.CodeAddresses.Count; start += MaxAddressesPerChunk)
            {
                chunks.Add(new ModuleChunk(work, start, Math.Min(MaxAddressesPerChunk, work.CodeAddresses.Count - start)));
            }
        }

        var done = 0;
        Parallel.ForEach(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(_maxDegreeOfParallelism, MaxSymbolLookupWorkers) },
            () => CreateSymbolReader(_symbolPathText),
            (chunk, _, reader) =>
            {
                try
                {
                    LookupSymbols(reader, chunk);
                }
                catch (Exception)
                {
                    // Like TraceLog, a module whose symbols cannot be read is left unresolved.
                }

                var count = Interlocked.Increment(ref done);
                ReportProgress($"Loading Symbols [{count}/{chunks.Count}] for Module `{chunk.Work.File.Name}`");
                return reader;
            },
            reader => reader.Dispose());

        // One method per distinct name of a module, in address order - deterministic whatever the scheduling.
        foreach (var work in toLoad)
        {
            var names = work.Names!;
            var list = work.CodeAddresses;
            var methodsOfModule = new List<EtlMethod>();
            var methodIndexByName = new Dictionary<string, int>();
            var localIndexOfAddress = new int[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                var name = names[i];
                if (name is null)
                {
                    localIndexOfAddress[i] = -1;
                    continue;
                }

                if (!methodIndexByName.TryGetValue(name, out var localIndex))
                {
                    localIndex = methodsOfModule.Count;
                    methodsOfModule.Add(new EtlMethod(name, work.File.Index));
                    methodIndexByName[name] = localIndex;
                }

                localIndexOfAddress[i] = localIndex;
            }

            if (methodsOfModule.Count == 0)
            {
                continue;
            }

            var first = model.Methods.AddRange(methodsOfModule);
            for (var i = 0; i < list.Count; i++)
            {
                if (localIndexOfAddress[i] >= 0)
                {
                    codeAddresses.MethodIndex[list[i]] = first + localIndexOfAddress[i];
                }
            }

            work.Names = null;
        }

        ReportProgress($"Resolved symbols of {toLoad.Count} modules in {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }

    private void ReportProgress(string message)
    {
        if (_logStepProgress is { } log)
        {
            lock (_progressLock)
            {
                log(message);
            }
        }
    }

    // TraceEvent's NativeSymbolModule keeps its DIA session private; the bulk lookup below drives DIA directly.
    private static readonly FieldInfo? DiaSessionField = typeof(NativeSymbolModule).GetField("m_session", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Regex MergedAssemblyPrefix = new(@"\$(\d+)_", RegexOptions.Compiled);

    /// <summary>Looks up the symbol names of one slice of the addresses of a module in its PDB.</summary>
    private void LookupSymbols(SymbolReader reader, ModuleChunk chunk)
    {
        var work = chunk.Work;
        var file = work.File;
        if (reader.OpenSymbolFile(work.PdbPath!) is not NativeSymbolModule symbolModule)
        {
            return;
        }

        if (file.PdbSignature != Guid.Empty && symbolModule.PdbGuid != file.PdbSignature)
        {
            return;
        }

        var addresses = _model.CodeAddresses.Address;
        var list = work.CodeAddresses;
        var names = work.Names!;
        var imageBase = file.ImageBase;
        var end = chunk.Start + chunk.Count;

        var session = DiaSessionField?.GetValue(symbolModule) as IDiaSession;
        IDiaEnumSymbolsByAddr? symbolsByAddr = null;
        session?.getSymbolsByAddr(out symbolsByAddr);
        if (symbolsByAddr is null)
        {
            // No access to the session: one DIA lookup per address, as TraceLog does.
            for (var i = chunk.Start; i < end; i++)
            {
                var address = addresses[list[i]];
                if (address < imageBase)
                {
                    continue;
                }

                uint symbolStart = 0;
                var name = symbolModule.FindNameForRva((uint)(address - imageBase), ref symbolStart);
                if (name.Length > 0)
                {
                    names[i] = name;
                }
            }

            return;
        }

        // Sampled addresses cluster in hot functions, so once a symbol is found, every following address (the
        // addresses are sorted) that lies inside it and before the next symbol by address gets the same name
        // without asking DIA again - a lookup on a large PDB costs a good fraction of a millisecond. Bounding the
        // reuse by the next symbol's start matters: symbols can nest (e.g. a public symbol inside a function's
        // range), and DIA answers the innermost one for an address in there. The name is derived exactly as
        // NativeSymbolModule.FindNameForRva derives it.
        var mergedAssemblies = symbolModule.GetMergedAssembliesMap();
        uint currentStart = 0, currentEnd = 0;
        string? currentName = null;
        for (var i = chunk.Start; i < end; i++)
        {
            var address = addresses[list[i]];
            if (address < imageBase)
            {
                continue;
            }

            var rva = (uint)(address - imageBase);
            if (rva >= currentStart && rva < currentEnd)
            {
                names[i] = currentName;
                continue;
            }

            currentEnd = 0;
            var symbol = symbolsByAddr.symbolByRVA(rva);
            if (symbol is null)
            {
                continue;
            }

            var text = symbol.name;
            if (text is null)
            {
                continue;
            }

            var length = symbol.length;
            var symbolStart = symbol.relativeVirtualAddress;
            var inRange = !((symbolStart > rva || rva >= symbolStart + length) && length != 0);

            // The lookup positioned the enumerator on the symbol; the next one bounds the addresses the answer holds for.
            var nextSymbolStart = 0u;
            symbolsByAddr.Next(1, out var nextSymbol, out var fetched);
            if (fetched == 1 && nextSymbol is not null)
            {
                nextSymbolStart = nextSymbol.relativeVirtualAddress;
            }
            if (!inRange)
            {
                text = "??" + text;
            }

            if (text.IndexOf('@') >= 0)
            {
                symbol.get_undecoratedNameEx(4096, out var undecorated);
                if (undecorated is not null)
                {
                    text = undecorated;
                }

                if (text.StartsWith('@'))
                {
                    text = text.Substring(1);
                }

                if (text.StartsWith('_'))
                {
                    text = text.Substring(1);
                }

                var at = text.IndexOf('@');
                if (at > 0)
                {
                    text = text.Substring(0, at);
                }
            }

            var dollar = text.LastIndexOf('$');
            if (dollar >= 0 && dollar + 2 < text.Length && text[dollar + 1] == '#' && text.IndexOf('#', dollar + 2) >= 0)
            {
                text = text.Substring(0, dollar);
            }

            if (mergedAssemblies is not null)
            {
                var prefixMatchFound = false;
                text = MergedAssemblyPrefix.Replace(text, match =>
                {
                    prefixMatchFound = true;
                    return GetAssemblyNameFromModuleIndex(mergedAssemblies, int.Parse(match.Groups[1].Value), match.Groups[1].Value);
                });
                if (!prefixMatchFound)
                {
                    text = GetAssemblyNameFromModuleIndex(mergedAssemblies, int.MaxValue, string.Empty) + text;
                }
            }

            if (text.Length > 0)
            {
                names[i] = text;
            }

            if (inRange && length != 0 && nextSymbolStart > rva)
            {
                currentStart = rva;
                currentEnd = (uint)Math.Min(nextSymbolStart, Math.Min(uint.MaxValue, symbolStart + length));
                currentName = text.Length > 0 ? text : null;
            }
        }
    }

    private static string GetAssemblyNameFromModuleIndex(Dictionary<int, string> mergedAssembliesMap, int moduleIndex, string defaultValue)
    {
        if (mergedAssembliesMap.TryGetValue(moduleIndex, out var value))
        {
            try
            {
                return new AssemblyName(value).Name + "!";
            }
            catch (Exception)
            {
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Finds the PDB of every module in <paramref name="works"/>: first with the local elements of the symbol
    /// path (cheap, unthrottled, in parallel), then with the remote symbol servers for the rest, skipping the PDBs
    /// the <see cref="NegativeSymbolCache"/> knows to be unpublished and recording the new misses.
    /// </summary>
    private void FindPdbPaths(List<ModuleWork> works)
    {
        var symbolPath = new SymbolPath(_symbolPathText);
        var localOnlyPathText = symbolPath.LocalOnly().ToString();
        var completed = 0;

        // Local lookups. A module the trace carries no PDB signature for (the trace was not merged) gets it from
        // the image on disk, provided it is the image that ran (same checksum and timestamp).
        Parallel.ForEach(
            works,
            new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism },
            () => CreateSymbolReader(localOnlyPathText),
            (work, _, reader) =>
            {
                var file = work.File;
                try
                {
                    if (file.PdbSignature == Guid.Empty)
                    {
                        ReadPdbSignatureFromImage(file);
                    }

                    if (file.PdbSignature != Guid.Empty)
                    {
                        work.PdbPath = reader.FindSymbolFilePath(file.PdbName, file.PdbSignature, file.PdbAge, file.FilePath, file.ProductVersion, portablePdbMatch: true);
                    }
                }
                catch (Exception)
                {
                    work.PdbPath = null;
                }

                var done = Interlocked.Increment(ref completed);
                ReportProgress($"Resolving Cached Symbols [{done}/{works.Count}] for Module `{file.Name}`");
                return reader;
            },
            reader => reader.Dispose());

        var remoteElements = symbolPath.Elements.Where(element => element.IsSymServer && element.IsRemote).ToList();
        if (remoteElements.Count == 0)
        {
            return;
        }

        var negativeCache = NegativeSymbolCache.GetOrCreate(symbolPath.DefaultSymbolCache());
        var toQuery = new List<ModuleWork>();
        foreach (var work in works)
        {
            var file = work.File;
            if (work.PdbPath is not null || file.PdbSignature == Guid.Empty)
            {
                continue;
            }

            if (negativeCache.IsKnownMissing(NegativeSymbolCache.MakeKey(file.PdbName, file.PdbSignature, file.PdbAge)))
            {
                ReportProgress($"Skipping Known-Missing Symbols for Module `{file.Name}`");
            }
            else
            {
                toQuery.Add(work);
            }
        }

        if (toQuery.Count == 0)
        {
            return;
        }

        var localElements = symbolPath.Elements.Where(element => !(element.IsSymServer && element.IsRemote)).ToList();
        var servers = new List<ThrottledSymbolServer>(remoteElements.Count);
        try
        {
            foreach (var remoteElement in remoteElements)
            {
                // A reader per server, searching the local elements plus that one remote, so every lookup is
                // attributable to a single server and can be throttled against it.
                var perServerPath = new SymbolPath();
                foreach (var localElement in localElements)
                {
                    perServerPath.Add(localElement);
                }

                perServerPath.Add(remoteElement);
                servers.Add(new ThrottledSymbolServer(perServerPath.ToString()));
            }

            var queried = 0;

            // Asked in turn, stopping at the first hit. Asking them all at once was measured to be no faster - the
            // throttle, not the walk, is the limit - and it would query every server for a module the first one
            // already answered.
            async Task QueryAsync(ModuleWork work)
            {
                var file = work.File;
                string? path = null;
                foreach (var server in servers)
                {
                    using (var lease = await server.AcquireAsync().ConfigureAwait(false))
                    {
                        var reader = lease.Reader;
                        path = await Task.Run(() =>
                        {
                            try
                            {
                                return reader.FindSymbolFilePath(file.PdbName, file.PdbSignature, file.PdbAge, file.FilePath, file.ProductVersion, portablePdbMatch: true);
                            }
                            catch
                            {
                                return null;
                            }
                        }).ConfigureAwait(false);
                    }

                    if (path is not null)
                    {
                        break;
                    }
                }

                if (path is null)
                {
                    negativeCache.RecordMissing(NegativeSymbolCache.MakeKey(file.PdbName, file.PdbSignature, file.PdbAge));
                }

                work.PdbPath = path;
                var done = Interlocked.Increment(ref queried);
                ReportProgress($"Querying Symbol Servers [{done}/{toQuery.Count}] for Module `{file.Name}`");
            }

            var lookups = new Task[toQuery.Count];
            for (var i = 0; i < toQuery.Count; i++)
            {
                lookups[i] = QueryAsync(toQuery[i]);
            }

            Task.WaitAll(lookups);
        }
        finally
        {
            foreach (var server in servers)
            {
                server.Dispose();
            }

            negativeCache.Save();
        }
    }

    /// <summary>
    /// Reads the PDB signature of <paramref name="file"/> from the image on disk when it is the image that ran
    /// (same checksum and timestamp as the trace reports), like <c>TraceCodeAddresses.TraceModuleUnchanged</c> and
    /// <c>SymbolReader.FindSymbolFilePathForModule</c>.
    /// </summary>
    private static void ReadPdbSignatureFromImage(EtlModuleFile file)
    {
        if (!file.HasFilePath || file.ImageBase == 0)
        {
            return;
        }

        // A 32-bit process would have to bypass the System32 redirection here; ultra runs as a 64-bit process.
        var path = file.FilePath;
        if (!File.Exists(path))
        {
            return;
        }

        using var peFile = new PEFile.PEFile(path);
        var header = peFile.Header;
        if (header.CheckSum != (uint)file.ImageChecksum)
        {
            return;
        }

        if (file.TimeDateStamp != 0 && header.TimeDateStampSec != file.TimeDateStamp)
        {
            return;
        }

        if (peFile.GetPdbSignature(out var pdbName, out var pdbGuid, out var pdbAge, first: true))
        {
            file.PdbName = pdbName;
            file.PdbSignature = pdbGuid;
            file.PdbAge = pdbAge;
            if (file.ProductVersion is null)
            {
                file.ProductVersion = peFile.GetFileVersionInfo()?.FileVersion;
            }
        }
    }

    /// <summary>
    /// A single remote symbol server, limited to <see cref="MaxConcurrentLookupsPerSymbolServer"/> concurrent
    /// lookups, each with a <see cref="SymbolReader"/> of its own.
    /// </summary>
    private sealed class ThrottledSymbolServer : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(MaxConcurrentLookupsPerSymbolServer);
        private readonly ConcurrentBag<SymbolReader> _readers = [];

        public ThrottledSymbolServer(string symbolPathText)
        {
            for (var i = 0; i < MaxConcurrentLookupsPerSymbolServer; i++)
            {
                _readers.Add(CreateSymbolReader(symbolPathText));
            }
        }

        public async ValueTask<Lease> AcquireAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            _readers.TryTake(out var reader);
            return new Lease(this, reader!);
        }

        public void Dispose()
        {
            while (_readers.TryTake(out var reader))
            {
                reader.Dispose();
            }

            _semaphore.Dispose();
        }

        /// <summary>Holds one of the server's readers until disposed.</summary>
        public readonly struct Lease(ThrottledSymbolServer server, SymbolReader reader) : IDisposable
        {
            public SymbolReader Reader => reader;

            public void Dispose()
            {
                server._readers.Add(reader);
                server._semaphore.Release();
            }
        }
    }
}

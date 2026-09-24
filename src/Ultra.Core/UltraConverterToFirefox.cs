// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.IO.Compression;
using System.Text.Json;

namespace Ultra.Core;

/// <summary>
/// Converts a list of trace files (one ETL file or a list of nettrace files) to a Firefox profile.
/// </summary>
public abstract class UltraConverterToFirefox : IDisposable
{
    private protected readonly List<UltraProfilerTraceFile> TraceFiles;
    private protected readonly UltraProfilerOptions Options;
    private protected FirefoxProfiler.Profile ProfilerResult;

    /// <summary>
    /// A generic other category.
    /// </summary>
    public const int CategoryOther = 0;

    /// <summary>
    /// The kernel category.
    /// </summary>
    public const int CategoryKernel = 1;

    /// <summary>
    /// The native category.
    /// </summary>
    public const int CategoryNative = 2;

    /// <summary>
    /// The managed category.
    /// </summary>
    public const int CategoryManaged = 3;

    /// <summary>
    /// The GC category.
    /// </summary>
    public const int CategoryGc = 4;

    /// <summary>
    /// The JIT category.
    /// </summary>
    public const int CategoryJit = 5;

    /// <summary>
    /// The CLR category.
    /// </summary>
    public const int CategoryClr = 6;

    private protected UltraConverterToFirefox(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options)
    {
        TraceFiles = traceFiles;
        this.Options = options;
        ProfilerResult = new FirefoxProfiler.Profile(); // Create an empty profile (not used and override by the derived class)
    }

    /// <inheritdoc />
    public abstract void Dispose();

    /// <summary>
    /// Converts a list of trace files (one ETL file, or a list of nettrace files) to a Firefox profile.
    /// </summary>
    /// <param name="traceFiles">The list of trace files to convert.</param>
    /// <param name="options">The options used for converting.</param>
    /// <param name="processIds">The list of process ids to extract from the trace files.</param>
    /// <returns>The converted Firefox profile.</returns>
    public static FirefoxProfiler.Profile Convert(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options, List<int> processIds)
    {
        var extensions = traceFiles.Select(x => Path.GetExtension(x.FileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (extensions.Count != 1)
        {
            throw new ArgumentException($"All trace files must have the same extension. Instead got [{string.Join(", ", extensions)}]");
        }

        var extension = extensions.First();
        if (extension == ".etl")
        {
            if (UseLegacyEtlConverter)
            {
                if (traceFiles.Count > 1)
                {
                    throw new ArgumentException("Only one ETL file is supported by the legacy ETL converter");
                }

                using var legacyConverter = new UltraConverterToFirefoxEtw(traceFiles, options);
                return legacyConverter.Convert(processIds);
            }

            using var converter = new UltraConverterToFirefoxEtl(traceFiles, options);
            return converter.Convert(processIds);
        }
        else if (extension == ".nettrace")
        {
            using var converter = new UltraConverterToFirefoxEventPipe(traceFiles, options);
            return converter.Convert(processIds);
        }
        else
        {
            throw new ArgumentException($"Unsupported trace file extension [{extension}]");
        }
    }

    /// <summary>
    /// Whether ETL files are converted through the previous <c>TraceLog</c>/<c>.etlx</c> based converter instead
    /// of the single-pass one. Selected by setting the <c>ULTRA_LEGACY_ETL</c> environment variable to <c>1</c>;
    /// kept to compare the two conversions on the same trace.
    /// </summary>
    internal static bool UseLegacyEtlConverter { get; set; } = Environment.GetEnvironmentVariable("ULTRA_LEGACY_ETL") == "1";

    /// <summary>
    /// Converts a list of trace files (one or more ETL files, or a list of nettrace files) and writes the resulting
    /// Firefox profile, gzipped, to <paramref name="outputFilePath"/>.
    /// </summary>
    /// <param name="traceFiles">The list of trace files to convert.</param>
    /// <param name="options">The options used for converting.</param>
    /// <param name="processIds">The list of process ids to extract from the trace files.</param>
    /// <param name="outputFilePath">The <c>.json.gz</c> file to write.</param>
    /// <param name="cancellationToken">Cancels the conversion; no output file is then left behind.</param>
    /// <exception cref="ArgumentException">The trace files do not share one supported extension.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static void ConvertToFile(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options, List<int> processIds, string outputFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceFiles);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(outputFilePath);

        var extension = GetExtension(traceFiles);
        if (extension == ".etl" && !UseLegacyEtlConverter)
        {
            // Streams each thread as it is converted; see UltraConverterToFirefoxEtl.ConvertToFile.
            using var converter = new UltraConverterToFirefoxEtl(traceFiles, options);
            converter.ConvertToFile(processIds, outputFilePath, cancellationToken);
            return;
        }

        var profile = Convert(traceFiles, options, processIds);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.Create(outputFilePath);
        using var gzipStream = CreateProfileGZipStream(stream);
        JsonSerializer.Serialize(gzipStream, profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
    }

    // zlib level 5: measured on a 285 MB profile to compress 30% faster than the default level 6 for a file
    // less than 1% larger.
    private const int ProfileCompressionLevel = 5;

    /// <summary>Wraps <paramref name="stream"/> in the gzip compressor profiles are written with.</summary>
    private protected static GZipStream CreateProfileGZipStream(Stream stream)
        => new(stream, new ZLibCompressionOptions { CompressionLevel = ProfileCompressionLevel });

    private static string GetExtension(List<UltraProfilerTraceFile> traceFiles)
    {
        var extensions = traceFiles.Select(x => Path.GetExtension(x.FileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (extensions.Count != 1)
        {
            throw new ArgumentException($"All trace files must have the same extension. Instead got [{string.Join(", ", extensions)}]");
        }

        return extensions.First();
    }

    private FirefoxProfiler.Profile Convert(List<int> processIds)
    {
        ConvertImpl(processIds);
        return ProfilerResult;
    }

    private protected abstract void ConvertImpl(List<int> processIds);

    /// <summary>
    /// Returns a frame identical to <paramref name="frameTableIndex"/> but categorized as
    /// <see cref="CategoryGc"/>, creating it the first time it is asked for.
    /// </summary>
    /// <param name="frameTable">The frame table of the thread being converted.</param>
    /// <param name="frameTableIndex">The frame to clone; must not be a kernel or GC frame already.</param>
    /// <param name="gcSubtreeFrames">Maps a frame index to its GC-categorized clone.</param>
    /// <returns>The index of the GC-categorized clone.</returns>
    /// <remarks>
    /// The Firefox Profiler derives a stack node's category from its frame alone - its processed-profile
    /// upgrader (version 53) deletes <c>stackTable.category</c> on load - so a category written on the stack
    /// node is never displayed. A callee that should be attributed to the collector only because of who called
    /// it (a libc helper, Mono's lock-free allocator) shares its code address, and so its frame, with call paths
    /// outside the collector; it needs a GC-categorized frame of its own. The clone keeps the same func, so the
    /// call tree still merges both variants into a single node.
    /// </remarks>
    private protected static int GetOrCreateGcSubtreeFrame(FirefoxProfiler.FrameTable frameTable, int frameTableIndex, Dictionary<int, int> gcSubtreeFrames)
    {
        if (gcSubtreeFrames.TryGetValue(frameTableIndex, out var gcFrameTableIndex))
        {
            return gcFrameTableIndex;
        }

        gcFrameTableIndex = frameTable.Length;
        gcSubtreeFrames.Add(frameTableIndex, gcFrameTableIndex);

        frameTable.Address.Add(frameTable.Address[frameTableIndex]);
        frameTable.InlineDepth.Add(frameTable.InlineDepth[frameTableIndex]);
        frameTable.Category.Add(CategoryGc);
        frameTable.Subcategory.Add(0);
        frameTable.Func.Add(frameTable.Func[frameTableIndex]);
        frameTable.NativeSymbol.Add(frameTable.NativeSymbol[frameTableIndex]);
        frameTable.InnerWindowID.Add(frameTable.InnerWindowID[frameTableIndex]);
        frameTable.Implementation.Add(frameTable.Implementation[frameTableIndex]);
        frameTable.Line.Add(frameTable.Line[frameTableIndex]);
        frameTable.Column.Add(frameTable.Column[frameTableIndex]);
        frameTable.Length++;

        return gcFrameTableIndex;
    }

    /// <summary>
    /// Whether a resolved function name is an entry point whose entire callee subtree is garbage-collection
    /// work - not just the frames whose own symbol says so - and should be categorized as <see cref="CategoryGc"/>
    /// down to the leaves (kernel frames excepted): the collection routine of Mono's SGen collector and the
    /// main function of its dedicated worker thread, and Unity's unused-asset collection.
    /// </summary>
    /// <param name="name">The resolved function name, as stored in the thread's func table.</param>
    /// <returns><c>true</c> when the callee subtree of the function is GC work.</returns>
    /// <remarks>
    /// The SGen collection routine is matched by prefix because the routine that does the work,
    /// <c>sgen_perform_collection_inner</c>, is a static helper of <c>sgen_perform_collection</c> that some
    /// Mono builds inline: the collection then resolves under the outer name and under the inner one otherwise.
    /// <c>sgen_gc_collect</c> (an explicit <c>GC.Collect</c>) calls the same routine and is listed for builds
    /// that inline that call too.
    /// </remarks>
    private protected static bool IsGcSubtreeRootFunction(string name)
    {
        return name.StartsWith("sgen_perform_collection", StringComparison.Ordinal)
            || name is "sgen_gc_collect" or "sgen_thread_func"
            // Resources.UnloadUnusedAssets: Unity walks the managed heap to find the assets still referenced,
            // then destroys the rest - all of it collection work, none of it under an sgen_ symbol.
            || name == "GarbageCollectSharedAssets";
    }
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

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
            if (traceFiles.Count > 1)
            {
                throw new ArgumentException("Only one ETL file is supported");
            }

            using var converter = new UltraConverterToFirefoxEtw(traceFiles, options);
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
}

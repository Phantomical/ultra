// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ultra.Core.Etw;
using Ultra.Core.Markers;

namespace Ultra.Core;

/// <summary>
/// Converts ETW trace file(s) to a Firefox profile by reading the raw <c>.etl</c> event stream once into an
/// <see cref="EtlTraceModel"/> (see <see cref="EtlTraceReader"/>), resolving symbols module by module in
/// parallel (see <see cref="EtlSymbolResolver"/>), and then building every profile thread in parallel.
/// </summary>
/// <remarks>
/// This replaces the <c>TraceLog</c>-based conversion, which converted the whole trace to an <c>.etlx</c> file on
/// disk, read it back, and walked every event of every process once per conversion on a single thread. The
/// profile produced is the same; only the way it is computed differs.
/// </remarks>
internal sealed class UltraConverterToFirefoxEtl : UltraConverterToFirefox
{
    // Shared across all processes: a module file loaded by several processes has one lib.
    private readonly Dictionary<int, int> _mapModuleFileIndexToLib = new();
    private readonly string _symbolPathText;
    private EtlTraceModel _model = null!;
    private EtlMethod[] _methods = [];
    private int _profileThreadIndex;

    public UltraConverterToFirefoxEtl(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options) : base(traceFiles, options)
    {
        _symbolPathText = options.GetCachedSymbolPath().ToString();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
    }

    private int MaxDegreeOfParallelism => Options.MaxDegreeOfParallelism > 0 ? Options.MaxDegreeOfParallelism : Environment.ProcessorCount;

    private protected override void ConvertImpl(List<int> processIds)
    {
        var contexts = Prepare(processIds);

        Options.LogProgress?.Invoke("Converting Threads");
        var converted = 0;
        Parallel.ForEach(contexts, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism }, context =>
        {
            BuildThread(context);
            var done = Interlocked.Increment(ref converted);
            if ((done & 15) == 0 || done == contexts.Count)
            {
                Options.LogStepProgress?.Invoke($"Converting Threads [{done}/{contexts.Count}]");
            }
        });

        _model = null!;
    }

    // Threads whose JSON is serialized but not written yet are kept in memory; builders pause beyond this many.
    private const int MaxSerializedThreadsAhead = 64;

    /// <summary>
    /// Converts the trace and writes the gzipped Firefox profile JSON to <paramref name="outputFilePath"/>, streaming
    /// each thread as soon as it is built so that serialization and compression overlap with the conversion of the
    /// other threads, and so that a thread's tables are released right after they are serialized. Threads are
    /// written in profile order, so the file is identical to serializing the whole profile at once.
    /// </summary>
    /// <param name="processIds">The ids of the processes to convert.</param>
    /// <param name="outputFilePath">The <c>.json.gz</c> file to write.</param>
    /// <param name="cancellationToken">Cancels the conversion; the output file is then not left behind.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A requested process is not in the trace.</exception>
    public void ConvertToFile(List<int> processIds, string outputFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(outputFilePath);

        try
        {
            ConvertToFileImpl(processIds, outputFilePath, cancellationToken);
        }
        catch
        {
            // Whatever was written so far is not a valid profile.
            try { File.Delete(outputFilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private void ConvertToFileImpl(List<int> processIds, string outputFilePath, CancellationToken cancellationToken)
    {
        var contexts = Prepare(processIds, cancellationToken);
        var profile = ProfilerResult;
        cancellationToken.ThrowIfCancellationRequested();

        Options.LogProgress?.Invoke("Converting Threads");
        using var stream = File.Create(outputFilePath);
        using var gzipStream = CreateProfileGZipStream(stream);
        using var writer = new Utf8JsonWriter(gzipStream);
        var jsonContext = FirefoxProfiler.JsonProfilerContext.Default;

        writer.WriteStartObject();
        writer.WritePropertyName("meta");
        JsonSerializer.Serialize(writer, profile.Meta, jsonContext.ProfileMeta);
        writer.WritePropertyName("libs");
        JsonSerializer.Serialize(writer, profile.Libs, jsonContext.ListLib);
        if (profile.Pages is not null)
        {
            writer.WritePropertyName("pages");
            JsonSerializer.Serialize(writer, profile.Pages, jsonContext.ListPage);
        }

        if (profile.Counters is not null)
        {
            writer.WritePropertyName("counters");
            JsonSerializer.Serialize(writer, profile.Counters, jsonContext.ListCounter);
        }

        if (profile.ProfilerOverhead is not null)
        {
            writer.WritePropertyName("profilerOverhead");
            JsonSerializer.Serialize(writer, profile.ProfilerOverhead, jsonContext.ListProfilerOverhead);
        }

        writer.WritePropertyName("threads");
        writer.WriteStartArray();

        // Builders hand each thread's JSON over by index; the writer consumes them in order.
        var serialized = new byte[contexts.Count][];
        var gate = new object();
        var written = 0;
        Exception? failure = null;

        // Either side failing (or a cancellation) records the failure and wakes the other side, so that neither
        // the writer nor a builder can wait forever on the other.
        void Fail(Exception exception)
        {
            lock (gate)
            {
                failure ??= exception;
                Monitor.PulseAll(gate);
            }
        }

        var builders = Task.Run(() =>
        {
            try
            {
                // One thread at a time per worker, dispatched in profile order (the CPU-heaviest threads first),
                // so the writer rarely waits on a thread that has not started.
                Parallel.ForEach(Partitioner.Create(0, contexts.Count, 1), new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = cancellationToken }, range =>
                {
                    for (var i = range.Item1; i < range.Item2; i++)
                    {
                        if (failure is not null)
                        {
                            return;
                        }

                        var context = contexts[i];
                        BuildThread(context);
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(context.ProfileThread, jsonContext.Thread);

                        // The tables are not needed anymore; only the JSON is kept until written.
                        profile.Threads[i] = null!;

                        lock (gate)
                        {
                            while (i - written > MaxSerializedThreadsAhead && failure is null)
                            {
                                Monitor.Wait(gate);
                            }

                            serialized[i] = bytes;
                            Monitor.PulseAll(gate);
                        }
                    }
                });
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        });

        var writeStopwatch = Stopwatch.StartNew();
        var waitedMs = 0.0;
        try
        {
            for (var i = 0; i < contexts.Count; i++)
            {
                byte[] bytes;
                var waitStart = writeStopwatch.Elapsed.TotalMilliseconds;
                lock (gate)
                {
                    while (serialized[i] is null && failure is null)
                    {
                        Monitor.Wait(gate);
                    }

                    if (failure is not null)
                    {
                        break;
                    }

                    bytes = serialized[i];
                    serialized[i] = null!;
                    written = i + 1;
                    Monitor.PulseAll(gate);
                }

                waitedMs += writeStopwatch.Elapsed.TotalMilliseconds - waitStart;
                writer.WriteRawValue(bytes, skipInputValidation: true);
                if ((i & 15) == 15 || i == contexts.Count - 1)
                {
                    Options.LogStepProgress?.Invoke($"Writing Threads [{i + 1}/{contexts.Count}]");
                    writer.Flush();
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
            try { builders.Wait(); } catch (AggregateException) { }
            throw;
        }

        // Propagates a builder failure.
        builders.GetAwaiter().GetResult();
        Options.LogProgress?.Invoke($"Threads written in {writeStopwatch.Elapsed.TotalSeconds:0.0}s (waited {waitedMs / 1000:0.0}s)");

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        _model = null!;
    }

    /// <summary>
    /// Reads the trace, resolves its symbols and sets up every requested process: the profile is complete but for
    /// its threads' tables, which <see cref="BuildThread"/> fills for each returned context.
    /// </summary>
    private List<ThreadConvertContext> Prepare(List<int> processIds, CancellationToken cancellationToken = default)
    {
        // Deduplicate incoming process ids, preserving first-occurrence order: the same id can be passed more
        // than once (e.g. repeated --pid flags), and a process must be set up only once.
        var seenProcessIds = new HashSet<int>();
        var dedupedProcessIds = new List<int>(processIds.Count);
        foreach (var processId in processIds)
        {
            if (seenProcessIds.Add(processId))
            {
                dedupedProcessIds.Add(processId);
            }
        }

        processIds = dedupedProcessIds;

        var stopwatch = Stopwatch.StartNew();
        void LogPhase(string phase)
        {
            Options.LogProgress?.Invoke($"{phase} ({stopwatch.Elapsed.TotalSeconds:0.0}s)");
        }

        // Phase 1: one pass over the raw events.
        Options.LogProgress?.Invoke("Reading Events");
        var files = TraceFiles.Select(x => x.FileName).ToList();
        _model = EtlTraceReader.Read(files, processIds, Options.LogStepProgress);
        LogPhase("Reading Events Done");
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: symbols.
        Options.LogProgress?.Invoke("Loading Symbols");
        EtlSymbolResolver.Resolve(_model, _symbolPathText, MaxDegreeOfParallelism, Options.LogStepProgress);
        _methods = _model.Methods.ToArray();
        LogPhase("Loading Symbols Done");
        cancellationToken.ThrowIfCancellationRequested();

        ProfilerResult = CreateProfile();

        // Phase 3: set up every process and its threads, in the requested order, and finalize what does not depend
        // on the threads' tables (default visible threads, memory counters).
        _profileThreadIndex = 0;
        var processStates = new List<ProcessConvertState>(processIds.Count);
        foreach (var processId in processIds)
        {
            var process = _model.LastProcessWithId(processId)
                          ?? throw new InvalidOperationException($"The process with id {processId} was not found in the trace");
            processStates.Add(SetupProcess(process));
        }

        foreach (var state in processStates)
        {
            FinalizeProcess(state);
        }

        return processStates.SelectMany(state => state.ThreadContexts).ToList();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Per-process setup
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Per-process classification of module files, needed to categorize frames.</summary>
    private sealed class ProcessModuleContext
    {
        public HashSet<int> ManagedModuleFiles { get; } = new();
        public Dictionary<long, string> ManagedModuleFileNames { get; } = new();
        public int ClrJitModuleFile { get; set; } = -1;
        public int CoreClrModuleFile { get; set; } = -1;
    }

    private sealed class ProcessConvertState
    {
        public required EtlProcess Process { get; init; }
        public required List<ThreadConvertContext> ThreadContexts { get; init; }
        public required int ThreadIndexWithMaxCpuTime { get; init; }
        public required List<int> InitialVisibleThreads { get; init; }
        public required List<int> InitialSelectedThreads { get; init; }
    }

    /// <summary>The state of one profile thread being built; only its own builder touches it.</summary>
    private sealed class ThreadConvertContext
    {
        public required EtlThread Thread { get; init; }
        public required FirefoxProfiler.Thread ProfileThread { get; init; }
        public required ProcessModuleContext ProcessModules { get; init; }

        /// <summary>The position of this thread within its process's CPU-time-sorted thread list.</summary>
        public required int ThreadIndex { get; init; }

        /// <summary>The index of this thread's <see cref="FirefoxProfiler.Thread"/> within <c>ProfilerResult.Threads</c>.</summary>
        public required int ProfileThreadIndex { get; init; }

        public Dictionary<int, int> MapStackNodeToFirefox { get; } = new();
        public List<bool> StackInGcSubtree { get; } = new();
        public Dictionary<int, int> MapFrameToGcSubtreeFrame { get; } = new();
        public Dictionary<int, int> MapCodeAddressToFrame { get; } = new();
        public Dictionary<int, int> MapCodeAddressToFunc { get; } = new();
        public Dictionary<int, int> MapMethodToFunc { get; } = new();
        public Dictionary<string, int> MapStringToFirefox { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, int> MapModuleFileToResource { get; } = new();
    }

    private ProcessConvertState SetupProcess(EtlProcess process)
    {
        var model = _model;
        var meta = ProfilerResult.Meta;
        if (meta.Product == string.Empty)
        {
            meta.Product = process.Name;
        }

        var processStartTime = new DateTimeOffset(model.QPCToDateTimeUtc(process.StartTimeQPC)).ToUnixTimeMilliseconds();
        var processEndTime = new DateTimeOffset(model.QPCToDateTimeUtc(process.EndTimeQPC)).ToUnixTimeMilliseconds();
        if (processStartTime < meta.StartTime)
        {
            meta.StartTime = processStartTime;
        }

        if (processEndTime > meta.EndTime)
        {
            meta.EndTime = processEndTime;
        }

        var profilingStartTime = model.QPCToRelativeMSec(process.StartTimeQPC);
        if (profilingStartTime < meta.ProfilingStartTime)
        {
            meta.ProfilingStartTime = profilingStartTime;
        }

        var profilingEndTime = model.QPCToRelativeMSec(process.EndTimeQPC);
        if (profilingEndTime > meta.ProfilingEndTime)
        {
            meta.ProfilingEndTime = profilingEndTime;
        }

        var processModules = LoadModules(process);

        var threads = process.Threads;
        var cpuTimesInMs = new double[threads.Count];
        for (var i = 0; i < threads.Count; i++)
        {
            cpuTimesInMs[i] = (float)(threads[i].CpuSamples * model.SampleIntervalMs);
        }

        var visibilityPlan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, _profileThreadIndex, Options.MinimumCpuTimeBeforeThreadIsVisibleInMs);

        var processName = $"{process.Name} ({process.ProcessId})";
        var threadContexts = new List<ThreadConvertContext>(threads.Count);

        for (var threadIndex = 0; threadIndex < visibilityPlan.SortOrder.Length; threadIndex++)
        {
            var thread = threads[visibilityPlan.SortOrder[threadIndex]];

            string threadBaseName;
            if (!model.ThreadNamesById.TryGetValue(thread.ThreadId, out var dynamicThreadName))
            {
                threadBaseName = thread.ThreadInfo is not null
                    ? $"{thread.ThreadInfo} ({thread.ThreadId})"
                    : $"Thread ({thread.ThreadId})";
            }
            else
            {
                threadBaseName = $"{dynamicThreadName} ({thread.ThreadId})";
            }

            var threadStartTime = model.QPCToRelativeMSec(thread.StartTimeQPC);
            var threadEndTime = model.QPCToRelativeMSec(thread.EndTimeQPC);
            var profileThread = new FirefoxProfiler.Thread
            {
                Name = $"{threadIndex} - {threadBaseName}",
                ProcessName = processName,
                ProcessStartupTime = threadStartTime,
                RegisterTime = threadStartTime,
                ProcessShutdownTime = threadEndTime,
                UnregisterTime = threadEndTime,
                ProcessType = "default",
                Pid = $"{process.ProcessId}",
                Tid = $"{thread.ThreadId}",
                ShowMarkersInTimeline = true
            };

            var samples = profileThread.Samples;
            samples.ThreadCPUDelta = new List<int?>();
            samples.TimeDeltas = new List<double>();
            samples.WeightType = "samples";

            threadContexts.Add(new ThreadConvertContext
            {
                Thread = thread,
                ProfileThread = profileThread,
                ProcessModules = processModules,
                ThreadIndex = threadIndex,
                ProfileThreadIndex = _profileThreadIndex,
            });

            ProfilerResult.Threads.Add(profileThread);
            _profileThreadIndex++;
        }

        return new ProcessConvertState
        {
            Process = process,
            ThreadContexts = threadContexts,
            ThreadIndexWithMaxCpuTime = visibilityPlan.ThreadIndexWithMaxCpuTime,
            InitialVisibleThreads = visibilityPlan.InitialVisibleThreads,
            InitialSelectedThreads = visibilityPlan.InitialSelectedThreads,
        };
    }

    /// <summary>
    /// Adds the libs of the modules of <paramref name="process"/> to the profile and classifies its module files.
    /// </summary>
    private ProcessModuleContext LoadModules(EtlProcess process)
    {
        Options.LogProgress?.Invoke($"Loading Modules for process {process.Name} ({process.ProcessId})");

        var model = _model;
        var files = model.ModuleFiles;
        var context = new ProcessModuleContext();

        // Native images and managed modules, in the order TraceLog keeps them: by image base for the former and
        // by module id for the latter, in one list.
        var modules = new List<(ulong Key, int ModuleFileIndex, ulong ImageBase, long ModuleId, bool IsManaged)>(process.LoadedModules.Count + process.ManagedModuleList.Count);
        foreach (var module in process.LoadedModules)
        {
            modules.Add((module.ImageBase, module.ModuleFileIndex, module.ImageBase, (long)module.ImageBase, false));
        }

        foreach (var module in process.ManagedModuleList)
        {
            modules.Add(((ulong)module.ModuleId, module.ModuleFileIndex, 0, module.ModuleId, true));
        }

        var ordered = modules.OrderBy(m => m.Key).ToList();

        // Non-managed modules by path, so that the native image of an assembly is categorized as managed too.
        var nativeModuleFilesByPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in ordered)
        {
            if (!module.IsManaged)
            {
                var path = files[module.ModuleFileIndex].FilePath;
                if (!nativeModuleFilesByPath.TryGetValue(path, out var bucket))
                {
                    bucket = new List<int>();
                    nativeModuleFilesByPath.Add(path, bucket);
                }

                bucket.Add(module.ModuleFileIndex);
            }
        }

        foreach (var module in ordered)
        {
            var file = files[module.ModuleFileIndex];
            if (!_mapModuleFileIndexToLib.ContainsKey(module.ModuleFileIndex))
            {
                var lib = new FirefoxProfiler.Lib
                {
                    Name = file.Name,
                    AddressStart = module.ImageBase,
                    AddressEnd = module.IsManaged ? 0 : file.ImageEnd,
                    Path = file.FilePath,
                    DebugPath = file.PdbName,
                    DebugName = file.PdbName,
                    BreakpadId = $"0x{module.ModuleId:X16}",
                    Arch = "x64" // TODO
                };

                _mapModuleFileIndexToLib.Add(module.ModuleFileIndex, ProfilerResult.Libs.Count);
                ProfilerResult.Libs.Add(lib);
            }

            var fileName = Path.GetFileName(file.FilePath);
            if (fileName.Equals("clrjit.dll", StringComparison.OrdinalIgnoreCase))
            {
                context.ClrJitModuleFile = module.ModuleFileIndex;
            }
            else if (fileName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
            {
                context.CoreClrModuleFile = module.ModuleFileIndex;
            }

            if (module.IsManaged)
            {
                context.ManagedModuleFiles.Add(module.ModuleFileIndex);

                // Lets JIT compile markers name the assembly of the compiled method from the ModuleID the runtime
                // reports on MethodJittingStarted. Modules are in load order, so a runtime reusing a ModuleID after
                // an unload maps it to the module loaded last.
                if (file.HasFilePath)
                {
                    context.ManagedModuleFileNames[module.ModuleId] = Path.GetFileName(file.FilePath);
                }

                if (nativeModuleFilesByPath.TryGetValue(file.FilePath, out var otherModules))
                {
                    foreach (var otherModule in otherModules)
                    {
                        context.ManagedModuleFiles.Add(otherModule);
                    }
                }
            }
        }

        return context;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Per-thread build
    // ---------------------------------------------------------------------------------------------------------

    private void BuildThread(ThreadConvertContext context)
    {
        var thread = context.Thread;
        var profileThread = context.ProfileThread;
        var samples = profileThread.Samples;

        // Samples: the ones whose stack was walked, with the same time and CPU deltas the event-ordered
        // conversion computed (a context switch into the thread resets the CPU delta origin, a converted sample
        // does too).
        var times = thread.SampleTimesMs;
        var lastSwitchIns = thread.SampleLastSwitchInMs;
        var stacks = thread.SampleStacks;
        var count = times.Count;
        var startTime = 0.0;
        var switchTime = 0.0;
        for (var i = 0; i < count; i++)
        {
            var stackNode = stacks[i];
            if (stackNode < 0)
            {
                continue;
            }

            var time = times[i];
            if (lastSwitchIns[i] > switchTime)
            {
                switchTime = lastSwitchIns[i];
            }

            var firefoxStackIndex = ConvertStack(stackNode, context);
            samples.TimeDeltas!.Add(time - startTime);
            samples.Stack.Add(firefoxStackIndex);
            var cpuDeltaNs = (long)((time - switchTime) * 1_000_000.0);
            samples.ThreadCPUDelta!.Add(cpuDeltaNs > 0 ? (int)cpuDeltaNs : 0);
            samples.Length++;
            switchTime = time;
            startTime = time;
        }

        // Markers.
        var markers = profileThread.Markers;
        foreach (var marker in thread.Markers)
        {
            var (name, category, phase) = marker.Kind switch
            {
                EtlMarkerKind.JitCompile => ("JitCompile", CategoryJit, FirefoxProfiler.MarkerPhase.Interval),
                EtlMarkerKind.GcHeapStats => ("GCHeapStats", CategoryGc, FirefoxProfiler.MarkerPhase.Instance),
                EtlMarkerKind.GcAllocationTick => ($"{context.ThreadIndex} - GC Alloc ({thread.ThreadId})", CategoryGc, FirefoxProfiler.MarkerPhase.Instance),
                EtlMarkerKind.GcEvent => ("GC Event", CategoryGc, FirefoxProfiler.MarkerPhase.Interval),
                EtlMarkerKind.GcConcurrent => ("GC Concurrent", CategoryGc, FirefoxProfiler.MarkerPhase.Interval),
                EtlMarkerKind.GcSuspendEE => ("GC Suspend EE", CategoryGc, FirefoxProfiler.MarkerPhase.Interval),
                EtlMarkerKind.GcRestartEE => ("GC Restart EE", CategoryGc, FirefoxProfiler.MarkerPhase.Interval),
                _ => throw new InvalidOperationException($"Unknown marker kind {marker.Kind}"),
            };

            markers.StartTime.Add(marker.StartTimeMs);
            markers.EndTime.Add(marker.EndTimeMs);
            markers.Category.Add(category);
            markers.Phase.Add(phase);
            markers.ThreadId.Add(context.ProfileThreadIndex);
            markers.Name.Add(GetOrCreateString(name, context));
            markers.Data.Add(marker.Payload);
            markers.Length++;
        }

        UltraConverterToFirefoxEtw.RepairIsolatedIncompleteStacks(profileThread, context.MapFrameToGcSubtreeFrame);

        // The thread's samples are not needed anymore; release them while other threads are still being built.
        thread.SampleTimesMs.Clear();
        thread.SampleTimesMs.TrimExcess();
        thread.SampleLastSwitchInMs.Clear();
        thread.SampleLastSwitchInMs.TrimExcess();
        thread.SampleStacks.Clear();
        thread.SampleStacks.TrimExcess();
    }

    /// <summary>Converts a stack node of the model to a Firefox stack, converting its callers first.</summary>
    private int ConvertStack(int stackNode, ThreadConvertContext context)
    {
        if (stackNode < 0)
        {
            return -1;
        }

        if (context.MapStackNodeToFirefox.TryGetValue(stackNode, out var index))
        {
            return index;
        }

        var firefoxParentStackIndex = ConvertStack(_model.Stacks.Parent(stackNode), context);
        return ConvertStack(stackNode, firefoxParentStackIndex, context);
    }

    private int ConvertStack(int stackNode, int firefoxParentStackIndex, ThreadConvertContext context)
    {
        if (context.MapStackNodeToFirefox.TryGetValue(stackNode, out var index))
        {
            return index;
        }

        var stackTable = context.ProfileThread.StackTable;
        var firefoxStackIndex = stackTable.Length;
        context.MapStackNodeToFirefox.Add(stackNode, firefoxStackIndex);

        var codeAddressIndex = _model.Stacks.CodeAddress(stackNode);
        var frameTableIndex = ConvertFrame(codeAddressIndex, context, out var category, out var subCategory);

        // Propagate GC categorization down a collection subtree (see UltraConverterToFirefox.IsGcSubtreeRootFunction),
        // kernel frames excepted: a syscall made from GC code is still kernel work, not GC work.
        var parentInGcSubtree = firefoxParentStackIndex >= 0 && context.StackInGcSubtree[firefoxParentStackIndex];
        var inGcSubtree = parentInGcSubtree || IsGcSubtreeRootFrame(frameTableIndex, context);
        if (inGcSubtree && category != CategoryKernel && category != CategoryGc)
        {
            frameTableIndex = GetOrCreateGcSubtreeFrame(context.ProfileThread.FrameTable, frameTableIndex, context.MapFrameToGcSubtreeFrame);
            category = CategoryGc;
            subCategory = 0;
        }

        stackTable.Frame.Add(frameTableIndex);
        stackTable.Category.Add(category);
        stackTable.Subcategory.Add(subCategory);
        stackTable.Prefix.Add(firefoxParentStackIndex < 0 ? null : firefoxParentStackIndex);
        stackTable.Length++;

        context.StackInGcSubtree.Add(inGcSubtree);

        return firefoxStackIndex;
    }

    private static bool IsGcSubtreeRootFrame(int frameTableIndex, ThreadConvertContext context)
    {
        var profileThread = context.ProfileThread;
        var funcIndex = profileThread.FrameTable.Func[frameTableIndex];
        var nameIndex = profileThread.FuncTable.Name[funcIndex];
        return IsGcSubtreeRootFunction(profileThread.StringArray[nameIndex]);
    }

    /// <summary>The module file of a code address: the one it was attributed to, or its method's, like <c>TraceCodeAddresses.ModuleFileIndex</c>.</summary>
    private int GetModuleFileIndex(int codeAddressIndex)
    {
        var codeAddresses = _model.CodeAddresses;
        var moduleFileIndex = codeAddresses.ModuleFileIndex[codeAddressIndex];
        if (moduleFileIndex < 0)
        {
            var methodIndex = codeAddresses.MethodIndex[codeAddressIndex];
            if (methodIndex >= 0)
            {
                moduleFileIndex = _methods[methodIndex].ModuleFileIndex;
            }
        }

        return moduleFileIndex;
    }

    private int ConvertFrame(int codeAddressIndex, ThreadConvertContext context, out int category, out int subCategory)
    {
        var profileThread = context.ProfileThread;
        var frameTable = profileThread.FrameTable;

        if (context.MapCodeAddressToFrame.TryGetValue(codeAddressIndex, out var firefoxFrameTableIndex))
        {
            category = frameTable.Category[firefoxFrameTableIndex]!.Value;
            subCategory = frameTable.Subcategory[firefoxFrameTableIndex]!.Value;
            return firefoxFrameTableIndex;
        }

        firefoxFrameTableIndex = frameTable.Length;
        context.MapCodeAddressToFrame.Add(codeAddressIndex, firefoxFrameTableIndex);

        var model = _model;
        var moduleFileIndex = GetModuleFileIndex(codeAddressIndex);
        var moduleFile = moduleFileIndex >= 0 ? model.ModuleFiles[moduleFileIndex] : null;
        var absoluteAddress = model.CodeAddresses.Address[codeAddressIndex];
        var offsetIntoModule = moduleFile is not null ? (int)(absoluteAddress - moduleFile.ImageBase) : 0;

        frameTable.Address.Add(offsetIntoModule);
        frameTable.InlineDepth.Add(0);

        var isManaged = moduleFileIndex >= 0 && context.ProcessModules.ManagedModuleFiles.Contains(moduleFileIndex);

        subCategory = 0;
        if (isManaged)
        {
            category = CategoryManaged;
        }
        else
        {
            var isKernel = (absoluteAddress >> 56) == 0xFF;
            category = isKernel ? CategoryKernel : CategoryNative;

            if (moduleFileIndex >= 0)
            {
                if (moduleFileIndex == context.ProcessModules.ClrJitModuleFile)
                {
                    category = CategoryJit;
                }
                else if (moduleFileIndex == context.ProcessModules.CoreClrModuleFile)
                {
                    category = CategoryClr;
                }
            }
        }

        var methodIndex = model.CodeAddresses.MethodIndex[codeAddressIndex];
        var firefoxMethodIndex = ConvertMethod(codeAddressIndex, methodIndex, moduleFileIndex, context);

        if (methodIndex >= 0)
        {
            var nameIndex = profileThread.FuncTable.Name[firefoxMethodIndex];
            var fullMethodName = profileThread.StringArray[nameIndex];
            // Hack to distinguish GC methods
            // https://github.com/dotnet/runtime/blob/af3393d3991b7aab608e514e4a4be3ae2bbafbf8/src/coreclr/gc/gc.cpp#L49-L53
            // Mono's SGen collector exports its own C functions with a `sgen_` prefix instead.
            var isGC = fullMethodName.StartsWith("WKS::gc", StringComparison.OrdinalIgnoreCase)
                       || fullMethodName.StartsWith("SVR::gc", StringComparison.OrdinalIgnoreCase)
                       || fullMethodName.StartsWith("sgen_", StringComparison.OrdinalIgnoreCase);
            if (isGC)
            {
                category = CategoryGc;
            }
        }

        frameTable.Category.Add(category);
        frameTable.Subcategory.Add(subCategory);
        frameTable.Func.Add(firefoxMethodIndex);
        frameTable.NativeSymbol.Add(null);
        frameTable.InnerWindowID.Add(null);
        frameTable.Implementation.Add(null);
        frameTable.Line.Add(null);
        frameTable.Column.Add(null);
        frameTable.Length++;

        return firefoxFrameTableIndex;
    }

    private int ConvertMethod(int codeAddressIndex, int methodIndex, int moduleFileIndex, ThreadConvertContext context)
    {
        var profileThread = context.ProfileThread;
        var funcTable = profileThread.FuncTable;
        int firefoxMethodIndex;
        if (methodIndex < 0)
        {
            if (context.MapCodeAddressToFunc.TryGetValue(codeAddressIndex, out var index))
            {
                return index;
            }

            firefoxMethodIndex = funcTable.Length;
            context.MapCodeAddressToFunc[codeAddressIndex] = firefoxMethodIndex;
        }
        else if (context.MapMethodToFunc.TryGetValue(methodIndex, out var index))
        {
            return index;
        }
        else
        {
            firefoxMethodIndex = funcTable.Length;
            context.MapMethodToFunc.Add(methodIndex, firefoxMethodIndex);
        }

        var model = _model;
        if (methodIndex < 0)
        {
            var moduleName = moduleFileIndex >= 0 ? model.ModuleFiles[moduleFileIndex].Name : null;
            var absoluteAddress = model.CodeAddresses.Address[codeAddressIndex];
            funcTable.Name.Add(GetOrCreateString($"{moduleName}!0x{absoluteAddress:X16}", context));
        }
        else
        {
            funcTable.Name.Add(GetOrCreateString(_methods[methodIndex].FullName, context));
        }

        funcTable.IsJS.Add(false);
        funcTable.RelevantForJS.Add(false);
        // An address without a resolved symbol still belongs to its module (native library or managed assembly),
        // so it is attributed to the module's resource like a resolved one.
        funcTable.Resource.Add(GetOrCreateResource(moduleFileIndex, context));
        funcTable.FileName.Add(null);
        funcTable.LineNumber.Add(null);
        funcTable.ColumnNumber.Add(null);
        funcTable.Length++;

        return firefoxMethodIndex;
    }

    private int GetOrCreateResource(int moduleFileIndex, ThreadConvertContext context)
    {
        // A module file that is not a loaded module of the process (e.g. a kernel module) has no lib to point
        // to, so its functions stay without a resource.
        if (moduleFileIndex < 0 || !_mapModuleFileIndexToLib.TryGetValue(moduleFileIndex, out var firefoxLibIndex))
        {
            return -1;
        }

        if (context.MapModuleFileToResource.TryGetValue(moduleFileIndex, out var resourceIndex))
        {
            return resourceIndex;
        }

        var resourceTable = context.ProfileThread.ResourceTable;
        resourceIndex = resourceTable.Length;
        context.MapModuleFileToResource.Add(moduleFileIndex, resourceIndex);

        var moduleName = Path.GetFileName(_model.ModuleFiles[moduleFileIndex].FilePath);
        resourceTable.Name.Add(GetOrCreateString(moduleName, context));
        resourceTable.Lib.Add(firefoxLibIndex);
        resourceTable.Host.Add(null);
        resourceTable.Type.Add(1); // 1 = library https://github.com/firefox-devtools/profiler/blob/main/src/types/profile.js (resourceTypes)
        resourceTable.Length++;

        return resourceIndex;
    }

    private static int GetOrCreateString(string text, ThreadConvertContext context)
    {
        if (context.MapStringToFirefox.TryGetValue(text, out var index))
        {
            return index;
        }

        var profileThread = context.ProfileThread;
        var firefoxStringIndex = profileThread.StringArray.Count;
        context.MapStringToFirefox.Add(text, firefoxStringIndex);
        profileThread.StringArray.Add(text);
        return firefoxStringIndex;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Per-process finalization
    // ---------------------------------------------------------------------------------------------------------

    private void FinalizeProcess(ProcessConvertState state)
    {
        var process = state.Process;

        ProfilerResult.Meta.InitialVisibleThreads!.AddRange(state.InitialVisibleThreads);
        ProfilerResult.Meta.InitialSelectedThreads!.AddRange(state.InitialSelectedThreads);

        // If we have GCHeapStatsEvents, we can create a Memory track
        if (process.GcHeapStatsEvents.Count > 0)
        {
            var gcHeapStatsEvents = process.GcHeapStatsEvents;
            gcHeapStatsEvents.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

            var gcHeapStatsCounter = new FirefoxProfiler.Counter()
            {
                Name = "GCHeapStats",
                Category = "Memory", // Category must be Memory otherwise it won't be displayed
                Description = "GC Heap Stats",
                Color = FirefoxProfiler.ProfileColor.Orange, // Doesn't look like it is used
                Pid = $"{process.ProcessId}",
                MainThreadIndex = state.ThreadIndexWithMaxCpuTime,
            };

            gcHeapStatsCounter.Samples.Time = new();

            ProfilerResult.Counters ??= new();
            ProfilerResult.Counters.Add(gcHeapStatsCounter);

            long previousTotalHeapSize = 0;

            // Bug in Memory, they discard the first sample
            // and it is then not recording the first TotalHeapSize which is the initial value
            // So we force to create a dummy empty entry
            // https://github.com/firefox-devtools/profiler/blob/e9fe870f2a85b1c8771b1d671eb316bd1f5723ec/src/profile-logic/profile-data.js#L1732-L1753
            gcHeapStatsCounter.Samples.Time!.Add(0);
            gcHeapStatsCounter.Samples.Count.Add(0);
            gcHeapStatsCounter.Samples.Length++;

            foreach (var evt in gcHeapStatsEvents)
            {
                gcHeapStatsCounter.Samples.Time!.Add(evt.TimeMs);
                // The memory track is special and is assuming a delta
                var deltaMemory = evt.Event.TotalHeapSize - previousTotalHeapSize;
                gcHeapStatsCounter.Samples.Count.Add(deltaMemory);
                gcHeapStatsCounter.Samples.Length++;
                previousTotalHeapSize = evt.Event.TotalHeapSize;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Profile
    // ---------------------------------------------------------------------------------------------------------

    private FirefoxProfiler.Profile CreateProfile()
    {
        var model = _model;
        var platform = $"{model.OSName} {model.OSVersion} {model.OSBuild}";
        var profile = new FirefoxProfiler.Profile
        {
            Meta =
            {
                StartTime = double.MaxValue,
                EndTime = 0.0f,
                ProfilingStartTime = double.MaxValue,
                ProfilingEndTime = 0.0f,
                Version = 29,
                PreprocessedProfileVersion = 51,
                Product = string.Empty,
                InitialSelectedThreads = [],
                Platform = platform,
                Oscpu = platform,
                LogicalCPUs = model.NumberOfProcessors,
                DoesNotUseFrameImplementation = true,
                Symbolicated = true,
                SampleUnits = new FirefoxProfiler.SampleUnits
                {
                    Time = "ms",
                    EventDelay = "ms",
                    ThreadCPUDelta = "ns"
                },
                InitialVisibleThreads = [],
                Stackwalk = 1,
                Interval = model.SampleIntervalMs,
                Categories =
                [
                    new FirefoxProfiler.Category()
                    {
                        Name = "Other",
                        Color = FirefoxProfiler.ProfileColor.Grey,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = "Kernel",
                        Color = FirefoxProfiler.ProfileColor.Orange,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = "Native",
                        Color = FirefoxProfiler.ProfileColor.Blue,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET",
                        Color = FirefoxProfiler.ProfileColor.Green,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET GC",
                        Color = FirefoxProfiler.ProfileColor.Yellow,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET JIT",
                        Color = FirefoxProfiler.ProfileColor.Purple,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET CLR",
                        Color = FirefoxProfiler.ProfileColor.Grey,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                ],
                Abi = RuntimeInformation.RuntimeIdentifier,
                MarkerSchema =
                {
                    JitCompileEvent.Schema(),
                    GCEvent.SchemaMajor(),
                    GCEvent.SchemaMinor(),
                    GCHeapStatsEvent.Schema(),
                    GCAllocationTickEvent.Schema(),
                    GCSuspendExecutionEngineEvent.Schema(),
                    GCRestartExecutionEngineEvent.Schema(),
                    GCConcurrentEvent.Schema(),
                }
            }
        };

        return profile;
    }
}

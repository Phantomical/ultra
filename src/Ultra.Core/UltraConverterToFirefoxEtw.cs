// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ByteSizeLib;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Ultra.Core.Markers;

namespace Ultra.Core;

/// <summary>
/// Converts an ETW trace file to a Firefox profile.
/// </summary>
internal class UltraConverterToFirefoxEtw : UltraConverterToFirefox
{
    private const int MaxIncompleteStackDepthForRepair = 4;
    private const int MaxBrokenStackFragmentDepthForRepair = 16;
    private const int MinCommonPrefixDepthForRepair = 4;
    private const int MinCommonPrefixDepthAdvantageForRepair = 2;

    // How often (in events) phase 2 of ConvertImpl reports progress. Picked to be frequent enough that a
    // multi-GB trace still produces visible progress output, while being rare enough that the modulo check
    // and the LogStepProgress call it occasionally allows are not themselves measurable overhead.
    private const int ProgressLogEventInterval = 1_000_000;

    // Concurrent lookups allowed against any one remote symbol server. Local elements are not throttled.
    private const int MaxConcurrentLookupsPerSymbolServer = 4;

    // Shared across all processes and never cleared (deliberately - ModuleFileIndex values are
    // unique across the whole trace, and re-adding an already-known module is a no-op guarded by
    // ContainsKey below), so it does not need to be per-process context state.
    private readonly Dictionary<ModuleFileIndex, int> _mapModuleFileIndexToFirefox;
    private readonly SymbolReader _symbolReader;
    private readonly string _symbolPathText;
    private readonly TraceLog _traceLog;
    private readonly Dictionary<int, string> _threadNames;
    private int _profileThreadIndex;

    public UltraConverterToFirefoxEtw(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options) : base(traceFiles, options)
    {
        _traceLog = TraceLog.OpenOrConvert(traceFiles[0].FileName);

        ProfilerResult = CreateProfile();

        var symbolPath = options.GetCachedSymbolPath();
        _symbolPathText = symbolPath.ToString();

        _symbolReader = CreateSymbolReader();

        _mapModuleFileIndexToFirefox = new();
        _threadNames = new();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _symbolReader.Dispose();
        _traceLog.Dispose();
    }

    /// <summary>
    /// Creates a <see cref="SymbolReader"/> over the configured symbol path.
    /// </summary>
    /// <remarks>
    /// A <see cref="SymbolReader"/> keeps unsynchronized caches internally, so it cannot be shared
    /// across threads. Readers do share the on-disk symbol cache named by the symbol path.
    /// </remarks>
    private SymbolReader CreateSymbolReader() => CreateSymbolReader(_symbolPathText);

    /// <summary>
    /// Creates a <see cref="SymbolReader"/> over <paramref name="symbolPathText"/>.
    /// </summary>
    /// <param name="symbolPathText">The symbol path the reader should search.</param>
    private static SymbolReader CreateSymbolReader(string symbolPathText)
        => new(TextWriter.Null, symbolPathText)
        {
            Options = SymbolReaderOptions.None,
            SecurityCheck = static _ => true,
        };

    private protected override void ConvertImpl(List<int> processIds)
    {
        // Deduplicate incoming process ids, preserving first-occurrence order, before the setup loop
        // below. Callers can pass the same id more than once (e.g. repeated --pid flags collected as-is
        // in Program.cs). TraceProcesses.LastProcessWithID(id) returns the SAME TraceProcess instance for
        // a repeated id, and TraceThread does not override Equals/GetHashCode, so without this dedup
        // SetupProcess would call threadContexts.Add(thread, context) a second time with an identical key
        // and throw ArgumentException - after the entire (possibly long) profiling run has already
        // completed.
        if (processIds.Count > 1)
        {
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
        }

        _profileThreadIndex = 0;

        var threadContexts = new Dictionary<TraceThread, ThreadConvertContext>();
        var processStates = new List<ProcessConvertState>(processIds.Count);

        // Phase 1: Setup, per process, in the same order as today. This creates every
        // FirefoxProfiler.Thread and its per-thread context up front, so that phase 2 below can
        // run as a single pass over the whole trace instead of one pass per thread.
        foreach (var processId in processIds)
        {
            var process = _traceLog.Processes.LastProcessWithID(processId);
            processStates.Add(SetupProcess(process, threadContexts));
        }

        // Phase 2: a single pass over the entire trace. TraceThread.EventsInThread is
        // implemented as Log.Events.Filter(startTimeQPC, endTimeQPC, e => e.ThreadID ==
        // ThreadID) - a linear scan with full per-event decode that merely seeks to the
        // thread's start time, so calling it once per thread (the previous shape of this method)
        // is O(threads x events). Routing every event once to its owning thread's context here
        // makes the whole conversion O(events) total.
        //
        // This phase dominates wall time on a large trace and, unlike phases 1/3, previously emitted no
        // progress output at all while it ran - the status line would sit frozen for minutes and read as
        // a hang. Report progress every ProgressLogEventInterval events instead: a plain counter/modulo
        // check is allocation-free, and the message itself (the only allocation) is only formatted on the
        // rare iterations where it is actually logged.
        long eventsProcessed = 0;
        foreach (var evt in _traceLog.Events)
        {
            eventsProcessed++;
            if (eventsProcessed % ProgressLogEventInterval == 0)
            {
                Options.LogStepProgress?.Invoke($"Converting Events: {eventsProcessed:N0} processed");
            }

            // Thread names come from ThreadSetName kernel events. TraceLog drops many bookkeeping
            // events while building the .etlx (all stack-walk events, Thread/DCStart|DCStop,
            // MethodILToNativeMap, non-jitted MethodLoad*, FileIO/Name, idle-thread PerfInfo/Sample, to
            // name some) - but ThreadSetName is not one of them: its SetupCallbacks handler sets neither
            // bookKeepingEvent nor removeFromStream, so it survives into _traceLog.Events like an
            // ordinary event (this is also how TraceLog itself seeds TraceThread.ThreadInfo). That lets
            // collecting it piggy-back on this single pass instead of requiring a second full pass over
            // the raw .etl file (which is what the previous ETWTraceEventSource.Process() call was
            // solely for). Collection is intentionally unconditional - not scoped to threads we are
            // converting - to match the old behavior of scanning the whole raw file regardless of
            // `processIds`.
            if (evt is ThreadSetNameTraceData threadSetName)
            {
                if (!string.IsNullOrEmpty(threadSetName.ThreadName))
                {
                    _threadNames[threadSetName.ThreadID] = threadSetName.ThreadName;
                }
            }

            // Thread IDs can be reused during a trace. TraceThread carries the timestamped lifetime;
            // ThreadID alone can merge unrelated lifetimes into one Firefox thread. evt.Thread()
            // resolves the owning TraceThread via TraceLogExtensions.Thread() ->
            // Threads.GetThread(ThreadID, TimeStampQPC), which returns the last TraceThread history
            // entry with startTime <= t. That reproduces the LOWER bound of the old per-thread
            // EventsInThread filter (an event is never attributed to a TraceThread that starts after
            // it) but not its upper bound: that enumerator also stopped at `TimeStampQPC >
            // endTimeQPC`, whereas GetThread never consults an end time at all. The upper bound is
            // therefore reapplied explicitly below, so routing stays equivalent to the per-thread pass
            // this loop replaced.
            //
            // Comparing relative milliseconds is exact here: both sides go through the same
            // TraceEventSource.QPCTimeToRelMSec, which is monotonic in QPC, and one QPC tick (1e-4 ms
            // at the usual 10 MHz) is far wider than a double ULP at trace-scale magnitudes. The old
            // enumerator's boundary was inclusive, hence the strict `>` here.
            //
            // This bound does discard real data. Measured on a 1.6 GB / 12.4M-event trace with 0
            // events lost, it drops 998 CPU samples across 88 threads that all carry the profiled
            // process' own ProcessID and sit in kernel teardown code, reaching 143 ms past a thread's
            // Thread/Stop at process exit - Thread/Stop marks when the thread object is signalled, not
            // when the CPU stops running in that thread's context. Keeping them would be a deliberate
            // change rather than a free win: UnregisterTime, ProcessShutdownTime and
            // Meta.ProfilingEndTime all derive from EndTimeRelativeMSec and would have to widen with
            // them, or the profile would advertise thread lifetimes that its own samples fall outside.
            var thread = evt.Thread();
            if (thread is null || !threadContexts.TryGetValue(thread, out var context))
            {
                continue;
            }

            if (evt.TimeStampRelativeMSec > context.ThreadEndTimeRelativeMSec)
            {
                continue;
            }

            ProcessEvent(evt, context);
        }

        // Phase 3: Finalize, per process, in the same order as today. By now _threadNames is
        // fully populated (phase 2 has already scanned every event in the trace), so thread names
        // can be computed here.
        foreach (var state in processStates)
        {
            FinalizeProcess(state);
        }
    }

    /// <summary>
    /// Mutable per-thread conversion state for a single <see cref="FirefoxProfiler.Thread"/>.
    /// These fields used to be instance fields (and loop locals) cleared/reset at the top of each
    /// iteration of a per-thread loop that processed one thread fully before moving to the next.
    /// Now that every thread's events are interleaved by a single time-ordered pass over the
    /// whole trace, each thread needs its own persistent copy instead of one shared, reset-per-
    /// iteration copy - so every thread's context is alive for the whole conversion instead of
    /// one at a time. This trades memory for eliminating the O(threads x events) rescans;
    /// deliberately no pooling/cleverness is added here.
    /// </summary>
    private sealed class ThreadConvertContext
    {
        public ThreadConvertContext(
            TraceProcess process,
            TraceThread thread,
            FirefoxProfiler.Thread profileThread,
            ProcessModuleContext processModules,
            int threadIndex,
            int profileThreadIndex,
            List<(double, GCHeapStatsEvent)> gcHeapStatsEvents,
            Dictionary<long, (JitCompileEvent, double)> jitCompilePendingMethodId)
        {
            Process = process;
            Thread = thread;
            ProfileThread = profileThread;
            ProcessModules = processModules;
            ThreadIndex = threadIndex;
            ProfileThreadIndex = profileThreadIndex;
            GcHeapStatsEvents = gcHeapStatsEvents;
            JitCompilePendingMethodId = jitCompilePendingMethodId;
            ThreadEndTimeRelativeMSec = thread.EndTimeRelativeMSec;
        }

        public TraceProcess Process { get; }
        public TraceThread Thread { get; }
        public FirefoxProfiler.Thread ProfileThread { get; }
        public ProcessModuleContext ProcessModules { get; }

        /// <summary>The position of this thread within its process's CPU-time-sorted thread list.</summary>
        public int ThreadIndex { get; }

        /// <summary>The index of this thread's <see cref="FirefoxProfiler.Thread"/> within <c>ProfilerResult.Threads</c>.</summary>
        public int ProfileThreadIndex { get; }

        /// <summary>
        /// Inclusive upper bound of the thread's lifetime, cached from <see cref="TraceThread.EndTimeRelativeMSec"/>
        /// because it is tested once per event and that property recomputes a division on every read.
        /// </summary>
        public double ThreadEndTimeRelativeMSec { get; }

        /// <summary>
        /// Shared by every thread context of the same process (same list instance); appended to
        /// during the event pass and sorted by timestamp afterwards in <see cref="FinalizeProcess"/>,
        /// exactly as before.
        /// </summary>
        public List<(double, GCHeapStatsEvent)> GcHeapStatsEvents { get; }

        public Dictionary<CallStackIndex, int> MapCallStackIndexToFirefox { get; } = new();
        public Dictionary<CodeAddressIndex, int> MapCodeAddressIndexToFirefox { get; } = new();
        public Dictionary<CodeAddressIndex, int> MapCodeAddressIndexToMethodIndexFirefox { get; } = new();
        public Dictionary<MethodIndex, int> MapMethodIndexToFirefox { get; } = new();
        public Dictionary<string, int> MapStringToFirefox { get; } = new(StringComparer.Ordinal);

        public Stack<(double, GCSuspendExecutionEngineEvent)> GcSuspendEEEvents { get; } = new();
        public Stack<double> GcRestartEEEvents { get; } = new();
        public Stack<(double, GCEvent)> GcStartStopEvents { get; } = new();

        /// <summary>
        /// Shared by every thread context of the same process (same dictionary instance, mirroring
        /// <see cref="GcHeapStatsEvents"/> above) - this must stay per-PROCESS, not per-thread. A
        /// MethodJittingStarted event and the MethodLoadUnload event for the same MethodID are not
        /// guaranteed to occur on the same OS thread (e.g. background tiered-JIT compilation can hand a
        /// method off to a different thread than the one that ends up loading it), so a per-thread
        /// dictionary would silently DROP those cross-thread markers instead of merely reordering them.
        /// The old code (one dictionary per process, one thread converted fully at a time) genuinely did
        /// cross-thread matching, but only worked when the jitting thread happened to be converted before
        /// the loading thread in the old CPU-time-sorted, thread-grouped iteration order - it silently
        /// failed the rest of the time. This single time-ordered pass over the whole trace makes
        /// per-process matching work consistently instead of by accident, so this preserves strictly MORE
        /// markers than either the old ordering-dependent behavior or a per-thread-scoped version of this
        /// same pass would.
        /// </summary>
        public Dictionary<long, (JitCompileEvent, double)> JitCompilePendingMethodId { get; }

        public double StartTime;
        public double SwitchTimeInMsec;

        /// <summary>
        /// Releases the backing storage of this thread's interning dictionaries once
        /// <see cref="ProfileThread"/> is fully built (called from <c>FinalizeProcess</c>, after
        /// <c>RepairIsolatedIncompleteStacks</c>), so it can be collected during the JSON+gzip
        /// serialization that follows instead of staying live - together with every other thread's -
        /// for the rest of the conversion. <see cref="Dictionary{TKey,TValue}.Clear"/> alone would drop
        /// the entries but not shrink the backing arrays; the paired <c>TrimExcess()</c> call (capacity
        /// down to the now-zero count) is what actually releases that memory. No pooling: these
        /// dictionaries are never read again after this point.
        /// </summary>
        public void ReleaseInterningMaps()
        {
            MapCallStackIndexToFirefox.Clear();
            MapCallStackIndexToFirefox.TrimExcess();
            MapCodeAddressIndexToFirefox.Clear();
            MapCodeAddressIndexToFirefox.TrimExcess();
            MapCodeAddressIndexToMethodIndexFirefox.Clear();
            MapCodeAddressIndexToMethodIndexFirefox.TrimExcess();
            MapMethodIndexToFirefox.Clear();
            MapMethodIndexToFirefox.TrimExcess();
            MapStringToFirefox.Clear();
            MapStringToFirefox.TrimExcess();
        }
    }

    /// <summary>
    /// Per-process module classification (managed-module set and clrjit/coreclr module indices)
    /// used while converting frames. <see cref="LoadModules"/> used to reset shared instance
    /// fields for this on every call, which was safe only because processes were converted fully,
    /// one at a time. Now that setup for every process runs before the single shared event pass,
    /// these can no longer be shared mutable instance state - they would end up reflecting only
    /// the last process set up - so each process gets its own instance instead, referenced by
    /// every one of its threads' <see cref="ThreadConvertContext"/>.
    /// </summary>
    private sealed class ProcessModuleContext
    {
        public ProcessModuleContext(HashSet<ModuleFileIndex> managedModules, ModuleFileIndex clrJitModuleIndex, ModuleFileIndex coreClrModuleIndex)
        {
            ManagedModules = managedModules;
            ClrJitModuleIndex = clrJitModuleIndex;
            CoreClrModuleIndex = coreClrModuleIndex;
        }

        public HashSet<ModuleFileIndex> ManagedModules { get; }
        public ModuleFileIndex ClrJitModuleIndex { get; }
        public ModuleFileIndex CoreClrModuleIndex { get; }
    }

    /// <summary>Per-process state carried from the setup phase (1) to the finalize phase (3).</summary>
    private sealed class ProcessConvertState
    {
        public ProcessConvertState(
            TraceProcess process,
            List<ThreadConvertContext> threadContexts,
            List<(double, GCHeapStatsEvent)> gcHeapStatsEvents,
            int threadIndexWithMaxCpuTime,
            List<int> initialVisibleThreads,
            List<int> initialSelectedThreads)
        {
            Process = process;
            ThreadContexts = threadContexts;
            GcHeapStatsEvents = gcHeapStatsEvents;
            ThreadIndexWithMaxCpuTime = threadIndexWithMaxCpuTime;
            InitialVisibleThreads = initialVisibleThreads;
            InitialSelectedThreads = initialSelectedThreads;
        }

        public TraceProcess Process { get; }
        public List<ThreadConvertContext> ThreadContexts { get; }
        public List<(double, GCHeapStatsEvent)> GcHeapStatsEvents { get; }
        public int ThreadIndexWithMaxCpuTime { get; }

        /// <summary>
        /// The profile-thread indices to mark visible by default for this process, computed by
        /// <see cref="ThreadVisibilityPlanner.Plan"/> in <see cref="SetupProcess"/> and appended to
        /// <c>ProfilerResult.Meta.InitialVisibleThreads</c> by <see cref="FinalizeProcess"/>.
        /// </summary>
        public List<int> InitialVisibleThreads { get; }

        /// <summary>
        /// The profile-thread indices to select by default for this process, computed by
        /// <see cref="ThreadVisibilityPlanner.Plan"/> in <see cref="SetupProcess"/> and appended to
        /// <c>ProfilerResult.Meta.InitialSelectedThreads</c> by <see cref="FinalizeProcess"/>.
        /// </summary>
        public List<int> InitialSelectedThreads { get; }
    }

    /// <summary>
    /// Prepares a process for conversion: updates profile-wide metadata, loads its modules and
    /// symbols, and creates a <see cref="FirefoxProfiler.Thread"/> plus a per-thread conversion
    /// context for each of its threads, in the same order threads were processed in before.
    /// </summary>
    /// <param name="process">The process to prepare.</param>
    /// <param name="threadContexts">The shared thread-to-context map that phase 2 routes events through.</param>
    /// <returns>The per-process state needed by <see cref="FinalizeProcess"/>.</returns>
    private ProcessConvertState SetupProcess(TraceProcess process, Dictionary<TraceThread, ThreadConvertContext> threadContexts)
    {
        if (ProfilerResult.Meta.Product == string.Empty)
        {
            ProfilerResult.Meta.Product = process.Name;
        }

        var processStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        var processEndTime = new DateTimeOffset(process.EndTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        if (processStartTime < ProfilerResult.Meta.StartTime)
        {
            ProfilerResult.Meta.StartTime = processStartTime;
        }
        if (processEndTime > ProfilerResult.Meta.EndTime)
        {
            ProfilerResult.Meta.EndTime = processEndTime;
        }

        var profilingStartTime = process.StartTimeRelativeMsec;
        if (profilingStartTime < ProfilerResult.Meta.ProfilingStartTime)
        {
            ProfilerResult.Meta.ProfilingStartTime = profilingStartTime;
        }
        var profilingEndTime = process.EndTimeRelativeMsec;
        if (profilingEndTime > ProfilerResult.Meta.ProfilingEndTime)
        {
            ProfilerResult.Meta.ProfilingEndTime = profilingEndTime;
        }

        var processModules = LoadModules(process);

        // Stays per-process: entries are appended (from every one of the process's threads)
        // during the single event pass in time order rather than thread-grouped order, then
        // sorted by timestamp immediately below in FinalizeProcess exactly as before - so the
        // result is unchanged except possibly for ties.
        List<(double, GCHeapStatsEvent)> gcHeapStatsEvents = new();

        // Shared by every thread context of this process (see
        // ThreadConvertContext.JitCompilePendingMethodId for why this must be per-process, not
        // per-thread).
        Dictionary<long, (JitCompileEvent, double)> jitCompilePendingMethodId = new();

        var originalThreads = process.Threads.ToList();
        var cpuTimesInMs = new double[originalThreads.Count];
        for (var i = 0; i < originalThreads.Count; i++)
        {
            cpuTimesInMs[i] = originalThreads[i].CPUMSec;
        }

        // The CPU-descending thread order, each thread's resulting profile-thread index, and the
        // profile's default visible/selected threads are computed by a pure, independently unit-tested
        // helper (see ThreadVisibilityPlanner) - these are exactly the kind of thing a refactor can
        // silently get wrong without any test noticing, since the only symptom is a reordered/reindexed
        // array deep inside the serialized profile JSON.
        var visibilityPlan = ThreadVisibilityPlanner.Plan(cpuTimesInMs, _profileThreadIndex, Options.MinimumCpuTimeBeforeThreadIsVisibleInMs);

        var processName = $"{process.Name} ({process.ProcessID})";

        var threadContextList = new List<ThreadConvertContext>(originalThreads.Count);

        // Add threads, in the CPU-descending order the plan computed.
        for (var threadIndex = 0; threadIndex < visibilityPlan.SortOrder.Length; threadIndex++)
        {
            var thread = originalThreads[visibilityPlan.SortOrder[threadIndex]];

            // Name depends on _threadNames, which is only fully known after phase 2 has scanned
            // the whole trace (thread names must be known before Thread.Name is computed - see
            // Step 4 of the perf plan). Left empty here and computed for real in
            // FinalizeProcess, once that is guaranteed.
            var profileThread = new FirefoxProfiler.Thread
            {
                Name = string.Empty,
                ProcessName = processName,
                ProcessStartupTime = thread.StartTimeRelativeMSec,
                RegisterTime = thread.StartTimeRelativeMSec,
                ProcessShutdownTime = thread.EndTimeRelativeMSec,
                UnregisterTime = thread.EndTimeRelativeMSec,
                ProcessType = "default",
                Pid = $"{process.ProcessID}",
                Tid = $"{thread.ThreadID}",
                ShowMarkersInTimeline = true
            };

            var samples = profileThread.Samples;
            samples.ThreadCPUDelta = new List<int?>();
            samples.TimeDeltas = new List<double>();
            samples.WeightType = "samples";

            // _profileThreadIndex is the base index passed into ThreadVisibilityPlanner.Plan above, plus
            // the number of threads already added in this loop (threadIndex): i.e. exactly
            // visibilityPlan.ProfileThreadIndexByOriginalIndex[visibilityPlan.SortOrder[threadIndex]].
            var context = new ThreadConvertContext(process, thread, profileThread, processModules, threadIndex, _profileThreadIndex, gcHeapStatsEvents, jitCompilePendingMethodId);

            threadContexts.Add(thread, context);
            threadContextList.Add(context);

            ProfilerResult.Threads.Add(profileThread);

            _profileThreadIndex++;
        }

        return new ProcessConvertState(process, threadContextList, gcHeapStatsEvents, visibilityPlan.ThreadIndexWithMaxCpuTime, visibilityPlan.InitialVisibleThreads, visibilityPlan.InitialSelectedThreads);
    }

    /// <summary>
    /// Applies the conversion logic for a single trace event to the thread that owns it. This is
    /// exactly the per-event body that used to run once per thread inside <c>foreach (var evt in
    /// thread.EventsInThread)</c>, unchanged except that it reads and writes per-thread state
    /// through <paramref name="context"/> instead of instance fields and loop locals.
    /// </summary>
    private void ProcessEvent(TraceEvent evt, ThreadConvertContext context)
    {
        var thread = context.Thread;
        var process = context.Process;
        var profileThread = context.ProfileThread;
        var samples = profileThread.Samples;
        var markers = profileThread.Markers;

        //const TraceEventID GCStartEventID = (TraceEventID) 1;
        //const TraceEventID GCStopEventID = (TraceEventID) 2;
        const TraceEventID GCRestartEEStopEventID = (TraceEventID) 3;
        //const TraceEventID GCHeapStatsEventID = (TraceEventID) 4;
        //const TraceEventID GCCreateSegmentEventID = (TraceEventID) 5;
        //const TraceEventID GCFreeSegmentEventID = (TraceEventID) 6;
        const TraceEventID GCRestartEEStartEventID = (TraceEventID) 7;
        const TraceEventID GCSuspendEEStopEventID = (TraceEventID) 8;
        //const TraceEventID GCSuspendEEStartEventID = (TraceEventID) 9;
        //const TraceEventID GCAllocationTickEventID = (TraceEventID) 10;

        if (evt is not SampledProfileTraceData)
        {
            if (evt.Opcode == (TraceEventOpcode) 0x24 && evt is CSwitchTraceData switchTraceData)
            {
                if (evt.ThreadID == thread.ThreadID && switchTraceData.OldThreadID != thread.ThreadID)
                {
                    // Old Thread -> This Thread
                    // Switch-in
                    context.SwitchTimeInMsec = evt.TimeStampRelativeMSec;
                }
                //else if (evt.ThreadID != thread.ThreadID && switchTraceData.OldThreadID == thread.ThreadID)
                //{
                //    // This Thread -> Other Thread
                //    // Switch-out
                //    switchTimeOutMsec = evt.TimeStampRelativeMSec;
                //}
            }

            if (evt.ThreadID == thread.ThreadID)
            {
                if (evt is MethodJittingStartedTraceData methodJittingStarted)
                {
                    var signature = methodJittingStarted.MethodSignature;
                    var indexOfParent = signature.IndexOf('(');
                    if (indexOfParent >= 0)
                    {
                        signature = signature.Substring(indexOfParent);
                    }

                    var jitCompile = new JitCompileEvent
                    {
                        FullName =
                            $"{methodJittingStarted.MethodNamespace}.{methodJittingStarted.MethodName}{signature}",
                        MethodILSize = methodJittingStarted.MethodILSize
                    };

                    context.JitCompilePendingMethodId[methodJittingStarted.MethodID] =
                        (jitCompile, evt.TimeStampRelativeMSec);
                }
                else if (evt is MethodLoadUnloadTraceDataBase methodLoadUnloadVerbose)
                {
                    if (context.JitCompilePendingMethodId.TryGetValue(methodLoadUnloadVerbose.MethodID,
                            out var jitCompilePair))
                    {
                        context.JitCompilePendingMethodId.Remove(methodLoadUnloadVerbose.MethodID);

                        markers.StartTime.Add(jitCompilePair.Item2);
                        markers.EndTime.Add(evt.TimeStampRelativeMSec);
                        markers.Category.Add(CategoryJit);
                        markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                        markers.ThreadId.Add(context.ProfileThreadIndex);
                        markers.Name.Add(GetOrCreateString("JitCompile", context));
                        markers.Data.Add(jitCompilePair.Item1);
                        markers.Length++;
                    }
                }
                else if (evt is GCHeapStatsTraceData gcHeapStats)
                {
                    markers.StartTime.Add(evt.TimeStampRelativeMSec);
                    markers.EndTime.Add(evt.TimeStampRelativeMSec);
                    markers.Category.Add(CategoryGc);
                    markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                    markers.ThreadId.Add(context.ProfileThreadIndex);
                    markers.Name.Add(GetOrCreateString($"GCHeapStats", context));

                    var heapStatEvent = new GCHeapStatsEvent
                    {
                        TotalHeapSize = gcHeapStats.TotalHeapSize,
                        TotalPromoted = gcHeapStats.TotalPromoted,
                        GenerationSize0 = gcHeapStats.GenerationSize0,
                        TotalPromotedSize0 = gcHeapStats.TotalPromotedSize0,
                        GenerationSize1 = gcHeapStats.GenerationSize1,
                        TotalPromotedSize1 = gcHeapStats.TotalPromotedSize1,
                        GenerationSize2 = gcHeapStats.GenerationSize2,
                        TotalPromotedSize2 = gcHeapStats.TotalPromotedSize2,
                        GenerationSize3 = gcHeapStats.GenerationSize3,
                        TotalPromotedSize3 = gcHeapStats.TotalPromotedSize3,
                        GenerationSize4 = gcHeapStats.GenerationSize4,
                        TotalPromotedSize4 = gcHeapStats.TotalPromotedSize4,
                        FinalizationPromotedSize = gcHeapStats.FinalizationPromotedSize,
                        FinalizationPromotedCount = gcHeapStats.FinalizationPromotedCount,
                        PinnedObjectCount = gcHeapStats.PinnedObjectCount,
                        SinkBlockCount = gcHeapStats.SinkBlockCount,
                        GCHandleCount = gcHeapStats.GCHandleCount
                    };

                    context.GcHeapStatsEvents.Add((evt.TimeStampRelativeMSec, heapStatEvent));

                    markers.Data.Add(heapStatEvent);
                    markers.Length++;
                }
                else if (evt is GCAllocationTickTraceData allocationTick)
                {
                    markers.StartTime.Add(evt.TimeStampRelativeMSec);
                    markers.EndTime.Add(evt.TimeStampRelativeMSec);
                    markers.Category.Add(CategoryGc);
                    markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                    markers.ThreadId.Add(context.ProfileThreadIndex);
                    markers.Name.Add(GetOrCreateString($"{context.ThreadIndex} - GC Alloc ({thread.ThreadID})", context));

                    var allocationTickEvent = new GCAllocationTickEvent
                    {
                        AllocationAmount = allocationTick.AllocationAmount,
                        AllocationKind = allocationTick.AllocationKind switch
                        {
                            GCAllocationKind.Small => "Small",
                            GCAllocationKind.Large => "Large",
                            GCAllocationKind.Pinned => "Pinned",
                            _ => "Unknown"
                        },
                        TypeName = allocationTick.TypeName,
                        HeapIndex = allocationTick.HeapIndex
                    };
                    markers.Data.Add(allocationTickEvent);
                    markers.Length++;
                }
                else if (evt.ProviderGuid == ClrTraceEventParser.ProviderGuid)
                {
                    if (evt is GCStartTraceData gcStart)
                    {
                        var gcEvent = new GCEvent
                        {
                            Reason = gcStart.Reason.ToString(),
                            Count = gcStart.Count,
                            Depth = gcStart.Depth,
                            GCType = gcStart.Type.ToString()
                        };

                        context.GcStartStopEvents.Push((evt.TimeStampRelativeMSec, gcEvent));
                    }
                    else if (evt is GCEndTraceData gcEnd && context.GcStartStopEvents.Count > 0)
                    {
                        var (gcEventStartTime, gcEvent) = context.GcStartStopEvents.Pop();

                        markers.StartTime.Add(gcEventStartTime);
                        markers.EndTime.Add(evt.TimeStampRelativeMSec);
                        markers.Category.Add(CategoryGc);
                        markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                        markers.ThreadId.Add(context.ProfileThreadIndex);
                        markers.Name.Add(GetOrCreateString($"GC Event", context));
                        markers.Data.Add(gcEvent);
                        markers.Length++;
                    }
                    else if (evt is GCSuspendEETraceData gcSuspendEE)
                    {
                        var gcSuspendEEEvent = new GCSuspendExecutionEngineEvent
                        {
                            Reason = gcSuspendEE.Reason.ToString(),
                            Count = gcSuspendEE.Count
                        };

                        context.GcSuspendEEEvents.Push((evt.TimeStampRelativeMSec, gcSuspendEEEvent));
                    }
                    else if (evt.ID == GCSuspendEEStopEventID && evt is GCNoUserDataTraceData &&
                             context.GcSuspendEEEvents.Count > 0)
                    {
                        var (gcSuspendEEEventStartTime, gcSuspendEEEvent) = context.GcSuspendEEEvents.Pop();

                        markers.StartTime.Add(gcSuspendEEEventStartTime);
                        markers.EndTime.Add(evt.TimeStampRelativeMSec);
                        markers.Category.Add(CategoryGc);
                        markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                        markers.ThreadId.Add(context.ProfileThreadIndex);
                        markers.Name.Add(GetOrCreateString($"GC Suspend EE", context));
                        markers.Data.Add(gcSuspendEEEvent);
                        markers.Length++;
                    }
                    else if (evt.ID == GCRestartEEStartEventID && evt is GCNoUserDataTraceData)
                    {
                        context.GcRestartEEEvents.Push(evt.TimeStampRelativeMSec);
                    }
                    else if (evt.ID == GCRestartEEStopEventID && evt is GCNoUserDataTraceData &&
                             context.GcRestartEEEvents.Count > 0)
                    {
                        var gcRestartEEEventStartTime = context.GcRestartEEEvents.Pop();

                        markers.StartTime.Add(gcRestartEEEventStartTime);
                        markers.EndTime.Add(evt.TimeStampRelativeMSec);
                        markers.Category.Add(CategoryGc);
                        markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                        markers.ThreadId.Add(context.ProfileThreadIndex);
                        markers.Name.Add(GetOrCreateString($"GC Restart EE", context));
                        markers.Data.Add(null);
                        markers.Length++;
                    }
                }
            }

            return;
        }

        // This guard used to be redundant with the per-thread `EventsInThread` filtering plus the
        // `ReferenceEquals(evt.Thread(), thread)` check above it; both are now handled by routing
        // through evt.Thread() before this method is even called. It is preserved here verbatim
        // (guarding only the sampled-profile path, as it did before) out of caution, since it is
        // not proven redundant for this specific event type.
        if (evt.ProcessID != process.ProcessID || evt.ThreadID != thread.ThreadID)
        {
            return;
        }

        //Console.WriteLine($"PERF {evt}");

        var callStackIndex = evt.CallStackIndex();
        if (callStackIndex == CallStackIndex.Invalid)
        {
            return;
        }

        if (!ReferenceEquals(_traceLog.CallStacks.Thread(callStackIndex), thread))
        {
            return;
        }

        // Add sample
        var firefoxCallStackIndex = ConvertCallStack(callStackIndex, context);

        var deltaTime = evt.TimeStampRelativeMSec - context.StartTime;
        // TimeDeltas/ThreadCPUDelta are nullable properties but SetupProcess always initializes
        // them (in a different method, so nullable flow analysis can't see it) before phase 2 -
        // the single event pass that calls ProcessEvent - runs.
        samples.TimeDeltas!.Add(deltaTime);
        samples.Stack.Add(firefoxCallStackIndex);
        var cpuDeltaMs = (long) ((evt.TimeStampRelativeMSec - context.SwitchTimeInMsec) * 1_000_000.0);
        if (cpuDeltaMs > 0)
        {
            samples.ThreadCPUDelta!.Add((int) cpuDeltaMs);
        }
        else
        {
            samples.ThreadCPUDelta!.Add(0);
        }

        context.SwitchTimeInMsec = evt.TimeStampRelativeMSec;
        samples.Length++;
        context.StartTime = evt.TimeStampRelativeMSec;
    }

    /// <summary>
    /// Finalizes a process after the single event pass (phase 2) has completed for the whole
    /// trace: computes each thread's final name, repairs incomplete stacks, builds the GC heap
    /// stats memory counter, and marks the process's default visible/selected threads - in the
    /// same per-process order as before.
    /// </summary>
    private void FinalizeProcess(ProcessConvertState state)
    {
        var process = state.Process;

        foreach (var context in state.ThreadContexts)
        {
            var thread = context.Thread;
            var profileThread = context.ProfileThread;

            // _threadNames is guaranteed fully populated at this point (phase 2 has already
            // scanned the entire trace for every process), so this reproduces exactly what used
            // to be computed up-front per thread, before any of its events were processed.
            string threadBaseName;
            if (!_threadNames.TryGetValue(thread.ThreadID, out var dynamicThreadName))
            {
                threadBaseName = thread.ThreadInfo is not null
                    ? $"{thread.ThreadInfo} ({thread.ThreadID})"
                    : $"Thread ({thread.ThreadID})";
            }
            else
            {
                threadBaseName = $"{dynamicThreadName} ({thread.ThreadID})";
            }
            profileThread.Name = $"{context.ThreadIndex} - {threadBaseName}";

            // This thread's events were already converted back in phase 2 (the single combined event
            // pass over the whole trace, which reports its own progress - see ProgressLogEventInterval
            // above). This message must not claim conversion is happening now: it precedes only the
            // finalization work still left for this thread (name already computed above; stack repair
            // and visibility/selection bookkeeping below).
            Options.LogProgress?.Invoke($"Finalizing Thread: {profileThread.Name}");

            RepairIsolatedIncompleteStacks(profileThread);

            // profileThread is now fully built (nothing below touches this thread's interning
            // dictionaries again), so release their backing storage now rather than letting every
            // thread's maps stay alive through the JSON+gzip serialization that follows.
            context.ReleaseInterningMaps();
        }

        // Default visible/selected threads were already computed by ThreadVisibilityPlanner in
        // SetupProcess (see ProcessConvertState.InitialVisibleThreads/InitialSelectedThreads); commit
        // them here, in the same per-process order as before.
        ProfilerResult.Meta.InitialVisibleThreads!.AddRange(state.InitialVisibleThreads);
        ProfilerResult.Meta.InitialSelectedThreads!.AddRange(state.InitialSelectedThreads);

        // If we have GCHeapStatsEvents, we can create a Memory track
        if (state.GcHeapStatsEvents.Count > 0)
        {
            var gcHeapStatsEvents = state.GcHeapStatsEvents;
            gcHeapStatsEvents.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            var gcHeapStatsCounter = new FirefoxProfiler.Counter()
            {
                Name = "GCHeapStats",
                Category = "Memory", // Category must be Memory otherwise it won't be displayed
                Description = "GC Heap Stats",
                Color = FirefoxProfiler.ProfileColor.Orange, // Doesn't look like it is used
                Pid = $"{process.ProcessID}",
                MainThreadIndex = state.ThreadIndexWithMaxCpuTime,
            };

            //gcHeapStatsCounter.Samples.Number = new();
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
                gcHeapStatsCounter.Samples.Time!.Add(evt.Item1);
                // The memory track is special and is assuming a delta
                var deltaMemory = evt.Item2.TotalHeapSize - previousTotalHeapSize;
                gcHeapStatsCounter.Samples.Count.Add(deltaMemory);
                gcHeapStatsCounter.Samples.Length++;
                previousTotalHeapSize = evt.Item2.TotalHeapSize;
            }
        }
    }

    // ETW stack walking can occasionally produce a very short user stack, or a short broken
    // native/kernel fragment, between two samples that share a deep caller prefix. Graft useful
    // partial stacks onto the surrounding common prefix, and discard fragments that only contain
    // an unresolved/kernel root, so Firefox Profiler does not render a single broken sample as a
    // full call-stack change.
    internal static int RepairIsolatedIncompleteStacks(FirefoxProfiler.Thread profileThread)
    {
        var samples = profileThread.Samples;
        var sampleStacks = samples.Stack;
        var sampleCount = Math.Min(samples.Length, sampleStacks.Count);
        if (sampleCount < 3)
        {
            return 0;
        }

        var stackTable = profileThread.StackTable;
        var originalStackTableLength = stackTable.Length;
        if (originalStackTableLength == 0)
        {
            return 0;
        }

        var originalSampleStacks = new int?[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            originalSampleStacks[i] = sampleStacks[i];
        }

        var stackDepths = new int[originalStackTableLength];
        Array.Fill(stackDepths, -1);

        Dictionary<StackEntryKey, int>? stackEntryMap = null;
        var repairedCount = 0;

        var sampleIndex = 1;
        while (sampleIndex < sampleCount - 1)
        {
            if (!IsIncompleteStackRepairCandidate(originalSampleStacks[sampleIndex], originalStackTableLength, stackDepths, profileThread))
            {
                sampleIndex++;
                continue;
            }

            var runStart = sampleIndex;
            do
            {
                sampleIndex++;
            }
            while (sampleIndex < sampleCount - 1 && IsIncompleteStackRepairCandidate(originalSampleStacks[sampleIndex], originalStackTableLength, stackDepths, profileThread));

            var runEnd = sampleIndex;
            if (runStart == 0 || runEnd >= sampleCount)
            {
                continue;
            }

            var leftStack = originalSampleStacks[runStart - 1];
            var rightStack = originalSampleStacks[runEnd];
            if (leftStack is null || rightStack is null)
            {
                continue;
            }

            if (IsIncompleteStackRepairCandidate(leftStack, originalStackTableLength, stackDepths, profileThread) ||
                IsIncompleteStackRepairCandidate(rightStack, originalStackTableLength, stackDepths, profileThread))
            {
                continue;
            }

            var commonPrefix = FindCommonPrefixStack(leftStack.Value, rightStack.Value, profileThread);
            if (commonPrefix.StackIndex < 0 || commonPrefix.Depth < MinCommonPrefixDepthForRepair)
            {
                continue;
            }

            var commonPrefixNodes = GetStackNodes(commonPrefix.StackIndex, profileThread);
            var canRepairRun = true;
            for (var i = runStart; i < runEnd; i++)
            {
                var stackIndex = originalSampleStacks[i]!.Value;
                var stackDepth = GetStackDepth(stackIndex, stackDepths, profileThread);
                if (stackDepth + MinCommonPrefixDepthAdvantageForRepair > commonPrefix.Depth)
                {
                    canRepairRun = false;
                    break;
                }

                if (IsStackPrefix(GetStackNodes(stackIndex, profileThread), commonPrefixNodes, profileThread))
                {
                    canRepairRun = false;
                    break;
                }
            }

            if (!canRepairRun)
            {
                continue;
            }

            for (var i = runStart; i < runEnd; i++)
            {
                var stackIndex = originalSampleStacks[i]!.Value;
                var incompleteStackNodes = GetStackNodes(stackIndex, profileThread);
                int repairedStackIndex;
                if (ShouldDiscardIncompleteStackFragment(incompleteStackNodes, profileThread))
                {
                    repairedStackIndex = commonPrefix.StackIndex;
                }
                else
                {
                    stackEntryMap ??= CreateStackEntryMap(profileThread);
                    repairedStackIndex = GraftIncompleteStack(stackIndex, commonPrefix.StackIndex, commonPrefixNodes, profileThread, stackEntryMap);
                }

                if (repairedStackIndex != stackIndex)
                {
                    sampleStacks[i] = repairedStackIndex;
                    repairedCount++;
                }
            }
        }

        return repairedCount;
    }

    private static bool IsIncompleteStackRepairCandidate(int? stackIndex, int originalStackTableLength, int[] stackDepths, FirefoxProfiler.Thread profileThread)
    {
        if (stackIndex is null || stackIndex.Value < 0 || stackIndex.Value >= originalStackTableLength)
        {
            return false;
        }

        var depth = GetStackDepth(stackIndex.Value, stackDepths, profileThread);
        if (depth <= 0)
        {
            return false;
        }

        if (depth <= MaxIncompleteStackDepthForRepair)
        {
            return true;
        }

        return depth <= MaxBrokenStackFragmentDepthForRepair &&
               ShouldDiscardIncompleteStackFragment(GetStackNodes(stackIndex.Value, profileThread), profileThread);
    }

    private static bool ShouldDiscardIncompleteStackFragment(List<int> stackNodes, FirefoxProfiler.Thread profileThread)
    {
        if (stackNodes.Count == 0)
        {
            return false;
        }

        foreach (var stackNode in stackNodes)
        {
            var category = GetStackNodeCategory(stackNode, profileThread);
            if (category is CategoryManaged or CategoryGc or CategoryJit or CategoryClr)
            {
                return false;
            }
        }

        var rootStackNode = stackNodes[0];
        var rootCategory = GetStackNodeCategory(rootStackNode, profileThread);
        return rootCategory == CategoryKernel || IsUnknownModuleAddressFrame(rootStackNode, profileThread);
    }

    private static int? GetStackNodeCategory(int stackIndex, FirefoxProfiler.Thread profileThread)
    {
        var stackTable = profileThread.StackTable;
        var category = stackTable.Category[stackIndex];
        if (category is not null)
        {
            return category;
        }

        return profileThread.FrameTable.Category[stackTable.Frame[stackIndex]];
    }

    private static bool IsUnknownModuleAddressFrame(int stackIndex, FirefoxProfiler.Thread profileThread)
    {
        var stackTable = profileThread.StackTable;
        var frameIndex = stackTable.Frame[stackIndex];
        var funcIndex = profileThread.FrameTable.Func[frameIndex];
        var stringIndex = profileThread.FuncTable.Name[funcIndex];
        var frameName = profileThread.StringArray[stringIndex];
        return frameName.StartsWith("!0x", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetStackDepth(int stackIndex, int[] stackDepths, FirefoxProfiler.Thread profileThread)
    {
        if (stackDepths[stackIndex] >= 0)
        {
            return stackDepths[stackIndex];
        }

        var stackTable = profileThread.StackTable;
        var depth = 0;
        var currentStackIndex = stackIndex;
        while (currentStackIndex >= 0)
        {
            if (currentStackIndex < stackDepths.Length && stackDepths[currentStackIndex] >= 0)
            {
                depth += stackDepths[currentStackIndex];
                break;
            }

            depth++;
            currentStackIndex = stackTable.Prefix[currentStackIndex] ?? -1;
        }

        stackDepths[stackIndex] = depth;
        return depth;
    }

    private static StackPrefix FindCommonPrefixStack(int leftStackIndex, int rightStackIndex, FirefoxProfiler.Thread profileThread)
    {
        var leftStackNodes = GetStackNodes(leftStackIndex, profileThread);
        var rightStackNodes = GetStackNodes(rightStackIndex, profileThread);
        var commonDepth = 0;
        var commonStackIndex = -1;
        var count = Math.Min(leftStackNodes.Count, rightStackNodes.Count);
        for (var i = 0; i < count; i++)
        {
            if (!SameStackFrame(leftStackNodes[i], rightStackNodes[i], profileThread))
            {
                break;
            }

            commonDepth++;
            commonStackIndex = leftStackNodes[i];
        }

        return new StackPrefix(commonStackIndex, commonDepth);
    }

    private static int GraftIncompleteStack(int incompleteStackIndex, int prefixStackIndex, List<int> prefixStackNodes, FirefoxProfiler.Thread profileThread, Dictionary<StackEntryKey, int> stackEntryMap)
    {
        var incompleteStackNodes = GetStackNodes(incompleteStackIndex, profileThread);
        var overlap = FindPrefixOverlap(prefixStackNodes, incompleteStackNodes, profileThread);
        var currentPrefixStackIndex = prefixStackIndex;
        for (var i = overlap; i < incompleteStackNodes.Count; i++)
        {
            currentPrefixStackIndex = GetOrCreateStackEntry(incompleteStackNodes[i], currentPrefixStackIndex, profileThread, stackEntryMap);
        }

        return currentPrefixStackIndex;
    }

    private static int FindPrefixOverlap(List<int> prefixStackNodes, List<int> incompleteStackNodes, FirefoxProfiler.Thread profileThread)
    {
        var maximumOverlap = Math.Min(prefixStackNodes.Count, incompleteStackNodes.Count);
        for (var overlap = maximumOverlap; overlap > 0; overlap--)
        {
            var prefixStart = prefixStackNodes.Count - overlap;
            var same = true;
            for (var i = 0; i < overlap; i++)
            {
                if (!SameStackFrame(prefixStackNodes[prefixStart + i], incompleteStackNodes[i], profileThread))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return overlap;
            }
        }

        return 0;
    }

    private static bool IsStackPrefix(List<int> possiblePrefixStackNodes, List<int> stackNodes, FirefoxProfiler.Thread profileThread)
    {
        if (possiblePrefixStackNodes.Count >= stackNodes.Count)
        {
            return false;
        }

        for (var i = 0; i < possiblePrefixStackNodes.Count; i++)
        {
            if (!SameStackFrame(possiblePrefixStackNodes[i], stackNodes[i], profileThread))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetOrCreateStackEntry(int sourceStackIndex, int prefixStackIndex, FirefoxProfiler.Thread profileThread, Dictionary<StackEntryKey, int> stackEntryMap)
    {
        var stackTable = profileThread.StackTable;
        var key = new StackEntryKey(
            stackTable.Frame[sourceStackIndex],
            prefixStackIndex,
            stackTable.Category[sourceStackIndex],
            stackTable.Subcategory[sourceStackIndex]);

        if (stackEntryMap.TryGetValue(key, out var existingStackIndex))
        {
            return existingStackIndex;
        }

        var stackIndex = stackTable.Length;
        stackEntryMap.Add(key, stackIndex);
        stackTable.Frame.Add(key.Frame);
        stackTable.Category.Add(key.Category);
        stackTable.Subcategory.Add(key.Subcategory);
        stackTable.Prefix.Add(key.Prefix);
        stackTable.Length++;
        return stackIndex;
    }

    private static List<int> GetStackNodes(int stackIndex, FirefoxProfiler.Thread profileThread)
    {
        var stackTable = profileThread.StackTable;
        var stackNodes = new List<int>();
        var currentStackIndex = stackIndex;
        while (currentStackIndex >= 0)
        {
            stackNodes.Add(currentStackIndex);
            currentStackIndex = stackTable.Prefix[currentStackIndex] ?? -1;
        }

        stackNodes.Reverse();
        return stackNodes;
    }

    private static Dictionary<StackEntryKey, int> CreateStackEntryMap(FirefoxProfiler.Thread profileThread)
    {
        var stackTable = profileThread.StackTable;
        var stackEntryMap = new Dictionary<StackEntryKey, int>(stackTable.Length);
        for (var i = 0; i < stackTable.Length; i++)
        {
            stackEntryMap.TryAdd(new StackEntryKey(stackTable.Frame[i], stackTable.Prefix[i], stackTable.Category[i], stackTable.Subcategory[i]), i);
        }

        return stackEntryMap;
    }

    private static bool SameStackFrame(int leftStackIndex, int rightStackIndex, FirefoxProfiler.Thread profileThread)
    {
        var stackTable = profileThread.StackTable;
        return stackTable.Frame[leftStackIndex] == stackTable.Frame[rightStackIndex] &&
               stackTable.Category[leftStackIndex] == stackTable.Category[rightStackIndex] &&
               stackTable.Subcategory[leftStackIndex] == stackTable.Subcategory[rightStackIndex];
    }

    private readonly record struct StackPrefix(int StackIndex, int Depth);

    private readonly record struct StackEntryKey(int Frame, int? Prefix, int? Category, int? Subcategory);

    /// <summary>
    /// Finds, concurrently, which of <paramref name="modules"/> have no PDB available, so that the serial
    /// symbol pass can skip them instead of repeating their symbol-server lookups one at a time.
    /// </summary>
    /// <param name="modules">The loaded modules of the process about to be symbolized.</param>
    /// <returns>The modules for which no PDB could be found.</returns>
    /// <remarks>
    /// A miss costs a round trip to every remote server on the symbol path, and is not cached on disk, so
    /// these lookups dominate <see cref="LoadModules"/> on traces containing modules whose symbols are not
    /// published. A hit leaves the PDB in the shared cache, which every server element on the path checks
    /// before going out, so the serial pass then resolves it locally. This does I/O only and touches no
    /// <see cref="TraceLog"/> state.
    /// </remarks>
    private HashSet<ModuleFileIndex> ResolveModulesWithoutSymbols(List<TraceLoadedModule> modules)
    {
        var withoutSymbols = new HashSet<ModuleFileIndex>();

        var symbolPath = new SymbolPath(_symbolPathText);
        var remoteElements = symbolPath.Elements.Where(element => element.IsSymServer && element.IsRemote).ToList();
        if (remoteElements.Count == 0)
        {
            // Everything is local, where the lookups are cheap and need no throttling.
            return withoutSymbols;
        }

        var pending = new List<TraceModuleFile>();
        var seen = new HashSet<ModuleFileIndex>();
        foreach (var module in modules)
        {
            var moduleFile = module.ModuleFile;

            // Mirror when the pass in LoadModules actually performs a lookup, so this never issues a
            // request that would not otherwise have been made. Without a recorded signature,
            // OpenPdbForModuleFile takes a local-file route this cannot resolve ahead of time.
            if (_mapModuleFileIndexToFirefox.ContainsKey(moduleFile.ModuleFileIndex)
                || moduleFile.CodeAddressesInModule == 0
                || moduleFile.PdbSignature == Guid.Empty
                || !seen.Add(moduleFile.ModuleFileIndex))
            {
                continue;
            }

            pending.Add(moduleFile);
        }

        if (pending.Count < 2)
        {
            return withoutSymbols;
        }

        // LogStepProgress reaches a console writer that is not thread safe.
        var progressLock = new object();
        var completed = 0;

        void ReportProgress(string step, TraceModuleFile moduleFile)
        {
            var done = Interlocked.Increment(ref completed);
            if (Options.LogStepProgress is { } logStepProgress)
            {
                lock (progressLock)
                {
                    logStepProgress($"{step} [{done}/{pending.Count}] for Module `{moduleFile.Name}`");
                }
            }
        }

        static bool HasSymbols(SymbolReader reader, TraceModuleFile moduleFile)
        {
            try
            {
                return reader.FindSymbolFilePath(
                    moduleFile.PdbName,
                    moduleFile.PdbSignature,
                    moduleFile.PdbAge,
                    moduleFile.FilePath,
                    moduleFile.ProductVersion,
                    portablePdbMatch: true) is not null;
            }
            catch
            {
                // Leave it to the serial pass to retry and report as before.
                return true;
            }
        }

        // Anything already cached is settled by reading the file system, which needs no throttling.
        var cachedPathText = symbolPath.LocalOnly().ToString();
        var resolved = new bool[pending.Count];
        Parallel.For(
            0,
            pending.Count,
            () => CreateSymbolReader(cachedPathText),
            (i, _, reader) =>
            {
                resolved[i] = HasSymbols(reader, pending[i]);
                if (resolved[i])
                {
                    ReportProgress("Resolving Cached Symbols", pending[i]);
                }

                return reader;
            },
            reader => reader.Dispose());

        var uncached = new List<int>();
        for (var i = 0; i < pending.Count; i++)
        {
            if (!resolved[i])
            {
                uncached.Add(i);
            }
        }

        if (uncached.Count == 0)
        {
            return withoutSymbols;
        }

        var localElements = symbolPath.Elements.Where(element => !(element.IsSymServer && element.IsRemote)).ToList();
        var servers = new List<ThrottledSymbolServer>(remoteElements.Count);
        try
        {
            foreach (var remoteElement in remoteElements)
            {
                // A reader per server, searching the local elements plus that one remote, so every lookup
                // is attributable to a single server and can be throttled against it.
                var perServerPath = new SymbolPath();
                foreach (var localElement in localElements)
                {
                    perServerPath.Add(localElement);
                }
                perServerPath.Add(remoteElement);

                servers.Add(new ThrottledSymbolServer(perServerPath.ToString(), CreateSymbolReader));
            }

            // Asked in turn, stopping at the first hit. Asking them all at once was measured to be no
            // faster - the throttle, not the walk, is the limit - and it would query every server for a
            // module the first one already answered.
            async Task<bool> IsOnAnyServerAsync(TraceModuleFile moduleFile)
            {
                var found = false;
                foreach (var server in servers)
                {
                    using (var lease = await server.AcquireAsync().ConfigureAwait(false))
                    {
                        var reader = lease.Reader;
                        found = await Task.Run(() => HasSymbols(reader, moduleFile)).ConfigureAwait(false);
                    }

                    if (found)
                    {
                        break;
                    }
                }

                ReportProgress("Querying Symbol Servers", moduleFile);
                return found;
            }

            var lookups = new Task<bool>[uncached.Count];
            for (var i = 0; i < uncached.Count; i++)
            {
                lookups[i] = IsOnAnyServerAsync(pending[uncached[i]]);
            }

            // Awaited in order, so the result is the same whatever order they complete in.
            for (var i = 0; i < uncached.Count; i++)
            {
                if (!lookups[i].GetAwaiter().GetResult())
                {
                    withoutSymbols.Add(pending[uncached[i]].ModuleFileIndex);
                }
            }
        }
        finally
        {
            foreach (var server in servers)
            {
                server.Dispose();
            }
        }

        return withoutSymbols;
    }

    /// <summary>
    /// A single remote symbol server, limited to <see cref="MaxConcurrentLookupsPerSymbolServer"/>
    /// concurrent lookups, each with a <see cref="SymbolReader"/> of its own.
    /// </summary>
    private sealed class ThrottledSymbolServer : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(MaxConcurrentLookupsPerSymbolServer);
        private readonly ConcurrentBag<SymbolReader> _readers = [];

        public ThrottledSymbolServer(string symbolPathText, Func<string, SymbolReader> createSymbolReader)
        {
            for (var i = 0; i < MaxConcurrentLookupsPerSymbolServer; i++)
            {
                _readers.Add(createSymbolReader(symbolPathText));
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

    /// <summary>
    /// Loads the modules - and symbols for a given process.
    /// </summary>
    /// <param name="process">The process to load the modules.</param>
    /// <returns>The per-process module classification (managed-module set and clrjit/coreclr module indices) needed to categorize this process's frames later.</returns>
    private ProcessModuleContext LoadModules(TraceProcess process)
    {
        Options.LogProgress?.Invoke($"Loading Modules for process {process.Name} ({process.ProcessID})");

        var managedModules = new HashSet<ModuleFileIndex>();
        var clrJitModuleIndex = ModuleFileIndex.Invalid;
        var coreClrModuleIndex = ModuleFileIndex.Invalid;

        var allModules = process.LoadedModules.ToList();
        var modulesWithoutSymbols = ResolveModulesWithoutSymbols(allModules);

        // Precompute the non-managed modules grouped by FilePath (OrdinalIgnoreCase, matching the
        // comparison below) once per process, instead of re-scanning `allModules` with a LINQ
        // `Where(x => x is not TraceManagedModule)` for every managed module - that was
        // O(managedModules x allModules) for this process.
        var nonManagedModulesByFilePath = new Dictionary<string, List<TraceLoadedModule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in allModules)
        {
            if (module is not TraceManagedModule)
            {
                if (!nonManagedModulesByFilePath.TryGetValue(module.FilePath, out var bucket))
                {
                    bucket = new List<TraceLoadedModule>();
                    nonManagedModulesByFilePath.Add(module.FilePath, bucket);
                }
                bucket.Add(module);
            }
        }

        for (var i = 0; i < allModules.Count; i++)
        {
            var module = allModules[i];
            if (!_mapModuleFileIndexToFirefox.ContainsKey(module.ModuleFile.ModuleFileIndex))
            {
                Options.LogStepProgress?.Invoke($"Loading Symbols [{i}/{allModules.Count}] for Module `{module.Name}`, ImageSize: {ByteSize.FromBytes(module.ModuleFile.ImageSize)}");

                var lib = new FirefoxProfiler.Lib
                {
                    Name = module.Name,
                    AddressStart = module.ImageBase,
                    AddressEnd = module.ModuleFile.ImageEnd,
                    Path = module.ModuleFile.FilePath,
                    DebugPath = module.ModuleFile.PdbName,
                    DebugName = module.ModuleFile.PdbName,
                    BreakpadId = $"0x{module.ModuleID:X16}",
                    Arch = "x64" // TODO
                };

                // TraceCodeAddresses.LookupSymbolsForModule scans the ENTIRE codeAddresses array
                // (O(codeAddresses)) to collect the addresses that belong to this module, and
                // returns early (`if (list.Count == 0) return;`) as soon as it finds none. When
                // the module has zero code addresses attributed to it at all
                // (CodeAddressesInModule == 0), that collected list is guaranteed to be empty, so
                // the call would do nothing but pay for the full scan. Skip it in that case.
                if (module.ModuleFile.CodeAddressesInModule != 0
                    && !modulesWithoutSymbols.Contains(module.ModuleFile.ModuleFileIndex))
                {
                    _traceLog!.CodeAddresses.LookupSymbolsForModule(_symbolReader, module.ModuleFile);
                }

                _mapModuleFileIndexToFirefox.Add(module.ModuleFile.ModuleFileIndex, ProfilerResult.Libs.Count);
                ProfilerResult.Libs.Add(lib);
            }

            var fileName = Path.GetFileName(module.FilePath);
            if (fileName.Equals("clrjit.dll", StringComparison.OrdinalIgnoreCase))
            {
                clrJitModuleIndex = module.ModuleFile.ModuleFileIndex;
            }
            else if (fileName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
            {
                coreClrModuleIndex = module.ModuleFile.ModuleFileIndex;
            }

            if (module is TraceManagedModule managedModule)
            {
                managedModules.Add(managedModule.ModuleFile.ModuleFileIndex);

                if (nonManagedModulesByFilePath.TryGetValue(managedModule.FilePath, out var otherModules))
                {
                    foreach (var otherModule in otherModules)
                    {
                        managedModules.Add(otherModule.ModuleFile.ModuleFileIndex);
                    }
                }
            }
        }

        return new ProcessModuleContext(managedModules, clrJitModuleIndex, coreClrModuleIndex);
    }

    /// <summary>
    /// Converts an ETW call stack to a Firefox call stack.
    /// </summary>
    /// <param name="callStackIndex">The ETW callstack index to convert.</param>
    /// <param name="context">The current thread's conversion context.</param>
    /// <returns>The converted Firefox call stack index.</returns>
    private int ConvertCallStack(CallStackIndex callStackIndex, ThreadConvertContext context)
    {
        if (callStackIndex == CallStackIndex.Invalid) return -1;

        var parentCallStackIndex = _traceLog.CallStacks.Caller(callStackIndex);
        var fireFoxParentCallStackIndex = ConvertCallStack(parentCallStackIndex, context);

        return ConvertCallStack(callStackIndex, fireFoxParentCallStackIndex, context);
    }

    /// <summary>
    /// Converts an ETW call stack to a Firefox call stack.
    /// </summary>
    /// <param name="callStackIndex">The ETW callstack index to convert.</param>
    /// <param name="firefoxParentCallStackIndex">The parent Firefox callstack index.</param>
    /// <param name="context">The current thread's conversion context.</param>
    /// <returns>The converted Firefox call stack index.</returns>
    private int ConvertCallStack(CallStackIndex callStackIndex, int firefoxParentCallStackIndex, ThreadConvertContext context)
    {
        if (context.MapCallStackIndexToFirefox.TryGetValue(callStackIndex, out var index))
        {
            return index;
        }
        var stackTable = context.ProfileThread.StackTable;

        var firefoxCallStackIndex = stackTable.Length;
        context.MapCallStackIndexToFirefox.Add(callStackIndex, firefoxCallStackIndex);

        var codeAddressIndex = _traceLog.CallStacks.CodeAddressIndex(callStackIndex);
        var frameTableIndex = ConvertFrame(codeAddressIndex, context, out var category, out var subCategory);

        stackTable.Frame.Add(frameTableIndex);
        stackTable.Category.Add(category);
        stackTable.Subcategory.Add(subCategory);
        stackTable.Prefix.Add(firefoxParentCallStackIndex < 0 ? null : (int)firefoxParentCallStackIndex);
        stackTable.Length++;

        return firefoxCallStackIndex;
    }

    /// <summary>
    /// Converts an ETW code address to a Firefox frame.
    /// </summary>
    /// <param name="codeAddressIndex">The ETW code address index.</param>
    /// <param name="context">The current thread's conversion context.</param>
    /// <param name="category">The category of the frame.</param>
    /// <param name="subCategory">The subcategory of the frame.</param>
    /// <returns></returns>
    private int ConvertFrame(CodeAddressIndex codeAddressIndex, ThreadConvertContext context, out int category, out int subCategory)
    {
        var profileThread = context.ProfileThread;
        var frameTable = profileThread.FrameTable;

        if (context.MapCodeAddressIndexToFirefox.TryGetValue(codeAddressIndex, out var firefoxFrameTableIndex))
        {
            category = frameTable.Category[firefoxFrameTableIndex]!.Value;
            subCategory = frameTable.Subcategory[firefoxFrameTableIndex]!.Value;
            return firefoxFrameTableIndex;
        }

        firefoxFrameTableIndex = frameTable.Length;
        context.MapCodeAddressIndexToFirefox.Add(codeAddressIndex, firefoxFrameTableIndex);

        var module = _traceLog.CodeAddresses.ModuleFile(codeAddressIndex);
        var absoluteAddress = _traceLog.CodeAddresses.Address(codeAddressIndex);
        var offsetIntoModule = module is not null ? (int)(absoluteAddress - module.ImageBase) : 0;

        // Address
        // InlineDepth
        // Category
        // Subcategory
        // Func
        // NativeSymbol
        // InnerWindowID
        // Implementation
        // Line
        // Column

        frameTable.Address.Add(offsetIntoModule);
        frameTable.InlineDepth.Add(0);

        bool isManaged = false;
        if (module is not null)
        {
            isManaged = context.ProcessModules.ManagedModules.Contains(module.ModuleFileIndex);
        }

        subCategory = 0;

        if (isManaged)
        {
            category = CategoryManaged;
        }
        else
        {
            bool isKernel = (absoluteAddress >> 56) == 0xFF;
            category = isKernel ? CategoryKernel : CategoryNative;

            if (module != null)
            {
                if (module.ModuleFileIndex == context.ProcessModules.ClrJitModuleIndex)
                {
                    category = CategoryJit;
                }
                else if (module.ModuleFileIndex == context.ProcessModules.CoreClrModuleIndex)
                {
                    category = CategoryClr;
                }
            }
        }

        var methodIndex = _traceLog.CodeAddresses.MethodIndex(codeAddressIndex);
        var firefoxMethodIndex = ConvertMethod(codeAddressIndex, methodIndex, context);

        if (methodIndex != MethodIndex.Invalid)
        {
            var nameIndex = profileThread.FuncTable.Name[firefoxMethodIndex];
            var fullMethodName = profileThread.StringArray[nameIndex];
            // Hack to distinguish GC methods
            // https://github.com/dotnet/runtime/blob/af3393d3991b7aab608e514e4a4be3ae2bbafbf8/src/coreclr/gc/gc.cpp#L49-L53
            var isGC = fullMethodName.StartsWith("WKS::gc", StringComparison.OrdinalIgnoreCase) || fullMethodName.StartsWith("SVR::gc", StringComparison.OrdinalIgnoreCase);
            if (isGC)
            {
                category = CategoryGc;
            }
        }

        frameTable.Category.Add(category);
        frameTable.Subcategory.Add(subCategory);

        frameTable.Func.Add(firefoxMethodIndex);

        // Set other fields to null
        frameTable.NativeSymbol.Add(null);
        frameTable.InnerWindowID.Add(null);
        frameTable.Implementation.Add(null);

        //var sourceLine = log.CodeAddresses.GetSourceLine(_symbolReader, codeAddressIndex);
        //if (sourceLine != null)
        //{
        //    ft.Line.Add(sourceLine.LineNumber);
        //    ft.Column.Add(sourceLine.ColumnNumber);
        //}
        //else
        {
            frameTable.Line.Add(null);
            frameTable.Column.Add(null);
        }
        frameTable.Length++;

        return firefoxFrameTableIndex;
    }

    /// <summary>
    /// Converts an ETW method to a Firefox method.
    /// </summary>
    /// <param name="codeAddressIndex">The original code address.</param>
    /// <param name="methodIndex">The method index. Can be invalid.</param>
    /// <param name="context">The current thread's conversion context.</param>
    /// <returns>The converted Firefox method index.</returns>
    private int ConvertMethod(CodeAddressIndex codeAddressIndex, MethodIndex methodIndex, ThreadConvertContext context)
    {
        var profileThread = context.ProfileThread;
        var funcTable = profileThread.FuncTable;
        int firefoxMethodIndex;
        if (methodIndex == MethodIndex.Invalid)
        {
            if (context.MapCodeAddressIndexToMethodIndexFirefox.TryGetValue(codeAddressIndex, out var index))
            {
                return index;
            }
            firefoxMethodIndex = funcTable.Length;
            context.MapCodeAddressIndexToMethodIndexFirefox[codeAddressIndex] = firefoxMethodIndex;
        }
        else if (context.MapMethodIndexToFirefox.TryGetValue(methodIndex, out var index))
        {
            return index;
        }
        else
        {
            firefoxMethodIndex = funcTable.Length;
            context.MapMethodIndexToFirefox.Add(methodIndex, firefoxMethodIndex);
        }

        //public List<int> Name { get; }
        //public List<bool> IsJS { get; }
        //public List<bool> RelevantForJS { get; }
        //public List<int> Resource { get; }
        //public List<int?> FileName { get; }
        //public List<int?> LineNumber { get; }
        //public List<int?> ColumnNumber { get; }

        if (methodIndex == MethodIndex.Invalid)
        {
            var module = _traceLog.CodeAddresses.ModuleFile(codeAddressIndex);
            string? moduleName = module?.Name;
            var absoluteAddress = _traceLog.CodeAddresses.Address(codeAddressIndex);

            funcTable.Name.Add(GetOrCreateString($"{moduleName}!0x{absoluteAddress:X16}", context));
            funcTable.IsJS.Add(false);
            funcTable.RelevantForJS.Add(false);
            funcTable.Resource.Add(-1);
            funcTable.FileName.Add(null);
            funcTable.LineNumber.Add(null);
            funcTable.ColumnNumber.Add(null);
        }
        else
        {
            var fullMethodName = _traceLog.CodeAddresses.Methods.FullMethodName(methodIndex) ?? $"0x{_traceLog.CodeAddresses.Address(codeAddressIndex):X16}";

            var firefoxMethodNameIndex = GetOrCreateString(fullMethodName, context);
            funcTable.Name.Add(firefoxMethodNameIndex);
            funcTable.IsJS.Add(false);
            funcTable.RelevantForJS.Add(false);
            funcTable.FileName.Add(null); // TODO
            funcTable.LineNumber.Add(null);
            funcTable.ColumnNumber.Add(null);

            var moduleIndex = _traceLog.CodeAddresses.ModuleFileIndex(codeAddressIndex);
            if (moduleIndex != ModuleFileIndex.Invalid && _mapModuleFileIndexToFirefox.TryGetValue(moduleIndex, out var firefoxModuleIndex))
            {
                funcTable.Resource.Add(profileThread.ResourceTable.Length);

                var moduleName = Path.GetFileName(_traceLog.ModuleFiles[moduleIndex].FilePath);
                profileThread.ResourceTable.Name.Add(GetOrCreateString(moduleName, context));
                profileThread.ResourceTable.Lib.Add(firefoxModuleIndex);
                profileThread.ResourceTable.Length++;
            }
            else
            {
                funcTable.Resource.Add(-1);
            }
        }

        funcTable.Length++;

        return firefoxMethodIndex;
    }

    /// <summary>
    /// Gets or creates a string for the specified thread's conversion context.
    /// </summary>
    /// <param name="text">The string to create.</param>
    /// <param name="context">The current thread's conversion context.</param>
    /// <returns>The index of the string in the Firefox profile thread.</returns>
    private int GetOrCreateString(string text, ThreadConvertContext context)
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

    /// <summary>
    /// Creates a new Firefox profile.
    /// </summary>
    /// <returns>A new Firefox profile.</returns>
    private FirefoxProfiler.Profile CreateProfile()
    {
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
                Platform = $"{_traceLog.OSName} {_traceLog.OSVersion} {_traceLog.OSBuild}",
                Oscpu = $"{_traceLog.OSName} {_traceLog.OSVersion} {_traceLog.OSBuild}",
                LogicalCPUs = _traceLog.NumberOfProcessors,
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
                Interval = _traceLog.SampleProfileInterval.TotalMilliseconds,
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
                    GCEvent.Schema(),
                    GCHeapStatsEvent.Schema(),
                    GCAllocationTickEvent.Schema(),
                    GCSuspendExecutionEngineEvent.Schema(),
                    GCRestartExecutionEngineEvent.Schema(),
                }
            }
        };

        return profile;
    }
}

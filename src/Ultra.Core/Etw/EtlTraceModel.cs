// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using Ultra.Core.Markers;

namespace Ultra.Core.Etw;

/// <summary>
/// The in-memory model of an ETW trace that <see cref="EtlTraceReader"/> builds in a single pass over the raw
/// <c>.etl</c> file(s), holding only what <see cref="UltraConverterToFirefoxEtl"/> needs for the requested
/// processes: their threads, samples, call stacks, modules, managed methods and marker events. It replaces the
/// <c>TraceLog</c>/<c>.etlx</c> round trip, which materializes every event of every process on disk and reads it
/// back, with something that is built once and read in parallel.
/// </summary>
/// <remarks>
/// Times are kept as both raw QPC ticks (for exact matching, e.g. a stack walk to its sample) and the same
/// milliseconds-relative-to-session-start doubles that <c>TraceLog</c> exposes, computed with the same formula, so
/// the resulting profile is numerically identical to the one the <c>TraceLog</c> path produced.
/// </remarks>
internal sealed class EtlTraceModel
{
    /// <summary>Every process seen in the trace, in first-seen order; <see cref="EtlProcess.Index"/> indexes this list.</summary>
    public List<EtlProcess> Processes { get; } = new();

    /// <summary>All thread records of the trace, in creation order; <see cref="EtlThread.Index"/> indexes this list.</summary>
    public List<EtlThread> Threads { get; } = new();

    /// <summary>The last name each thread id was given by a <c>ThreadSetName</c> event.</summary>
    public Dictionary<int, string> ThreadNamesById { get; } = new();

    /// <summary>The ids of the processes being converted.</summary>
    public HashSet<int> TargetProcessIds { get; } = new();

    /// <summary>Module files (a unique path + image base), shared by all processes.</summary>
    public EtlModuleFileTable ModuleFiles { get; } = new();

    /// <summary>Unique (process, address) pairs referenced by call stacks.</summary>
    public EtlCodeAddressTable CodeAddresses { get; } = new();

    /// <summary>Resolved methods (native symbols and JIT-compiled managed methods).</summary>
    public EtlMethodTable Methods { get; } = new();

    /// <summary>The interned call-stack tree shared by all threads.</summary>
    public EtlStackTable Stacks { get; } = new();

    /// <summary>Whether <paramref name="address"/> is a kernel-mode address on a machine with <paramref name="pointerSize"/>-byte pointers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsKernelAddress(ulong address, int pointerSize)
        => pointerSize == 4 ? address >= 0x80000000UL : address >= 0xFFFF800000000000UL;

    /// <summary>The QPC tick of the session start, the origin of every relative time.</summary>
    public long SessionStartTimeQPC { get; set; }

    /// <summary>The QPC tick of the last event of the trace.</summary>
    public long SessionEndTimeQPC { get; set; }

    /// <summary>QPC ticks per second.</summary>
    public long QPCFreq { get; set; } = 10_000_000;

    /// <summary>The QPC tick that <see cref="SyncTimeUtc"/> corresponds to, used to turn a QPC tick into a wall-clock time.</summary>
    public long SyncTimeQPC { get; set; }

    /// <summary>The wall-clock time (UTC) that <see cref="SyncTimeQPC"/> corresponds to.</summary>
    public DateTime SyncTimeUtc { get; set; }

    /// <summary>The CPU sampling interval, in milliseconds.</summary>
    public double SampleIntervalMs { get; set; } = 1.0;

    /// <summary>The number of logical processors of the machine the trace was recorded on.</summary>
    public int NumberOfProcessors { get; set; }

    /// <summary>The pointer size (4 or 8) of the machine the trace was recorded on.</summary>
    public int PointerSize { get; set; } = 8;

    /// <summary>The OS product name (e.g. <c>Windows 10 Pro</c>), when the trace was merged with build information.</summary>
    public string? OSName { get; set; }

    /// <summary>The OS build lab string, when the trace was merged with build information.</summary>
    public string? OSBuild { get; set; }

    /// <summary>The OS version reported by the trace header.</summary>
    public Version OSVersion { get; set; } = new(0, 0);

    /// <summary>The number of events the session reported lost.</summary>
    public int EventsLost { get; set; }

    /// <summary>Converts a QPC tick into milliseconds relative to the session start, exactly as <c>TraceLog</c> does.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double QPCToRelativeMSec(long qpc)
    {
        if (qpc < SessionStartTimeQPC)
        {
            qpc = SessionStartTimeQPC;
        }

        return (qpc - SessionStartTimeQPC) * 1000.0 / QPCFreq;
    }

    /// <summary>Converts a QPC tick into a UTC wall-clock time, exactly as <c>TraceLog</c> does.</summary>
    public DateTime QPCToDateTimeUtc(long qpc)
    {
        if (qpc == long.MaxValue)
        {
            return DateTime.MaxValue;
        }

        var ticks = (long)((qpc - SyncTimeQPC) * 10_000_000.0 / QPCFreq) + SyncTimeUtc.Ticks;
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            ticks = DateTime.MaxValue.Ticks;
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private EtlProcess? _kernelProcess;
    private readonly Dictionary<int, EtlProcess> _lastProcessByPid = new();
    private readonly Dictionary<int, EtlThread> _lastThreadByTid = new();

    /// <summary>Gets the process to which kernel-mode code addresses are attributed (process id 0), creating it if needed.</summary>
    public EtlProcess KernelProcess => _kernelProcess ??= GetOrCreateProcess(0, SessionStartTimeQPC);

    /// <summary>
    /// Finds the process with <paramref name="processId"/> alive at <paramref name="timeQPC"/>: the most recent
    /// process record with that id whose start (or first event) is not after the time, like
    /// <c>TraceProcesses.GetProcess</c>.
    /// </summary>
    public EtlProcess? FindProcess(int processId, long timeQPC)
    {
        _lastProcessByPid.TryGetValue(processId, out var process);
        while (process is not null && process.StartTimeQPC > timeQPC && process.FirstEventSeenQPC > timeQPC)
        {
            process = process.Previous;
        }

        return process;
    }

    /// <summary>The last process record with <paramref name="processId"/>, like <c>TraceProcesses.LastProcessWithID</c>.</summary>
    public EtlProcess? LastProcessWithId(int processId) => FindProcess(processId, long.MaxValue);

    /// <summary>
    /// Gets the process with <paramref name="processId"/> alive at <paramref name="timeQPC"/>, creating a record if
    /// none exists (or when <paramref name="isProcessStartEvent"/> reports a new incarnation of the id), like
    /// <c>TraceProcesses.GetOrCreateProcess</c>.
    /// </summary>
    public EtlProcess GetOrCreateProcess(int processId, long timeQPC, bool isProcessStartEvent = false)
    {
        var process = FindProcess(processId, timeQPC);
        if (process is null || isProcessStartEvent)
        {
            if (process is not null && isProcessStartEvent && process.StartTimeQPC == 0 && process.EndTimeQPC == long.MaxValue)
            {
                // The process was created lazily by an earlier event; this start event just fills it in.
                return process;
            }

            _lastProcessByPid.TryGetValue(processId, out var previous);
            process = new EtlProcess(processId, Processes.Count, previous)
            {
                FirstEventSeenQPC = timeQPC,
                // Decided at creation: a process record can be created by an event of one of its threads that
                // precedes the process' own start/rundown event, and that event's samples belong to the profile.
                IsTarget = TargetProcessIds.Contains(processId),
            };
            Processes.Add(process);
            _lastProcessByPid[processId] = process;
        }

        return process;
    }

    /// <summary>
    /// Finds the thread record with <paramref name="threadId"/> that owns the time <paramref name="timeQPC"/>: the
    /// most recent record with that id that was registered at or before the time, like <c>TraceThreads.GetThread</c>.
    /// Thread ids are reused by the OS, so one id can have several records over a trace.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EtlThread? FindThread(int threadId, long timeQPC)
    {
        _lastThreadByTid.TryGetValue(threadId, out var thread);
        while (thread is not null && thread.HistoryStartQPC > timeQPC)
        {
            thread = thread.Previous;
        }

        return thread;
    }

    /// <summary>
    /// Gets the thread record with <paramref name="threadId"/> at <paramref name="timeQPC"/>, creating one when
    /// there is none, when <paramref name="isThreadCreateEvent"/> reports a new incarnation of the id, or when the
    /// existing record belongs to a different process than <paramref name="process"/>, like
    /// <c>TraceThreads.GetOrCreateThread</c>.
    /// </summary>
    public EtlThread GetOrCreateThread(int threadId, long timeQPC, EtlProcess? process, bool isThreadCreateEvent = false)
    {
        var thread = FindThread(threadId, timeQPC);
        if (process is not null && process.ProcessId != -1 && thread is not null && thread.Process.ProcessId != -1 && process.ProcessId != thread.Process.ProcessId)
        {
            thread = null;
        }

        if (thread is null || isThreadCreateEvent)
        {
            process ??= GetOrCreateProcess(-1, timeQPC);
            _lastThreadByTid.TryGetValue(threadId, out var previous);
            thread = new EtlThread(threadId, process, Threads.Count, timeQPC, previous);
            if (isThreadCreateEvent)
            {
                thread.StartTimeQPC = timeQPC;
            }

            Threads.Add(thread);
            _lastThreadByTid[threadId] = thread;
            process.Threads.Add(thread);
        }
        else if (process is not null && thread.Process.ProcessId == -1)
        {
            thread.Process.Threads.Remove(thread);
            thread.Process = process;
            process.Threads.Add(thread);
        }

        return thread;
    }
}

/// <summary>A process of the trace.</summary>
internal sealed class EtlProcess
{
    public EtlProcess(int processId, int index, EtlProcess? previous)
    {
        ProcessId = processId;
        Index = index;
        Previous = previous;
    }

    public int ProcessId { get; }

    /// <summary>The index of this process in <see cref="EtlTraceModel.Processes"/>.</summary>
    public int Index { get; }

    /// <summary>The previous process record with the same id (process ids are reused by the OS).</summary>
    public EtlProcess? Previous { get; }

    /// <summary>Whether the profile being built includes this process, i.e. whether its samples and stacks are kept.</summary>
    public bool IsTarget { get; set; }

    public string ImageFileName { get; set; } = string.Empty;
    public string CommandLine { get; set; } = string.Empty;
    public int ParentId { get; set; }
    public int ExitStatus { get; set; }

    /// <summary>The process start, as a QPC tick; 0 when the trace holds no start event for it.</summary>
    public long StartTimeQPC { get; set; }

    /// <summary>The process end, as a QPC tick; <see cref="long.MaxValue"/> until it ends (clamped to the session end after the pass).</summary>
    public long EndTimeQPC { get; set; } = long.MaxValue;

    public long FirstEventSeenQPC { get; set; }

    private string? _name;

    /// <summary>The process name derived from its image file name, like <c>TraceProcess.Name</c>.</summary>
    public string Name
    {
        get
        {
            if (_name is null)
            {
                _name = EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(ImageFileName);
                if (_name.Length == 0 && ProcessId != -1 && ProcessId != 0)
                {
                    _name = $"Process({ProcessId})";
                }
            }

            return _name;
        }
    }

    /// <summary>The threads of this process, in creation order.</summary>
    public List<EtlThread> Threads { get; } = new();

    /// <summary>The native images loaded in this process, in load order.</summary>
    public List<EtlLoadedModule> LoadedModules { get; } = new();

    /// <summary>The managed modules of this process by runtime module id, the module last seen under the id.</summary>
    public Dictionary<long, EtlManagedModule> ManagedModules { get; } = new();

    /// <summary>The managed modules of this process, in load order.</summary>
    public List<EtlManagedModule> ManagedModuleList { get; } = new();

    /// <summary>The JIT-compiled methods of this process, in load order.</summary>
    public List<EtlJitMethodLoad> JitMethods { get; } = new();

    /// <summary>
    /// The code address of each distinct address seen in this process' stacks that has not been resolved to a
    /// JIT-compiled method yet. An address resolved to a JIT-compiled method is removed, so that a later sample
    /// at the same address (the JIT reused the memory) gets a code address of its own.
    /// </summary>
    public Dictionary<ulong, int> CodeAddressByAddress { get; } = new();

    /// <summary>
    /// The code addresses of this process not resolved to a method yet, sorted by address once
    /// <see cref="UnresolvedCodeAddressesSorted"/> is set (sorting is deferred to the range queries that need it).
    /// </summary>
    public List<int> UnresolvedCodeAddresses { get; } = new();

    public bool UnresolvedCodeAddressesSorted { get; set; }

    // Per-process marker bookkeeping, shared by every thread of the process (see the ETW converter for why JIT
    // and GC pairing must be per process rather than per thread).
    public Dictionary<long, (JitCompileEvent Event, double StartTimeMs)> JitCompilePending { get; } = new();
    public GcCollectionTracker<EtlThread, GCEvent> GcCollections { get; } = new();
    public List<(double TimeMs, GCHeapStatsEvent Event)> GcHeapStatsEvents { get; } = new();

    // TraceLog's server-GC thread detection state.
    public Dictionary<int, int> MarkThreadsInGC { get; } = new();
    public bool ShouldCheckIsServerGC { get; set; }
    public bool IsServerGC { get; set; }
    public bool AnyThreads { get; set; }
}

/// <summary>A thread record of the trace.</summary>
internal sealed class EtlThread
{
    public EtlThread(int threadId, EtlProcess process, int index, long historyStartQPC, EtlThread? previous)
    {
        ThreadId = threadId;
        Process = process;
        Index = index;
        HistoryStartQPC = historyStartQPC;
        Previous = previous;
    }

    public int ThreadId { get; }
    public EtlProcess Process { get; set; }

    /// <summary>The index of this thread in <see cref="EtlTraceModel.Threads"/>.</summary>
    public int Index { get; }

    /// <summary>The time from which this record owns <see cref="ThreadId"/> (see <see cref="EtlTraceModel.FindThread"/>).</summary>
    public long HistoryStartQPC { get; }

    /// <summary>The previous record with the same thread id.</summary>
    public EtlThread? Previous { get; }

    /// <summary>The thread start, as a QPC tick; 0 when the trace holds no start event for it.</summary>
    public long StartTimeQPC { get; set; }

    /// <summary>The thread end, as a QPC tick; <see cref="long.MaxValue"/> when the trace holds no end event for it.</summary>
    public long EndTimeQPC { get; set; } = long.MaxValue;

    /// <summary>The categorization <c>TraceLog</c> would give the thread (e.g. <c>.NET Finalizer Thread</c>), if any.</summary>
    public string? ThreadInfo { get; set; }

    /// <summary>The last name given to the thread by a <c>ThreadSetName</c> event, if any.</summary>
    public string? SetName { get; set; }

    /// <summary>The number of CPU samples taken on this thread (like <c>TraceThread.cpuSamples</c>).</summary>
    public int CpuSamples { get; set; }

    // Samples, kept only for the threads of target processes. A sample whose stack never arrives is dropped when
    // the thread is finalized, exactly as the TraceLog-based converter skipped samples without a call stack.
    public List<double> SampleTimesMs { get; } = new();
    public List<double> SampleLastSwitchInMs { get; } = new();
    public List<int> SampleStacks { get; } = new();

    /// <summary>The time of the last context switch into this thread, in relative milliseconds (0 before any).</summary>
    public double LastSwitchInMs { get; set; }

    /// <summary>The samples of this thread whose kernel-mode stack fragment is waiting for the user-mode fragment.</summary>
    public List<EtlPendingKernelStack>? PendingKernelStacks { get; set; }

    /// <summary>The time of the last user-mode stack that completed pending kernel stacks, like <c>TraceThread.lastEmitStackOnExitFromKernelQPC</c>.</summary>
    public long LastEmitStackOnExitFromKernelQPC { get; set; }

    /// <summary>The most recent samples, to match a stack walk to its sample by timestamp.</summary>
    public EtlRecentSamples RecentSamples { get; } = new();

    /// <summary>
    /// The addresses (root first) and nodes of the last user-mode stack walked on this thread: consecutive stacks
    /// of a thread mostly share a long prefix, which is then reused instead of interned again.
    /// </summary>
    public ulong[] LastStackAddresses = new ulong[64];
    public int[] LastStackNodes = new int[64];
    public int LastStackDepth;

    // Per-thread marker bookkeeping, in the event order of the thread.
    public Stack<(double TimeMs, GCSuspendExecutionEngineEvent Event)>? GcSuspendEEEvents { get; set; }
    public Stack<double>? GcRestartEEEvents { get; set; }
    public List<EtlMarker> Markers { get; } = new();
}

/// <summary>A sample whose kernel-mode frames are known and whose user-mode stack has not arrived yet.</summary>
internal sealed class EtlPendingKernelStack(int sampleIndex, ulong[] kernelFrames)
{
    public int SampleIndex { get; } = sampleIndex;

    /// <summary>The kernel-mode frames, leaf first.</summary>
    public ulong[] KernelFrames { get; } = kernelFrames;
}

/// <summary>
/// The most recent samples of a thread, to match a stack walk to its sample by timestamp. A user-mode stack can
/// be walked several milliseconds after its sample (once the thread leaves the kernel), so this keeps more than
/// the few samples taken in between; <c>TraceLog</c> searches its last 2048 events of all threads instead.
/// </summary>
internal sealed class EtlRecentSamples
{
    private const int Size = 256;
    private readonly long[] _qpc = new long[Size];
    private readonly int[] _index = new int[Size];
    private int _next;

    public void Add(long qpc, int sampleIndex)
    {
        var slot = _next & (Size - 1);
        _qpc[slot] = qpc;
        _index[slot] = sampleIndex;
        _next++;
    }

    /// <summary>Finds the most recent sample taken at exactly <paramref name="qpc"/>, or -1.</summary>
    public int Find(long qpc)
    {
        // Most recent first, so that two samples with the same timestamp resolve to the latest one.
        var count = Math.Min(_next, Size);
        for (var i = 1; i <= count; i++)
        {
            var slot = (_next - i) & (Size - 1);
            if (_qpc[slot] == qpc)
            {
                return _index[slot];
            }
        }

        return -1;
    }
}

/// <summary>The kind of a marker collected during the event pass.</summary>
internal enum EtlMarkerKind
{
    JitCompile,
    GcHeapStats,
    GcAllocationTick,
    GcEvent,
    GcConcurrent,
    GcSuspendEE,
    GcRestartEE,
}

/// <summary>A marker collected during the event pass, converted to a Firefox marker when its thread is built.</summary>
internal readonly record struct EtlMarker(EtlMarkerKind Kind, double StartTimeMs, double EndTimeMs, FirefoxProfiler.MarkerPayload? Payload);

/// <summary>A native image loaded in a process (one record per load).</summary>
internal sealed class EtlLoadedModule
{
    public EtlLoadedModule(int moduleFileIndex, ulong imageBase)
    {
        ModuleFileIndex = moduleFileIndex;
        ImageBase = imageBase;
    }

    public int ModuleFileIndex { get; }
    public ulong ImageBase { get; }
    public long LoadTimeQPC { get; set; }
    public long UnloadTimeQPC { get; set; } = long.MaxValue;
}

/// <summary>A managed module (assembly) loaded in a process.</summary>
internal sealed class EtlManagedModule
{
    public EtlManagedModule(long moduleId, int moduleFileIndex)
    {
        ModuleId = moduleId;
        ModuleFileIndex = moduleFileIndex;
    }

    public long ModuleId { get; }

    /// <summary>The module file of the IL image; a placeholder file without a path when the trace holds no load event for the module.</summary>
    public int ModuleFileIndex { get; set; }

    public long AssemblyId { get; set; }

    /// <summary>
    /// The unload of the module, as a QPC tick; <see cref="long.MaxValue"/> while loaded. The runtime reuses a
    /// module id once its module is unloaded, so a later module with the same id is another module.
    /// </summary>
    public long UnloadTimeQPC { get; set; } = long.MaxValue;
}

/// <summary>A JIT-compiled method load of a process.</summary>
/// <param name="StartAddress">The start address of the native code.</param>
/// <param name="Size">The size of the native code.</param>
/// <param name="FullName">The full method name (see <c>TraceLog.GetFullName</c>).</param>
/// <param name="ModuleId">The runtime id of the method's module.</param>
/// <param name="TimeStampQPC">The time of the load, which tells which module of a reused id the method belongs to.</param>
internal readonly record struct EtlJitMethodLoad(ulong StartAddress, int Size, string FullName, long ModuleId, long TimeStampQPC);

/// <summary>
/// The table of module files: a module file is a unique (path, image base) pair, shared by every process that
/// loads the same image at the same address, like <c>TraceModuleFiles</c>.
/// </summary>
internal sealed class EtlModuleFileTable
{
    private readonly Dictionary<(string Path, ulong ImageBase), int> _index = new();

    public List<EtlModuleFile> Files { get; } = new();

    public EtlModuleFile this[int index] => Files[index];

    public int Count => Files.Count;

    /// <summary>
    /// Gets the module file for <paramref name="path"/> at <paramref name="imageBase"/>, creating it if needed.
    /// Paths are lower-cased, like <c>TraceModuleFile</c> does, so that the same image is one file whatever the
    /// case its load events spell it with - and so that every name derived from a path in the profile keeps the
    /// spelling the previous conversion gave it.
    /// </summary>
    public int GetOrCreate(string? path, ulong imageBase)
    {
        path = path?.ToLowerInvariant();
        if (path is not null && _index.TryGetValue((path, imageBase), out var index))
        {
            return index;
        }

        index = Files.Count;
        Files.Add(new EtlModuleFile(index, path, imageBase));
        if (path is not null)
        {
            _index[(path, imageBase)] = index;
        }

        return index;
    }

    /// <summary>Names a placeholder module file created without a path once its load event names it.</summary>
    public void SetFileName(EtlModuleFile file, string path)
    {
        path = path.ToLowerInvariant();
        file.FilePath = path;
        _index.TryAdd((path, file.ImageBase), file.Index);
    }
}

/// <summary>A module file: a native image or a managed assembly, identified by path and image base.</summary>
internal sealed class EtlModuleFile
{
    /// <summary>
    /// The path <c>TraceLog</c> gives the placeholder module file of a managed module it never saw a load event
    /// for; such a module has no usable name.
    /// </summary>
    public const string UnknownManagedModulePath = "ManagedModule";

    public EtlModuleFile(int index, string? path, ulong imageBase)
    {
        Index = index;
        _filePath = path;
        ImageBase = imageBase;
    }

    private string? _filePath;
    private string? _name;

    public int Index { get; }

    /// <summary>Whether the trace named this file (see <see cref="UnknownManagedModulePath"/>).</summary>
    public bool HasFilePath => _filePath is not null;

    /// <summary>The path of the image, or <see cref="UnknownManagedModulePath"/> for a managed module the trace never named.</summary>
    public string FilePath
    {
        get => _filePath ?? UnknownManagedModulePath;
        set { _filePath = value; _name = null; }
    }

    /// <summary>The file name without its <c>.dll</c>/<c>.exe</c> extension, like <c>TraceModuleFile.Name</c>.</summary>
    public string Name => _name ??= EtlPathHelper.GetFileNameWithoutExtensionNoIllegalChars(FilePath);

    public ulong ImageBase { get; }
    public int ImageSize { get; set; }
    public ulong ImageEnd => ImageBase + (uint)ImageSize;
    public int ImageChecksum { get; set; }
    public int TimeDateStamp { get; set; }
    public string PdbName { get; set; } = string.Empty;
    public Guid PdbSignature { get; set; }
    public int PdbAge { get; set; }
    public string? ProductVersion { get; set; }

    /// <summary>The number of code addresses attributed to this file (from the stacks of the target processes).</summary>
    public int CodeAddressCount;
}

/// <summary>A resolved method: a native symbol or a JIT-compiled managed method.</summary>
internal readonly record struct EtlMethod(string FullName, int ModuleFileIndex);

/// <summary>The table of resolved methods, appended to from several threads during symbol resolution.</summary>
internal sealed class EtlMethodTable
{
    private readonly List<EtlMethod> _methods = new();
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) { return _methods.Count; } }
    }

    /// <summary>Takes a snapshot of the table, for lock-free reads once resolution is complete.</summary>
    public EtlMethod[] ToArray()
    {
        lock (_lock) { return _methods.ToArray(); }
    }

    public int Add(string fullName, int moduleFileIndex)
    {
        lock (_lock)
        {
            _methods.Add(new EtlMethod(fullName, moduleFileIndex));
            return _methods.Count - 1;
        }
    }

    /// <summary>Adds several methods at once, returning the index of the first.</summary>
    public int AddRange(List<EtlMethod> methods)
    {
        lock (_lock)
        {
            var first = _methods.Count;
            _methods.AddRange(methods);
            return first;
        }
    }
}

/// <summary>
/// The table of code addresses. A code address is a unique (process, address) pair, like <c>TraceCodeAddresses</c>;
/// kernel-mode addresses belong to <see cref="EtlTraceModel.KernelProcess"/> whichever process sampled them.
/// </summary>
internal sealed class EtlCodeAddressTable
{
    public List<ulong> Address { get; } = new();
    public List<int> ProcessIndex { get; } = new();
    public List<int> ModuleFileIndex { get; } = new();
    public List<int> MethodIndex { get; } = new();

    public int Count => Address.Count;

    /// <summary>Gets the code address for <paramref name="address"/> in <paramref name="process"/>, creating it if needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOrCreate(EtlProcess process, ulong address)
    {
        var map = process.CodeAddressByAddress;
        if (map.TryGetValue(address, out var index))
        {
            return index;
        }

        index = Address.Count;
        Address.Add(address);
        ProcessIndex.Add(process.Index);
        ModuleFileIndex.Add(-1);
        MethodIndex.Add(-1);
        map[address] = index;
        process.UnresolvedCodeAddresses.Add(index);
        process.UnresolvedCodeAddressesSorted = false;
        return index;
    }
}

/// <summary>Path helpers mirroring <c>TraceLog</c>'s naming of processes and modules.</summary>
internal static class EtlPathHelper
{
    private static readonly char[] DirectorySeparators = ['\\', '/'];

    /// <summary>
    /// The file name of <paramref name="filePath"/> without a <c>.dll</c>/<c>.exe</c> extension (any other
    /// extension is kept), like <c>TraceLog.GetFileNameWithoutExtensionNoIllegalChars</c>.
    /// </summary>
    public static string GetFileNameWithoutExtensionNoIllegalChars(string filePath)
    {
        var start = filePath.LastIndexOfAny(DirectorySeparators);
        start = start >= 0 ? start + 1 : 0;
        var dot = filePath.LastIndexOf('.');
        if (dot > start)
        {
            var extension = filePath.AsSpan(dot);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return filePath.Substring(start, dot - start);
            }
        }

        return filePath.Substring(start);
    }
}

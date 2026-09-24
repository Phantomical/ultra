// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

// TimeStampQPC is flagged as discouraged by TraceEvent; the raw tick is what stack walks are matched to their
// sample by, and what keeps the times identical to TraceLog's.
#pragma warning disable CS0618

using System.Reflection;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Parsers.Symbol;
using Ultra.Core.Markers;

namespace Ultra.Core.Etw;

/// <summary>
/// Builds an <see cref="EtlTraceModel"/> from raw <c>.etl</c> file(s) in a single pass over their events, using
/// <see cref="ETWTraceEventSource"/> for decoding only. It reproduces the bookkeeping <c>TraceLog</c> performs when
/// it converts a trace to <c>.etlx</c> (process/thread/module lifetimes, kernel/user stack fragment pairing, JIT
/// method ranges) for the processes being converted, and evaluates the marker logic of the Firefox converter for
/// those processes as the events stream by, so that nothing has to be written to disk and re-read.
/// </summary>
/// <remarks>
/// Several unmerged session files (e.g. the kernel, user and rundown files a profiling session produces) can be
/// read together: ETW merges them by timestamp while reading, which makes the separate merge step - a rewrite of
/// every byte of the trace - unnecessary for conversion.
/// </remarks>
internal sealed class EtlTraceReader
{
    private readonly EtlTraceModel _model = new();
    private readonly HashSet<int> _targetProcessIds;
    private readonly Action<string>? _logProgress;
    private readonly bool _reportProgress;

    // Progress is reported every this many events: rare enough that the counter and the check cost nothing.
    private const int ProgressEventInterval = 1_000_000;

    private long _eventCount;
    private bool _headerSeen;

    // TraceLog's pairing of the symbol events injected by an ETL merge with the image load they describe.
    private DbgIdInfo? _lastDbgData;
    private ImageIdInfo? _lastImageIdData;
    private FileVersionInfoData? _lastFileVersionData;
    private EtlModuleFile? _lastModuleFile;
    private long _lastModuleFileQPC;

    private bool _sampleIntervalStartSeen;

    private readonly record struct DbgIdInfo(long TimeStampQPC, string PdbFileName, Guid GuidSig, int Age);
    private readonly record struct ImageIdInfo(long TimeStampQPC, int TimeDateStamp, string OriginalFileName);
    private readonly record struct FileVersionInfoData(long TimeStampQPC, string FileVersion, string ProductVersion);

    private EtlTraceReader(IEnumerable<int> targetProcessIds, Action<string>? logProgress)
    {
        _targetProcessIds = new HashSet<int>(targetProcessIds);
        _model.TargetProcessIds.UnionWith(_targetProcessIds);
        _logProgress = logProgress;
        _reportProgress = logProgress is not null;
    }

    /// <summary>
    /// Reads <paramref name="etlFiles"/> and builds the model of the processes in <paramref name="processIds"/>.
    /// </summary>
    /// <param name="etlFiles">The <c>.etl</c> file(s) to read; several files are merged by timestamp while reading.</param>
    /// <param name="processIds">The ids of the processes to convert.</param>
    /// <param name="logProgress">Receives progress messages, if not <c>null</c>.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentException"><paramref name="etlFiles"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">A file does not exist.</exception>
    public static EtlTraceModel Read(IReadOnlyList<string> etlFiles, IEnumerable<int> processIds, Action<string>? logProgress)
    {
        ArgumentNullException.ThrowIfNull(etlFiles);
        ArgumentNullException.ThrowIfNull(processIds);
        if (etlFiles.Count == 0)
        {
            throw new ArgumentException("At least one ETL file is required", nameof(etlFiles));
        }

        foreach (var file in etlFiles)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"The trace file `{file}` does not exist", file);
            }
        }

        var reader = new EtlTraceReader(processIds, logProgress);
        using var source = etlFiles.Count == 1
            ? new ETWTraceEventSource(etlFiles[0], TraceEventSourceType.FileOnly)
            : new ETWTraceEventSource(etlFiles, TraceEventSourceType.MergeAll);

        return reader.ReadFrom(source);
    }

    private EtlTraceModel ReadFrom(ETWTraceEventSource source)
    {
        var model = _model;
        model.PointerSize = source.PointerSize;
        model.NumberOfProcessors = source.NumberOfProcessors;
        model.OSVersion = source.OSVersion;
        model.SyncTimeUtc = source.SessionStartTime.ToUniversalTime();
        InitializeTimesFromSource(source);

        SetupKernelCallbacks(source);
        SetupSymbolCallbacks(source);
        SetupClrCallbacks(source);

        source.AllEvents += OnAnyEvent;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        source.Process();
        var cpuAfter = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        _logProgress?.Invoke($"Read {_eventCount:N0} events in {stopwatch.Elapsed.TotalSeconds:0.0}s (cpu {(cpuAfter - cpuBefore).TotalSeconds:0.0}s, gc0/1/2 {GC.CollectionCount(0) - gcBefore.Item1}/{GC.CollectionCount(1) - gcBefore.Item2}/{GC.CollectionCount(2) - gcBefore.Item3}, stacks {model.Stacks.Count:N0}, code addresses {model.CodeAddresses.Count:N0}, working set {Environment.WorkingSet / (1 << 20):N0} MB)");

        model.EventsLost = source.EventsLost;
        Finish();
        return model;
    }

    /// <summary>
    /// Reads the session clock parameters <see cref="ETWTraceEventSource"/> computed from the log headers. They are
    /// only exposed through internal fields; the header event of the trace is used when they are unavailable.
    /// </summary>
    private void InitializeTimesFromSource(ETWTraceEventSource source)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(TraceEventSource);
        var freq = type.GetField("_QPCFreq", flags)?.GetValue(source) as long?;
        var start = type.GetField("sessionStartTimeQPC", flags)?.GetValue(source) as long?;
        var end = type.GetField("sessionEndTimeQPC", flags)?.GetValue(source) as long?;
        if (freq is > 0)
        {
            _model.QPCFreq = freq.Value;
        }

        if (start is not null && end is not null)
        {
            // Before the header event is seen, these hold the session times relative to a QPC origin of 0; the
            // header event (the first event of the trace) supplies the origin (see OnEventTraceHeader).
            _model.SessionStartTimeQPC = start.Value;
            _model.SessionEndTimeQPC = end.Value;
            _timesFromSource = true;
        }
    }

    private bool _timesFromSource;

    private void OnEventTraceHeader(EventTraceHeaderTraceData data)
    {
        if (_headerSeen)
        {
            return;
        }

        _headerSeen = true;
        var model = _model;
        model.SyncTimeQPC = data.TimeStampQPC;
        if (_timesFromSource)
        {
            model.SessionStartTimeQPC += data.TimeStampQPC;
            model.SessionEndTimeQPC += data.TimeStampQPC;
        }
        else
        {
            if (data.PerfFreq > 0)
            {
                model.QPCFreq = data.PerfFreq;
            }

            model.SessionStartTimeQPC = data.TimeStampQPC;
            var durationTicks = (data.EndTime.ToUniversalTime() - data.StartTime.ToUniversalTime()).Ticks;
            model.SessionEndTimeQPC = data.TimeStampQPC + (long)(durationTicks * (double)model.QPCFreq / 10_000_000.0);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Generic per-event bookkeeping
    // ---------------------------------------------------------------------------------------------------------

    private EtlThread? _lastThread;

    /// <summary>Set by an event handler that already did the generic bookkeeping of <see cref="OnAnyEvent"/> for its event.</summary>
    private bool _bookkeepingDone;

    /// <summary>
    /// Runs for every event: tracks the end of the session and, like <c>TraceLog</c> does for every event it
    /// keeps, makes sure the process and thread the event was raised on have a record. This is what gives a
    /// process the same set of threads the <c>TraceLog</c> path reported, including threads that never ran a
    /// sample.
    /// </summary>
    private void OnAnyEvent(TraceEvent data)
    {
        _eventCount++;
        if (_reportProgress && _eventCount % ProgressEventInterval == 0)
        {
            _logProgress!($"Reading Events: {_eventCount:N0} processed");
        }

        var qpc = data.TimeStampQPC;
        if (qpc > _model.SessionEndTimeQPC)
        {
            _model.SessionEndTimeQPC = qpc;
        }

        if (_bookkeepingDone)
        {
            _bookkeepingDone = false;
            return;
        }

        if (IsBookKeepingEvent(data))
        {
            return;
        }

        var thread = _lastThread;
        if (thread is null || (thread.ThreadId != data.ThreadID && data.ProcessID != -1))
        {
            var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
            if (data.ThreadID != -1)
            {
                _lastThread = _model.GetOrCreateThread(data.ThreadID, qpc, process);
            }
        }
    }

    /// <summary>
    /// Whether <c>TraceLog</c> drops <paramref name="data"/> from the event stream (or flags it as not owning a
    /// stack) before its generic process/thread bookkeeping, so that no record must be created for it here.
    /// </summary>
    private static bool IsBookKeepingEvent(TraceEvent data)
    {
        switch (data)
        {
            case StackWalkStackTraceData:
            case StackWalkRefTraceData:
            case StackWalkDefTraceData:
            case ClrStackWalkTraceData:
            case FileIONameTraceData:
            case MethodILToNativeMapTraceData:
                return true;
            case ThreadTraceData thread:
                return thread.Opcode is TraceEventOpcode.DataCollectionStart or TraceEventOpcode.DataCollectionStop;
            case SampledProfileTraceData sample:
                // The idle-thread samples TraceLog removes from the stream.
                return sample.ThreadID == 0 && !sample.NonProcess;
            case MethodLoadUnloadVerboseTraceData method:
                if (!method.IsDynamic && !method.IsJitted)
                {
                    return true;
                }

                // MethodDCStopVerbose (rundown) and MethodUnloadVerbose of a non-jitted method are bookkeeping too;
                // the unload case is covered above for non-jitted methods.
                return data.ID == (TraceEventID)139 || (data.Opcode == (TraceEventOpcode)2 && !method.IsJitted && data.ProviderGuid == ClrRundownTraceEventParser.ProviderGuid);
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Kernel events
    // ---------------------------------------------------------------------------------------------------------

    private void SetupKernelCallbacks(ETWTraceEventSource source)
    {
        var kernel = source.Kernel;

        kernel.EventTraceHeader += OnEventTraceHeader;

        kernel.SysConfigBuildInfo += data =>
        {
            _model.OSName = data.ProductName;
            _model.OSBuild = data.BuildLab;
        };

        kernel.PerfInfoCollectionStart += data =>
        {
            if (data.SampleSource == 0)
            {
                _sampleIntervalStartSeen = true;
                _model.SampleIntervalMs = data.NewInterval / 10_000.0;
            }
        };

        kernel.PerfInfoSetInterval += data =>
        {
            // Without a CollectionStart, the interval in force is the one the last SetInterval replaced (TraceLog
            // re-applies every one of them).
            if (data.SampleSource == 0 && !_sampleIntervalStartSeen)
            {
                _model.SampleIntervalMs = data.OldInterval / 10_000.0;
            }
        };

        kernel.ProcessStartGroup += OnProcessStart;
        kernel.ProcessEndGroup += OnProcessEnd;
        kernel.ThreadStartGroup += OnThreadStart;
        kernel.ThreadEndGroup += OnThreadEnd;
        kernel.ThreadSetName += OnThreadSetName;
        kernel.ImageGroup += OnImage;
        kernel.PerfInfoSample += OnSample;
        kernel.StackWalkStack += OnStackWalk;
        kernel.ThreadCSwitch += OnContextSwitch;
    }

    private void OnProcessStart(ProcessTraceData data)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc, data.Opcode == TraceEventOpcode.Start);
        process.StartTimeQPC = data.Opcode == TraceEventOpcode.DataCollectionStart ? _model.SessionStartTimeQPC : qpc;
        process.CommandLine = data.CommandLine;
        process.ImageFileName = data.ImageFileName;
        process.ParentId = data.ParentID;
    }

    private void OnProcessEnd(ProcessTraceData data)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        if (process.CommandLine.Length == 0)
        {
            process.CommandLine = data.CommandLine;
        }

        process.ImageFileName = data.ImageFileName;
        if (process.ParentId == 0 && data.ParentID != 0)
        {
            process.ParentId = data.ParentID;
        }

        if (data.Opcode != TraceEventOpcode.DataCollectionStop)
        {
            if (data.Opcode == TraceEventOpcode.Stop)
            {
                process.ExitStatus = data.ExitStatus;
            }

            process.EndTimeQPC = qpc;
        }
    }

    private void OnThreadStart(ThreadTraceData data)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        var isCreate = data.Opcode is TraceEventOpcode.Start or TraceEventOpcode.DataCollectionStart;
        var thread = _model.GetOrCreateThread(data.ThreadID, qpc, process, isCreate);
        _lastThread = thread;
        _bookkeepingDone = true;
        thread.StartTimeQPC = qpc;
        if (data.Opcode == TraceEventOpcode.DataCollectionStart)
        {
            thread.StartTimeQPC = _model.SessionStartTimeQPC;
        }
        else if (data.Opcode == TraceEventOpcode.Start)
        {
            if (!process.AnyThreads)
            {
                if (_model.SessionStartTimeQPC < process.StartTimeQPC && process.StartTimeQPC < qpc)
                {
                    thread.ThreadInfo = "Startup Thread";
                }

                process.AnyThreads = true;
            }
        }
    }

    private void OnThreadEnd(ThreadTraceData data)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        var thread = _model.GetOrCreateThread(data.ThreadID, qpc, process);
        _lastThread = thread;
        _bookkeepingDone = true;
        if (data.ThreadName.Length > 0)
        {
            CategorizeThread(data, data.ThreadName);
        }

        thread.EndTimeQPC = qpc;
        if (data.Opcode == TraceEventOpcode.DataCollectionStop)
        {
            // The session end as known so far: the header's end, raised by every event seen (TraceLog's running value).
            thread.EndTimeQPC = _model.SessionEndTimeQPC;
        }
    }

    private void OnThreadSetName(ThreadSetNameTraceData data)
    {
        var name = data.ThreadName;
        CategorizeThread(data, name);
        if (!string.IsNullOrEmpty(name))
        {
            // The converter names threads by id from the last ThreadSetName of the trace, whichever thread record
            // the event belongs to.
            _model.ThreadNamesById[data.ThreadID] = name;
        }
    }

    /// <summary>Gives a thread its <c>TraceLog</c> categorization, unless it already has one.</summary>
    private void CategorizeThread(TraceEvent data, string category, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        var thread = _model.FindThread(data.ThreadID, data.TimeStampQPC);
        if (thread is not null && (thread.ThreadInfo is null || overwrite))
        {
            thread.ThreadInfo = category;
        }
    }

    private void OnImage(ImageLoadTraceData data)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        if (!process.IsTarget && process.ProcessId != 0)
        {
            // Only the images of the converted processes and of the kernel are needed.
            return;
        }

        var isLoad = data.Opcode == (TraceEventOpcode)10 || data.Opcode == TraceEventOpcode.DataCollectionStart;
        var fileName = data.FileName;
        if (fileName.EndsWith("########", StringComparison.Ordinal) && _lastImageIdData is { } imageId && qpc == imageId.TimeStampQPC)
        {
            var index = fileName.IndexOf("########", StringComparison.Ordinal);
            fileName = fileName.Substring(0, index + 8) + "\\" + imageId.OriginalFileName;
        }

        var moduleFile = ImageLoadOrUnload(process, data, isLoad, fileName);

        if (_lastDbgData is { } dbg && qpc == dbg.TimeStampQPC)
        {
            moduleFile.PdbName = dbg.PdbFileName;
            moduleFile.PdbSignature = dbg.GuidSig;
            moduleFile.PdbAge = dbg.Age;
        }

        moduleFile.TimeDateStamp = data.TimeDateStamp;
        moduleFile.ImageChecksum = data.ImageChecksum;
        if (moduleFile.TimeDateStamp == 0 && _lastImageIdData is { } imageId2 && qpc == imageId2.TimeStampQPC)
        {
            moduleFile.TimeDateStamp = imageId2.TimeDateStamp;
        }

        if (_lastFileVersionData is { } fileVersion && qpc == fileVersion.TimeStampQPC)
        {
            moduleFile.ProductVersion = fileVersion.ProductVersion;
        }

        _lastModuleFile = moduleFile;
        _lastModuleFileQPC = qpc;
    }

    /// <summary>Records an image load or unload in a process, like <c>TraceLoadedModules.ImageLoadOrUnload</c>.</summary>
    private EtlModuleFile ImageLoadOrUnload(EtlProcess process, ImageLoadTraceData data, bool isLoad, string fileName)
    {
        var qpc = data.TimeStampQPC;
        var imageBase = data.ImageBase;
        var modules = process.LoadedModules;

        // The module already loaded at that address at that time, if any (a DCStart followed by a load of the
        // same image, or the unload of a loaded image).
        EtlLoadedModule? module = null;
        for (var i = modules.Count - 1; i >= 0; i--)
        {
            var candidate = modules[i];
            if (candidate.ImageBase == imageBase && candidate.LoadTimeQPC <= qpc && qpc <= candidate.UnloadTimeQPC)
            {
                module = candidate;
                break;
            }
        }

        if (module is null)
        {
            var fileIndex = _model.ModuleFiles.GetOrCreate(fileName, imageBase);
            _model.ModuleFiles[fileIndex].ImageSize = data.ImageSize;
            module = new EtlLoadedModule(fileIndex, imageBase);
            modules.Add(module);
        }

        var moduleFile = _model.ModuleFiles[module.ModuleFileIndex];
        if (!isLoad && moduleFile.FilePath.Length < fileName.Length)
        {
            // A truncated load file path, fixed up at unload time.
            moduleFile.FilePath = fileName.ToLowerInvariant();
        }

        moduleFile.ImageSize = data.ImageSize;

        if (isLoad)
        {
            module.LoadTimeQPC = data.Opcode == TraceEventOpcode.DataCollectionStart ? _model.SessionStartTimeQPC : qpc;
        }
        else
        {
            if (data.Opcode == TraceEventOpcode.DataCollectionStop)
            {
                if (process.ImageFileName.Length == 0 && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    process.ImageFileName = fileName;
                }
            }
            else
            {
                module.UnloadTimeQPC = qpc;
            }

            AssignModuleToUnresolvedAddressesInRange(process, imageBase, data.ImageSize, moduleFile.Index);
        }

        return moduleFile;
    }

    private void OnSample(SampledProfileTraceData data)
    {
        var tid = data.ThreadID;
        if (tid == 0 && !data.NonProcess)
        {
            // Idle: removed from the stream by TraceLog.
            return;
        }

        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        var thread = _model.GetOrCreateThread(tid, qpc, process);
        _lastThread = thread;
        _bookkeepingDone = true;
        thread.CpuSamples++;

        if (!process.IsTarget || qpc > thread.EndTimeQPC)
        {
            return;
        }

        var sampleIndex = thread.SampleTimesMs.Count;
        thread.SampleTimesMs.Add(data.TimeStampRelativeMSec);
        thread.SampleLastSwitchInMs.Add(thread.LastSwitchInMs);
        thread.SampleStacks.Add(-1);
        thread.RecentSamples.Add(qpc, sampleIndex);
    }

    private void OnContextSwitch(CSwitchTraceData data)
    {
        var qpc = data.TimeStampQPC;

        // TraceLog registers both threads of a switch as it records the event, creating records for threads it
        // has not seen yet (without a process), then does its generic bookkeeping for the event, which gives the
        // new thread's record its process; replicate so that the same thread records exist.
        var thread = _model.GetOrCreateThread(data.ThreadID, qpc, null);
        _model.GetOrCreateThread(data.OldThreadID, qpc, null);
        var processId = data.ProcessID;
        if (thread.Process.ProcessId == -1 && processId != -1)
        {
            var process = _model.GetOrCreateProcess(processId, qpc);
            thread = _model.GetOrCreateThread(data.ThreadID, qpc, process);
        }

        _lastThread = thread;
        _bookkeepingDone = true;

        if (!thread.Process.IsTarget || qpc > thread.EndTimeQPC)
        {
            return;
        }

        if (data.OldThreadID != thread.ThreadId)
        {
            // Switch-in.
            thread.LastSwitchInMs = data.TimeStampRelativeMSec;
        }
    }

    private unsafe void OnStackWalk(StackWalkStackTraceData data)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        var thread = _model.GetOrCreateThread(data.ThreadID, qpc, process);
        if (!process.IsTarget)
        {
            return;
        }

        var frameCount = data.FrameCount;
        if (frameCount <= 0)
        {
            return;
        }

        var model = _model;
        var stacks = model.Stacks;
        var pointerSize = data.PointerSize;
        var sampleIndex = thread.RecentSamples.Find(data.EventTimeStampQPC);

        if (IsKernelAddress(data.InstructionPointer(frameCount - 1), pointerSize))
        {
            // A kernel-mode fragment: it completes when the thread returns to user mode and its user-mode stack is
            // walked (see the user-mode branch below), or with no user frames at the end of the trace.
            if (sampleIndex < 0)
            {
                return;
            }

            var frames = new ulong[frameCount];
            for (var i = 0; i < frameCount; i++)
            {
                frames[i] = data.InstructionPointer(i);
            }

            var pending = thread.PendingKernelStacks ??= new List<EtlPendingKernelStack>();
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].SampleIndex == sampleIndex)
                {
                    // A second kernel fragment for the same sample replaces the first, like TraceLog.
                    pending[i] = new EtlPendingKernelStack(sampleIndex, frames);
                    return;
                }
            }

            pending.Add(new EtlPendingKernelStack(sampleIndex, frames));
            return;
        }

        // A user-mode stack, built from the root (the last address) down to the leaf, reusing the nodes of the
        // prefix it shares with the last stack walked on the thread.
        var lastAddresses = thread.LastStackAddresses;
        var lastNodes = thread.LastStackNodes;
        if (lastAddresses.Length < frameCount)
        {
            var newLength = Math.Max(frameCount, lastAddresses.Length * 2);
            Array.Resize(ref thread.LastStackAddresses, newLength);
            Array.Resize(ref thread.LastStackNodes, newLength);
            lastAddresses = thread.LastStackAddresses;
            lastNodes = thread.LastStackNodes;
        }

        var lastDepth = thread.LastStackDepth;
        var node = EtlStackTable.RootForProcess(process);
        var depth = 0;
        for (var i = frameCount - 1; i >= 0; i--, depth++)
        {
            var address = data.InstructionPointer(i);
            if (depth < lastDepth && lastAddresses[depth] == address)
            {
                node = lastNodes[depth];
                continue;
            }

            lastDepth = 0; // The prefix ends here; the rest is interned and recorded.
            node = stacks.Intern(node, address, process, model);
            lastAddresses[depth] = address;
            lastNodes[depth] = node;
        }

        thread.LastStackDepth = frameCount;

        var lastEmitQPC = thread.LastEmitStackOnExitFromKernelQPC;
        var completedTarget = false;
        if (!IsKernelAddress(data.InstructionPointer(0), pointerSize))
        {
            // The thread is back in user mode: every kernel fragment still waiting on this thread gets this
            // user-mode stack as its caller (its own user-mode stack was not walked while it was in the kernel).
            var pending = thread.PendingKernelStacks;
            if (pending is { Count: > 0 })
            {
                foreach (var pendingStack in pending)
                {
                    thread.SampleStacks[pendingStack.SampleIndex] = PrependKernelFrames(pendingStack.KernelFrames, node, process);
                    if (pendingStack.SampleIndex == sampleIndex)
                    {
                        completedTarget = true;
                    }
                }

                pending.Clear();
            }

            thread.LastEmitStackOnExitFromKernelQPC = qpc;
        }

        if (!completedTarget && sampleIndex >= 0)
        {
            if (data.EventTimeStampQPC < lastEmitQPC)
            {
                // Its stack was already completed by an earlier exit from the kernel.
                return;
            }

            // A fragment that starts in the kernel but ends in user mode (rare) is a complete stack of its own; if
            // a kernel fragment was already recorded for the sample, it goes on top.
            var pending = thread.PendingKernelStacks;
            if (pending is { Count: > 0 })
            {
                for (var i = 0; i < pending.Count; i++)
                {
                    if (pending[i].SampleIndex == sampleIndex)
                    {
                        node = PrependKernelFrames(pending[i].KernelFrames, node, process);
                        pending.RemoveAt(i);
                        break;
                    }
                }
            }

            thread.SampleStacks[sampleIndex] = node;
        }
    }

    /// <summary>Puts the frames of a kernel-mode fragment (leaf first) on top of <paramref name="userStack"/>.</summary>
    private int PrependKernelFrames(ulong[] kernelFrames, int userStack, EtlProcess process)
    {
        var stacks = _model.Stacks;
        var node = userStack;
        for (var i = kernelFrames.Length - 1; i >= 0; i--)
        {
            node = stacks.Intern(node, kernelFrames[i], process, _model);
        }

        return node;
    }

    private static bool IsKernelAddress(ulong address, int pointerSize) => EtlTraceModel.IsKernelAddress(address, pointerSize);

    // ---------------------------------------------------------------------------------------------------------
    // Symbol events (injected by an ETL merge)
    // ---------------------------------------------------------------------------------------------------------

    private void SetupSymbolCallbacks(ETWTraceEventSource source)
    {
        var symbols = new SymbolTraceEventParser(source);

        symbols.ImageIDDbgID_RSDS += data =>
        {
            if (_lastModuleFile is { } moduleFile && _lastModuleFileQPC == data.TimeStampQPC && string.IsNullOrEmpty(moduleFile.PdbName))
            {
                moduleFile.PdbName = data.PdbFileName;
                moduleFile.PdbSignature = data.GuidSig;
                moduleFile.PdbAge = data.Age;
                _lastDbgData = null;
            }
            else
            {
                _lastDbgData = new DbgIdInfo(data.TimeStampQPC, data.PdbFileName, data.GuidSig, data.Age);
            }
        };

        symbols.ImageID += data =>
        {
            if (_lastModuleFile is { } moduleFile && _lastModuleFileQPC == data.TimeStampQPC && moduleFile.TimeDateStamp == 0)
            {
                moduleFile.TimeDateStamp = data.TimeDateStamp;
                _lastImageIdData = null;
            }
            else
            {
                _lastImageIdData = new ImageIdInfo(data.TimeStampQPC, data.TimeDateStamp, data.OriginalFileName);
            }
        };

        symbols.ImageIDFileVersion += data =>
        {
            if (_lastModuleFile is { } moduleFile && _lastModuleFileQPC == data.TimeStampQPC && moduleFile.ProductVersion is null)
            {
                moduleFile.ProductVersion = data.ProductVersion;
                _lastFileVersionData = null;
            }
            else
            {
                _lastFileVersionData = new FileVersionInfoData(data.TimeStampQPC, data.FileVersion, data.ProductVersion);
            }
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // CLR events
    // ---------------------------------------------------------------------------------------------------------

    private void SetupClrCallbacks(ETWTraceEventSource source)
    {
        var clr = source.Clr;
        var rundown = new ClrRundownTraceEventParser(source);

        clr.LoaderModuleLoad += data => OnManagedModule(data, isLoad: true, isDCStartStop: false);
        clr.LoaderModuleUnload += data => OnManagedModule(data, isLoad: false, isDCStartStop: false);
        clr.LoaderModuleDCStopV2 += data => OnManagedModule(data, isLoad: false, isDCStartStop: true);
        rundown.LoaderModuleDCStop += data => OnManagedModule(data, isLoad: false, isDCStartStop: true);
        rundown.LoaderModuleDCStart += data => OnManagedModule(data, isLoad: false, isDCStartStop: true);

        clr.MethodLoadVerbose += OnMethodLoadVerbose;
        clr.MethodDCStartVerboseV2 += OnMethodLoadVerbose;
        rundown.MethodDCStartVerbose += OnMethodLoadVerbose;
        clr.MethodUnloadVerbose += OnMethodUnloadVerbose;
        clr.MethodDCStopVerboseV2 += OnMethodDCStopVerbose;
        rundown.MethodDCStopVerbose += OnMethodDCStopVerbose;
        clr.MethodLoad += OnMethodLoadUnloadNonVerbose;
        clr.MethodUnload += OnMethodLoadUnloadNonVerbose;
        clr.MethodJittingStarted += OnMethodJittingStarted;

        clr.GCStart += OnGCStart;
        clr.GCStop += OnGCStop;
        clr.GCSuspendEEStart += OnGCSuspendEEStart;
        clr.GCSuspendEEStop += OnGCSuspendEEStop;
        clr.GCRestartEEStart += OnGCRestartEEStart;
        clr.GCRestartEEStop += OnGCRestartEEStop;
        clr.GCHeapStats += OnGCHeapStats;
        clr.GCAllocationTick += OnGCAllocationTick;

        clr.GCFinalizersStart += data => CategorizeThread(data, ".NET Finalizer Thread");
        clr.GCFinalizersStop += data => CategorizeThread(data, ".NET Finalizer Thread");
        clr.GCMarkWithType += data =>
        {
            if (data.Type == 2)
            {
                AddMarkThread(data.ThreadID, data.TimeStampQPC, data.HeapNum);
            }
        };
        clr.ThreadPoolWorkerThreadAdjustmentSample += data => CategorizeThread(data, ".NET ThreadPool");
        clr.ThreadPoolIODequeue += data => CategorizeThread(data, ".NET IO ThreadPool Worker", overwrite: true);
    }

    private void AddMarkThread(int threadId, long timeStampQPC, int heapNum)
    {
        var thread = _model.FindThread(threadId, timeStampQPC);
        if (thread is not null && thread.ThreadInfo is null && thread.Process.ShouldCheckIsServerGC)
        {
            thread.Process.MarkThreadsInGC[threadId] = heapNum;
        }
    }

    /// <summary>
    /// Finds the thread a CLR event is converted for: the record owning its thread id at its time, provided it
    /// belongs to a converted process and the event is not past the thread's end.
    /// </summary>
    private EtlThread? RouteClrEvent(TraceEvent data)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        var thread = _model.GetOrCreateThread(data.ThreadID, qpc, process);
        _lastThread = thread;
        _bookkeepingDone = true;
        if (!thread.Process.IsTarget || qpc > thread.EndTimeQPC)
        {
            return null;
        }

        return thread;
    }

    private void OnManagedModule(ModuleLoadUnloadTraceData data, bool isLoad, bool isDCStartStop)
    {
        var qpc = data.TimeStampQPC;
        var process = _model.GetOrCreateProcess(data.ProcessID, qpc);
        if (!process.IsTarget)
        {
            return;
        }

        var ilPath = data.ModuleILPath;
        var nativePath = data.ModuleNativePath;
        if (ilPath == nativePath)
        {
            var index = ilPath.LastIndexOf(".ni.", ilPath.Length, 8, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                ilPath = ilPath.Substring(0, index) + ilPath.Substring(index + 3);
            }
        }

        var module = GetOrCreateManagedModule(process, data.ModuleID, qpc, ilPath);
        module.AssemblyId = data.AssemblyID;

        var moduleFile = _model.ModuleFiles[module.ModuleFileIndex];
        if (!moduleFile.HasFilePath)
        {
            _model.ModuleFiles.SetFileName(moduleFile, ilPath);
        }

        if (moduleFile.PdbSignature == Guid.Empty && data.ManagedPdbSignature != Guid.Empty)
        {
            moduleFile.PdbSignature = data.ManagedPdbSignature;
            moduleFile.PdbAge = data.ManagedPdbAge;
            moduleFile.PdbName = data.ManagedPdbBuildPath;
        }

        if (!isLoad && !isDCStartStop)
        {
            module.UnloadTimeQPC = qpc;
        }
    }

    /// <summary>
    /// Gets the managed module <paramref name="moduleId"/> of <paramref name="process"/> loaded at
    /// <paramref name="timeQPC"/>, creating a record (with a placeholder file when <paramref name="ilPath"/> is
    /// unknown) when there is none or when the module last seen under the id was unloaded by then, like
    /// <c>TraceLoadedModules.GetOrCreateManagedModule</c>.
    /// </summary>
    private EtlManagedModule GetOrCreateManagedModule(EtlProcess process, long moduleId, long timeQPC, string? ilPath)
    {
        if (!process.ManagedModules.TryGetValue(moduleId, out var module) || timeQPC >= module.UnloadTimeQPC)
        {
            var fileIndex = _model.ModuleFiles.GetOrCreate(ilPath, 0);
            module = new EtlManagedModule(moduleId, fileIndex);
            process.ManagedModules[moduleId] = module;
            process.ManagedModuleList.Add(module);
        }

        return module;
    }

    private void OnMethodLoadVerbose(MethodLoadUnloadVerboseTraceData data)
    {
        if (data.IsJitted)
        {
            var process = _model.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC);
            if (process.IsTarget)
            {
                // Every load is kept, including a second load at the same address (a re-jitted method, a
                // trampoline reported again with another size): they resolve the sampled addresses in load
                // order at the end of the pass, like TraceLog.
                GetOrCreateManagedModule(process, data.ModuleID, data.TimeStampQPC, null);
                process.JitMethods.Add(new EtlJitMethodLoad(data.MethodStartAddress, data.MethodSize, GetFullName(data), data.ModuleID, data.TimeStampQPC));
            }
        }

        // The load of a method that is neither jitted nor dynamic (and a MethodDCStopVerbose) is dropped by
        // TraceLog before the converter could pair it with a MethodJittingStarted.
        if ((data.IsJitted || data.IsDynamic) && data.ID != (TraceEventID)139)
        {
            OnMethodLoadUnloadMarker(data, data.MethodID, data.OptimizationTier);
        }
    }

    private void OnMethodUnloadVerbose(MethodLoadUnloadVerboseTraceData data)
    {
        var process = _model.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC);
        if (process.IsTarget)
        {
            // Resolves the addresses sampled in the method so far, so that a later method at the same address gets
            // code addresses of its own.
            ResolveJitMethod(process, data.MethodStartAddress, data.MethodSize, GetFullName(data), data.ModuleID, data.TimeStampQPC);
        }

        if (data.IsJitted)
        {
            OnMethodLoadUnloadMarker(data, data.MethodID, data.OptimizationTier);
        }
    }

    /// <summary>
    /// The end-of-trace rundown of the loaded methods resolves the addresses sampled in each of them, jitted or
    /// not (a runtime helper or trampoline is reported here with the <c>JitHelper</c> flag only), like
    /// <c>TraceLog</c> does.
    /// </summary>
    private void OnMethodDCStopVerbose(MethodLoadUnloadVerboseTraceData data)
    {
        var process = _model.GetOrCreateProcess(data.ProcessID, data.TimeStampQPC);
        if (process.IsTarget)
        {
            ResolveJitMethod(process, data.MethodStartAddress, data.MethodSize, GetFullName(data), data.ModuleID, data.TimeStampQPC);
        }
    }

    private void OnMethodLoadUnloadNonVerbose(MethodLoadUnloadTraceData data)
    {
        OnMethodLoadUnloadMarker(data, data.MethodID, data.OptimizationTier);
    }

    private void OnMethodJittingStarted(MethodJittingStartedTraceData data)
    {
        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        var signature = data.MethodSignature;
        var indexOfParent = signature.IndexOf('(');
        if (indexOfParent >= 0)
        {
            signature = signature.Substring(indexOfParent);
        }

        var process = thread.Process;
        var jitCompile = new JitCompileEvent
        {
            FullName = $"{data.MethodNamespace}.{data.MethodName}{signature}",
            Assembly = GetManagedModuleFileName(process, data.ModuleID),
            MethodILSize = data.MethodILSize
        };

        process.JitCompilePending[data.MethodID] = (jitCompile, data.TimeStampRelativeMSec);
    }

    /// <summary>The file name of the managed module <paramref name="moduleId"/> of <paramref name="process"/>, or <c>null</c> when the trace has no load event for it.</summary>
    private string? GetManagedModuleFileName(EtlProcess process, long moduleId)
    {
        if (process.ManagedModules.TryGetValue(moduleId, out var module))
        {
            var file = _model.ModuleFiles[module.ModuleFileIndex];
            if (file.HasFilePath)
            {
                return Path.GetFileName(file.FilePath);
            }
        }

        return null;
    }

    private void OnMethodLoadUnloadMarker(TraceEvent data, long methodId, OptimizationTier optimizationTier)
    {
        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        var process = thread.Process;
        if (process.JitCompilePending.Remove(methodId, out var pending))
        {
            // The tier is only reported once the code is loaded, not on MethodJittingStarted.
            pending.Event.OptimizationTier = optimizationTier;
            thread.Markers.Add(new EtlMarker(EtlMarkerKind.JitCompile, pending.StartTimeMs, data.TimeStampRelativeMSec, pending.Event));
        }
    }

    private void OnGCStart(GCStartTraceData data)
    {
        // TraceLog's server GC detection.
        var processForGc = _model.FindProcess(data.ProcessID, data.TimeStampQPC);
        if (processForGc is not null && processForGc.MarkThreadsInGC.Count == 0 && !processForGc.ShouldCheckIsServerGC)
        {
            processForGc.ShouldCheckIsServerGC = true;
        }

        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        var gcEvent = new GCEvent(data.Depth)
        {
            Reason = data.Reason.ToString(),
            Count = data.Count,
            Depth = data.Depth,
            GCType = data.Type.ToString()
        };

        thread.Process.GcCollections.OnGcStart(thread, data.Count, data.Depth, data.TimeStampRelativeMSec, gcEvent);
    }

    private void OnGCStop(GCEndTraceData data)
    {
        var processForGc = _model.FindProcess(data.ProcessID, data.TimeStampQPC);
        if (processForGc is not null)
        {
            if (processForGc.MarkThreadsInGC.Count > 0)
            {
                processForGc.ShouldCheckIsServerGC = false;
            }

            if (!processForGc.IsServerGC && processForGc.MarkThreadsInGC.Count > 1)
            {
                // TraceLog tests the categorization of the thread of the *last event* here instead of each mark
                // thread's own, so it labels all or none of them; each thread is checked on its own instead.
                processForGc.IsServerGC = true;
                foreach (var gcThread in processForGc.Threads)
                {
                    if (gcThread.ThreadInfo is null && processForGc.MarkThreadsInGC.TryGetValue(gcThread.ThreadId, out var heapNum))
                    {
                        gcThread.ThreadInfo = $".NET Server GC Thread({heapNum})";
                    }
                }
            }
        }

        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        // The collection may have started on another thread - its markers go there.
        if (thread.Process.GcCollections.OnGcEnd(data.Count, data.Depth, data.TimeStampRelativeMSec) is { } collection)
        {
            var startThread = collection.Thread;
            startThread.Markers.Add(new EtlMarker(EtlMarkerKind.GcEvent, collection.StartTimeInMs, collection.EndTimeInMs, collection.Payload));
            if (collection.HasConcurrentPhase)
            {
                startThread.Markers.Add(new EtlMarker(EtlMarkerKind.GcConcurrent, collection.ConcurrentStartTimeInMs!.Value, collection.ConcurrentEndTimeInMs!.Value, new GCConcurrentEvent()));
            }
        }
    }

    private void OnGCSuspendEEStart(GCSuspendEETraceData data)
    {
        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        var gcSuspendEEEvent = new GCSuspendExecutionEngineEvent
        {
            Reason = data.Reason.ToString(),
            Count = data.Count
        };

        (thread.GcSuspendEEEvents ??= new()).Push((data.TimeStampRelativeMSec, gcSuspendEEEvent));
        thread.Process.GcCollections.OnSuspendEEStart(data.TimeStampRelativeMSec);
    }

    private void OnGCSuspendEEStop(GCNoUserDataTraceData data)
    {
        var thread = RouteClrEvent(data);
        if (thread is null || thread.GcSuspendEEEvents is not { Count: > 0 } suspends)
        {
            return;
        }

        var (startTimeMs, gcSuspendEEEvent) = suspends.Pop();
        thread.Markers.Add(new EtlMarker(EtlMarkerKind.GcSuspendEE, startTimeMs, data.TimeStampRelativeMSec, gcSuspendEEEvent));
    }

    private void OnGCRestartEEStart(GCNoUserDataTraceData data)
    {
        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        (thread.GcRestartEEEvents ??= new()).Push(data.TimeStampRelativeMSec);
    }

    private void OnGCRestartEEStop(GCNoUserDataTraceData data)
    {
        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        thread.Process.GcCollections.OnRestartEEStop(data.TimeStampRelativeMSec);

        if (thread.GcRestartEEEvents is { Count: > 0 } restarts)
        {
            var startTimeMs = restarts.Pop();
            thread.Markers.Add(new EtlMarker(EtlMarkerKind.GcRestartEE, startTimeMs, data.TimeStampRelativeMSec, null));
        }
    }

    private void OnGCHeapStats(GCHeapStatsTraceData data)
    {
        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        var heapStatEvent = new GCHeapStatsEvent
        {
            TotalHeapSize = data.TotalHeapSize,
            TotalPromoted = data.TotalPromoted,
            GenerationSize0 = data.GenerationSize0,
            TotalPromotedSize0 = data.TotalPromotedSize0,
            GenerationSize1 = data.GenerationSize1,
            TotalPromotedSize1 = data.TotalPromotedSize1,
            GenerationSize2 = data.GenerationSize2,
            TotalPromotedSize2 = data.TotalPromotedSize2,
            GenerationSize3 = data.GenerationSize3,
            TotalPromotedSize3 = data.TotalPromotedSize3,
            GenerationSize4 = data.GenerationSize4,
            TotalPromotedSize4 = data.TotalPromotedSize4,
            FinalizationPromotedSize = data.FinalizationPromotedSize,
            FinalizationPromotedCount = data.FinalizationPromotedCount,
            PinnedObjectCount = data.PinnedObjectCount,
            SinkBlockCount = data.SinkBlockCount,
            GCHandleCount = data.GCHandleCount
        };

        var timeMs = data.TimeStampRelativeMSec;
        thread.Process.GcHeapStatsEvents.Add((timeMs, heapStatEvent));
        thread.Markers.Add(new EtlMarker(EtlMarkerKind.GcHeapStats, timeMs, timeMs, heapStatEvent));
    }

    private void OnGCAllocationTick(GCAllocationTickTraceData data)
    {
        var thread = RouteClrEvent(data);
        if (thread is null)
        {
            return;
        }

        var allocationTickEvent = new GCAllocationTickEvent
        {
            AllocationAmount = data.AllocationAmount,
            AllocationKind = data.AllocationKind switch
            {
                GCAllocationKind.Small => "Small",
                GCAllocationKind.Large => "Large",
                GCAllocationKind.Pinned => "Pinned",
                _ => "Unknown"
            },
            TypeName = data.TypeName,
            HeapIndex = data.HeapIndex
        };

        var timeMs = data.TimeStampRelativeMSec;
        thread.Markers.Add(new EtlMarker(EtlMarkerKind.GcAllocationTick, timeMs, timeMs, allocationTickEvent));
    }

    /// <summary>The full name of a method, like <c>TraceLog.GetFullName</c>: namespace, name and the parameter list of its signature.</summary>
    private static string GetFullName(MethodLoadUnloadVerboseTraceData data)
    {
        var signature = data.MethodSignature;
        var index = signature.IndexOf('(');
        return string.Concat(data.MethodNamespace, ".", data.MethodName, index < 0 ? "" : signature.Substring(index));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Address resolution bookkeeping (modules and JIT-compiled methods)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Attributes every code address of <paramref name="process"/> in the range that is not resolved to a method to
    /// the module file, like <c>TraceCodeAddresses.ForAllUnresolvedCodeAddressesInRange</c> with <c>considerResolved: false</c>.
    /// </summary>
    private void AssignModuleToUnresolvedAddressesInRange(EtlProcess process, ulong start, long length, int moduleFileIndex)
    {
        var unresolved = process.UnresolvedCodeAddresses;
        if (unresolved.Count == 0)
        {
            return;
        }

        var codeAddresses = _model.CodeAddresses;
        var (first, end) = FindUnresolvedRange(process, start, length);
        for (var i = first; i < end; i++)
        {
            var codeAddress = unresolved[i];
            if (codeAddresses.MethodIndex[codeAddress] == -1)
            {
                codeAddresses.ModuleFileIndex[codeAddress] = moduleFileIndex;
            }
        }
    }

    /// <summary>
    /// Resolves every code address of <paramref name="process"/> in the range that has no method yet to a new method
    /// named <paramref name="fullName"/>, and retires those addresses so that later samples at the same address get
    /// a code address of their own, like <c>TraceCodeAddresses.AddMethod</c>.
    /// </summary>
    private void ResolveJitMethod(EtlProcess process, ulong start, int length, string fullName, long moduleId, long timeQPC)
    {
        var unresolved = process.UnresolvedCodeAddresses;
        if (unresolved.Count == 0)
        {
            return;
        }

        var (first, end) = FindUnresolvedRange(process, start, length);
        if (first >= end)
        {
            return;
        }

        // Resolved entries stay in the list (removing them from the middle of a list of hundreds of thousands of
        // addresses, once per method, was measured to cost seconds); the range scans skip them.
        var codeAddresses = _model.CodeAddresses;
        var methodIndex = -1;
        for (var i = first; i < end; i++)
        {
            var codeAddress = unresolved[i];
            if (codeAddresses.MethodIndex[codeAddress] != -1)
            {
                continue;
            }

            if (methodIndex == -1)
            {
                var module = GetOrCreateManagedModule(process, moduleId, timeQPC, null);
                methodIndex = _model.Methods.Add(fullName, module.ModuleFileIndex);
            }

            codeAddresses.MethodIndex[codeAddress] = methodIndex;
            if (process.ProcessId != 0)
            {
                process.CodeAddressByAddress.Remove(codeAddresses.Address[codeAddress]);
            }
        }
    }

    /// <summary>Finds the slice of the (sorted) unresolved code addresses of <paramref name="process"/> falling in the range.</summary>
    private (int First, int End) FindUnresolvedRange(EtlProcess process, ulong start, long length)
    {
        var unresolved = process.UnresolvedCodeAddresses;
        var addresses = _model.CodeAddresses.Address;
        if (!process.UnresolvedCodeAddressesSorted)
        {
            unresolved.Sort((x, y) => addresses[x].CompareTo(addresses[y]));
            process.UnresolvedCodeAddressesSorted = true;
        }

        // Lower bound of start.
        int lo = 0, hi = unresolved.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (addresses[unresolved[mid]] < start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var first = lo;
        var rangeEnd = start + (ulong)length;
        var end = first;
        while (end < unresolved.Count && addresses[unresolved[end]] < rangeEnd)
        {
            end++;
        }

        return (first, end);
    }

    // ---------------------------------------------------------------------------------------------------------
    // End of the pass
    // ---------------------------------------------------------------------------------------------------------

    private void Finish()
    {
        var model = _model;

        // Kernel fragments still waiting for the thread to leave the kernel get no user-mode frames.
        foreach (var thread in model.Threads)
        {
            if (thread.PendingKernelStacks is { Count: > 0 } pending)
            {
                foreach (var pendingStack in pending)
                {
                    thread.SampleStacks[pendingStack.SampleIndex] = PrependKernelFrames(pendingStack.KernelFrames, EtlStackTable.RootForProcess(thread.Process), thread.Process);
                }

                pending.Clear();
            }
        }

        var sessionEnd = model.SessionEndTimeQPC;
        foreach (var process in model.Processes)
        {
            foreach (var module in process.LoadedModules)
            {
                if (module.UnloadTimeQPC == long.MaxValue && module.ImageBase != 0)
                {
                    var file = model.ModuleFiles[module.ModuleFileIndex];
                    AssignModuleToUnresolvedAddressesInRange(process, module.ImageBase, file.ImageSize, file.Index);
                }

                if (module.UnloadTimeQPC > sessionEnd)
                {
                    module.UnloadTimeQPC = sessionEnd;
                }
            }

            if (process.EndTimeQPC > sessionEnd)
            {
                process.EndTimeQPC = sessionEnd;
            }
        }

        // JIT-compiled methods resolve the addresses sampled in them, in load order.
        foreach (var process in model.Processes)
        {
            foreach (var method in process.JitMethods)
            {
                ResolveJitMethod(process, method.StartAddress, method.Size, method.FullName, method.ModuleId, method.TimeStampQPC);
            }
        }

        // The number of code addresses attributed to each module file, to skip symbol lookups for files without any.
        var codeAddresses = model.CodeAddresses;
        var files = model.ModuleFiles;
        for (var i = 0; i < codeAddresses.Count; i++)
        {
            var moduleFileIndex = codeAddresses.ModuleFileIndex[i];
            if (moduleFileIndex >= 0)
            {
                files[moduleFileIndex].CodeAddressCount++;
            }
        }
    }
}

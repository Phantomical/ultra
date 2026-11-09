// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ultra.Core.Etw.UserMode;

/// <summary>
/// Samples the threads of other processes without the kernel session: at each tick, every thread that ran since the
/// previous tick (its cycle time moved) is suspended, its context and the used part of its stack are copied, it is
/// resumed, and its stack is walked from the copy (see <see cref="X64Unwinder"/>). Samples, threads and images are
/// written as <see cref="UserModeSamplerEvents"/> into the ETW session that records the runtime events.
/// </summary>
/// <remarks>
/// Only same-user processes can be sampled (it needs <c>PROCESS_VM_READ</c> and <c>THREAD_SUSPEND_RESUME</c>), and only
/// x64 ones. A thread is suspended for the time of a <c>GetThreadContext</c> and a copy of its stack, and is resumed on
/// every path. A thread that ran only between two ticks and is blocked at the tick is sampled in its blocked state.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class UserModeSampler : IDisposable
{
    private const int MaxStackCopy = 1 << 20;
    private const int ThreadRefreshIntervalMs = 200;
    private const int ImageRefreshIntervalMs = 1000;

    private readonly float _intervalMs;
    private readonly UserModeSamplerEventWriter _writer = new();
    private readonly List<TargetProcess> _targets = new();
    private readonly ConcurrentQueue<int> _pendingProcessIds = new();
    private readonly X64StackSnapshot _stack = new(MaxStackCopy);
    private readonly ulong[] _frames = new ulong[UserModeSamplerEvents.MaxFrames];
    private readonly ulong[] _registers = new ulong[16];
    private readonly void* _context;
    private readonly Stopwatch _clock = new();
    private Thread? _thread;
    private volatile bool _stopRequested;
    private ulong _cyclesPerInterval;

    // Statistics, reported by Summary.
    private long _sampleCount;
    private long _frameCount;
    private long _ticks;
    private long _lateTicks;
    private long _failedSuspends;
    private long _walkTicks;
    private long _refreshTicks;
    private long _threadRefreshTicks;
    private long _failedContexts;
    private double _maxSuspendedMs;
    private double _totalSuspendedMs;
    private readonly long[] _stops = new long[Enum.GetValues<X64UnwindStop>().Length];

    private sealed class TargetProcess(int processId, IntPtr handle)
    {
        public int ProcessId { get; } = processId;
        public IntPtr Handle { get; } = handle;
        public required TargetProcessCodeMap CodeMap { get; init; }
        public required X64Unwinder Unwinder { get; init; }
        public Dictionary<int, TargetThread> Threads { get; } = new();
        public bool Exited { get; set; }
    }

    private sealed class TargetThread(int threadId, IntPtr handle)
    {
        public int ThreadId { get; } = threadId;
        public IntPtr Handle { get; } = handle;
        public ulong StackBase { get; set; }
        public ulong LastCycles { get; set; }
        public ulong CycleCredit { get; set; }
        public bool Seen { get; set; }
    }

    public UserModeSampler(float intervalMs)
    {
        _intervalMs = Math.Clamp(intervalMs, 0.1f, 1000f);
        _context = NativeMemory.AlignedAlloc(UserModeNative.ContextSize, 16);
    }

    /// <summary>
    /// Starts sampling <paramref name="processIds"/>. Their process, threads and images are written before this
    /// method returns, so that they precede in the trace every runtime event of these processes.
    /// </summary>
    /// <exception cref="InvalidOperationException">A process cannot be opened for sampling.</exception>
    public void Start(IEnumerable<int> processIds)
    {
        _clock.Start();
        _cyclesPerInterval = (ulong)(MeasureCyclesPerMillisecond() * _intervalMs);
        foreach (var processId in processIds)
        {
            AddProcessNow(processId);
        }

        _thread = new Thread(Run)
        {
            Name = "Ultra-UserModeSampler",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>Adds a process to sample once the sampler runs (e.g. a process launched after <see cref="Start"/>).</summary>
    public void AddProcess(int processId) => _pendingProcessIds.Enqueue(processId);

    /// <summary>Stops sampling and waits for the sampling thread.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _thread?.Join();
        _thread = null;
    }

    /// <summary>A one-line summary of the sampling, for the log.</summary>
    public string Summary
    {
        get
        {
            var stops = new StringBuilder();
            var names = Enum.GetNames<X64UnwindStop>();
            for (var i = 0; i < _stops.Length; i++)
            {
                if (_stops[i] > 0)
                {
                    stops.Append($" {names[i]}={_stops[i]}");
                }
            }

            var averageDepth = _sampleCount > 0 ? (double)_frameCount / _sampleCount : 0;
            var averageSuspended = _sampleCount > 0 ? _totalSuspendedMs * 1000.0 / _sampleCount : 0;
            return $"User-mode sampler: {_sampleCount:N0} samples, {averageDepth:0.0} frames avg, {_ticks:N0} ticks ({_lateTicks:N0} late), suspended {averageSuspended:0}us avg / {_maxSuspendedMs * 1000:0}us max, failed suspend/context {_failedSuspends}/{_failedContexts}, ETW write failures {_writer.FailedWrites}, walk {_walkTicks * 1000.0 / Stopwatch.Frequency:0}ms, refresh {_refreshTicks * 1000.0 / Stopwatch.Frequency:0}ms (threads {_threadRefreshTicks * 1000.0 / Stopwatch.Frequency:0}ms), sampling {_clock.ElapsedMilliseconds:N0}ms, dynamic function tables {string.Join("/", _targets.Select(x => x.CodeMap.DynamicTableCount))}, stack walk stops:{stops}";
        }
    }

    private void AddProcessNow(int processId)
    {
        var handle = UserModeNative.OpenProcess(UserModeNative.PROCESS_QUERY_INFORMATION | UserModeNative.PROCESS_VM_READ | UserModeNative.SYNCHRONIZE, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Unable to open process {processId} for user-mode sampling (error {Marshal.GetLastWin32Error()})");
        }

        var codeMap = new TargetProcessCodeMap(handle);
        var target = new TargetProcess(processId, handle) { CodeMap = codeMap, Unwinder = new X64Unwinder(codeMap) };
        _targets.Add(target);

        var buffer = stackalloc char[1024];
        uint length = 1024;
        var imagePath = UserModeNative.QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : string.Empty;
        var size = UserModeSamplerEvents.EncodeProcessInfo(_writer.Buffer, new UserModeSamplerEvents.ProcessInfo(processId, _intervalMs, imagePath));
        _writer.Write(UserModeSamplerEvents.ProcessInfoId, size);

        RefreshImages(target, rundown: true);
        codeMap.RefreshDynamicTables();
        RefreshThreads(rundown: true);
    }

    private void Run()
    {
        var timer = UserModeNative.CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, UserModeNative.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, UserModeNative.TIMER_ALL_ACCESS);
        try
        {
            var intervalTicks = (long)(_intervalMs * Stopwatch.Frequency / 1000.0);
            var next = _clock.ElapsedTicks;
            long lastThreadRefresh = 0, lastImageRefresh = 0;
            while (!_stopRequested)
            {
                while (_pendingProcessIds.TryDequeue(out var processId))
                {
                    try
                    {
                        AddProcessNow(processId);
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited or cannot be opened: nothing to sample.
                    }
                }

                var refreshStart = _clock.ElapsedTicks;
                var nowMs = _clock.ElapsedMilliseconds;
                if (nowMs - lastThreadRefresh >= ThreadRefreshIntervalMs)
                {
                    RefreshThreads(rundown: false);
                    lastThreadRefresh = nowMs;
                    _threadRefreshTicks += _clock.ElapsedTicks - refreshStart;
                }

                if (nowMs - lastImageRefresh >= ImageRefreshIntervalMs)
                {
                    foreach (var target in _targets)
                    {
                        if (!target.Exited)
                        {
                            RefreshImages(target, rundown: false);
                        }
                    }

                    lastImageRefresh = nowMs;
                }

                _refreshTicks += _clock.ElapsedTicks - refreshStart;

                foreach (var target in _targets)
                {
                    foreach (var thread in target.Threads.Values)
                    {
                        SampleIfRunning(target, thread);
                    }
                }

                _ticks++;
                next += intervalTicks;
                var remaining = next - _clock.ElapsedTicks;
                if (remaining <= 0)
                {
                    // Behind schedule (the tick took longer than the interval): skip the missed ticks.
                    _lateTicks++;
                    next = _clock.ElapsedTicks;
                    continue;
                }

                if (timer != IntPtr.Zero)
                {
                    // Relative due time, in 100ns units.
                    var dueTime = -(long)(remaining * 10_000_000.0 / Stopwatch.Frequency);
                    if (UserModeNative.SetWaitableTimer(timer, dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        UserModeNative.WaitForSingleObject(timer, 1000);
                        continue;
                    }
                }

                Thread.Sleep(TimeSpan.FromTicks(remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            }
        }
        finally
        {
            if (timer != IntPtr.Zero)
            {
                UserModeNative.CloseHandle(timer);
            }
        }
    }

    private void SampleIfRunning(TargetProcess target, TargetThread thread)
    {
        if (_cyclesPerInterval > 0 && UserModeNative.QueryThreadCycleTime(thread.Handle, out var cycles))
        {
            // A thread is sampled each time it has used one interval of CPU time, so that its samples are
            // proportional to its CPU time like the kernel's: a thread busy half of the time is sampled every
            // other tick. The credit is capped so that a thread does not catch up after a late tick.
            var credit = Math.Min(thread.CycleCredit + (cycles - thread.LastCycles), 2 * _cyclesPerInterval);
            thread.LastCycles = cycles;
            if (credit < _cyclesPerInterval)
            {
                thread.CycleCredit = credit;
                return;
            }

            thread.CycleCredit = credit - _cyclesPerInterval;
        }

        var registers = _registers.AsSpan();
        var stack = _stack;
        var start = _clock.ElapsedTicks;
        if (UserModeNative.SuspendThread(thread.Handle) == uint.MaxValue)
        {
            _failedSuspends++;
            return;
        }

        ulong rip;
        try
        {
            *(uint*)((byte*)_context + UserModeNative.ContextFlagsOffset) = UserModeNative.CONTEXT_CONTROL_INTEGER;
            if (!UserModeNative.GetThreadContext(thread.Handle, _context))
            {
                _failedContexts++;
                return;
            }

            new ReadOnlySpan<ulong>((byte*)_context + UserModeNative.ContextIntegerRegistersOffset, 16).CopyTo(registers);
            rip = *(ulong*)((byte*)_context + UserModeNative.ContextRipOffset);

            var rsp = registers[4];
            var length = thread.StackBase > rsp ? (int)Math.Min(thread.StackBase - rsp, (ulong)MaxStackCopy) : 4096;
            stack.Base = rsp;
            stack.Length = 0;
            fixed (byte* data = stack.Data)
            {
                if (UserModeNative.ReadProcessMemory(target.Handle, rsp, data, (nuint)length, out var read))
                {
                    stack.Length = (int)read;
                }
            }
        }
        finally
        {
            UserModeNative.ResumeThread(thread.Handle);
            var suspendedMs = (_clock.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;
            _totalSuspendedMs += suspendedMs;
            if (suspendedMs > _maxSuspendedMs)
            {
                _maxSuspendedMs = suspendedMs;
            }
        }

        var walkStart = _clock.ElapsedTicks;
        var count = target.Unwinder.Walk(registers, rip, stack, _frames, out var stop);
        _walkTicks += _clock.ElapsedTicks - walkStart;
        _stops[(int)stop]++;
        _sampleCount++;
        _frameCount += count;

        var flags = stop == X64UnwindStop.End ? (ushort)0 : UserModeSamplerEvents.SampleFlagTruncated;
        var size = UserModeSamplerEvents.EncodeSample(_writer.Buffer, target.ProcessId, thread.ThreadId, flags, _frames.AsSpan(0, count));
        _writer.Write(UserModeSamplerEvents.SampleId, size);
    }

    private void RefreshImages(TargetProcess target, bool rundown)
    {
        var loaded = new List<TargetImage>();
        var unloaded = new List<TargetImage>();
        target.CodeMap.RefreshImages(loaded, unloaded);
        foreach (var image in loaded)
        {
            var size = UserModeSamplerEvents.EncodeModuleLoad(_writer.Buffer, new UserModeSamplerEvents.ModuleLoad(
                target.ProcessId, image.Base, image.Size, image.Checksum, image.TimeDateStamp, rundown ? UserModeSamplerEvents.FlagRundown : (byte)0, image.Path));
            _writer.Write(UserModeSamplerEvents.ModuleLoadId, size);
        }

        foreach (var image in unloaded)
        {
            var size = UserModeSamplerEvents.EncodeModuleUnload(_writer.Buffer, target.ProcessId, image.Base);
            _writer.Write(UserModeSamplerEvents.ModuleUnloadId, size);
        }
    }

    /// <summary>Finds the threads of the sampled processes, writing the new ones and the ones that exited.</summary>
    private void RefreshThreads(bool rundown)
    {
        var newThreads = new List<(TargetProcess Target, TargetThread Thread, long CreationTime)>();
        var targets = new List<TargetProcess>();
        const uint threadAccess = UserModeNative.THREAD_SUSPEND_RESUME | UserModeNative.THREAD_GET_CONTEXT | UserModeNative.THREAD_QUERY_INFORMATION | UserModeNative.SYNCHRONIZE;
        foreach (var target in _targets)
        {
            if (target.Exited)
            {
                continue;
            }

            if (UserModeNative.WaitForSingleObject(target.Handle, 0) == UserModeNative.WAIT_OBJECT_0)
            {
                target.Exited = true;
            }

            targets.Add(target);
            foreach (var thread in target.Threads.Values)
            {
                thread.Seen = false;
            }

            // The threads of the process only (a system-wide thread snapshot costs tens of milliseconds); each
            // step opens a handle to the next thread, kept for a new thread and closed otherwise.
            var current = IntPtr.Zero;
            var currentKept = false;
            while (!target.Exited && UserModeNative.NtGetNextThread(target.Handle, current, threadAccess, 0, 0, out var next) == 0)
            {
                if (current != IntPtr.Zero && !currentKept)
                {
                    UserModeNative.CloseHandle(current);
                }

                current = next;
                currentKept = false;
                var threadId = (int)UserModeNative.GetThreadId(next);
                if (threadId == 0 || UserModeNative.WaitForSingleObject(next, 0) == UserModeNative.WAIT_OBJECT_0)
                {
                    continue;
                }

                if (target.Threads.TryGetValue(threadId, out var existing))
                {
                    existing.Seen = true;
                    continue;
                }

                var handle = next;
                currentKept = true;
                var thread = new TargetThread(threadId, handle) { Seen = true, StackBase = GetStackBase(target, handle) };
                UserModeNative.QueryThreadCycleTime(handle, out var cycles);
                // A thread found while sampling runs is sampled on its first tick; one found at the start only once it runs.
                thread.LastCycles = rundown ? cycles : 0;
                target.Threads.Add(threadId, thread);
                UserModeNative.GetThreadTimes(handle, out var creationTime, out _, out _, out _);
                newThreads.Add((target, thread, creationTime));
            }

            if (current != IntPtr.Zero && !currentKept)
            {
                UserModeNative.CloseHandle(current);
            }
        }

        // The first thread of a process is its main thread; it is named so when it has no name of its own.
        var mainThreads = new Dictionary<TargetProcess, TargetThread>();
        if (rundown)
        {
            foreach (var group in newThreads.GroupBy(x => x.Target))
            {
                mainThreads[group.Key] = group.MinBy(x => x.CreationTime).Thread;
            }
        }

        foreach (var (target, thread, _) in newThreads)
        {
            var name = GetThreadName(thread.Handle);
            if (name.Length == 0 && mainThreads.TryGetValue(target, out var mainThread) && mainThread == thread)
            {
                name = "Main Thread";
            }

            var size = UserModeSamplerEvents.EncodeThreadStart(_writer.Buffer, new UserModeSamplerEvents.ThreadStart(
                target.ProcessId, thread.ThreadId, rundown ? UserModeSamplerEvents.FlagRundown : (byte)0, name));
            _writer.Write(UserModeSamplerEvents.ThreadStartId, size);
        }

        foreach (var target in targets)
        {
            List<int>? exited = null;
            foreach (var thread in target.Threads.Values)
            {
                if (!thread.Seen)
                {
                    (exited ??= new()).Add(thread.ThreadId);
                }
            }

            if (exited is null)
            {
                continue;
            }

            foreach (var threadId in exited)
            {
                target.Threads.Remove(threadId, out var thread);
                UserModeNative.CloseHandle(thread!.Handle);
                var size = UserModeSamplerEvents.EncodeThreadStop(_writer.Buffer, target.ProcessId, threadId);
                _writer.Write(UserModeSamplerEvents.ThreadStopId, size);
            }
        }
    }

    /// <summary>
    /// The rate of thread cycle times (the time-stamp counter on current CPUs), measured by spinning for a few
    /// milliseconds; 0 when cycle times are not available, in which case every thread is sampled at every tick.
    /// </summary>
    private static double MeasureCyclesPerMillisecond()
    {
        var thread = UserModeNative.GetCurrentThread();
        if (!UserModeNative.QueryThreadCycleTime(thread, out var startCycles))
        {
            return 0;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < 5)
        {
        }

        var elapsedMs = clock.Elapsed.TotalMilliseconds;
        UserModeNative.QueryThreadCycleTime(thread, out var endCycles);
        return (endCycles - startCycles) / elapsedMs;
    }

    private static ulong GetStackBase(TargetProcess target, IntPtr thread)
    {
        UserModeNative.THREAD_BASIC_INFORMATION info;
        if (UserModeNative.NtQueryInformationThread(thread, 0, &info, (uint)sizeof(UserModeNative.THREAD_BASIC_INFORMATION), out _) != 0 || info.TebBaseAddress == IntPtr.Zero)
        {
            return 0;
        }

        // NT_TIB.StackBase, the second field of the TEB.
        Span<byte> buffer = stackalloc byte[8];
        return target.CodeMap.TryRead((ulong)info.TebBaseAddress + 8, buffer) ? BitConverter.ToUInt64(buffer) : 0;
    }

    private static string GetThreadName(IntPtr thread)
    {
        if (UserModeNative.GetThreadDescription(thread, out var description) < 0 || description == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(description) ?? string.Empty;
        }
        finally
        {
            UserModeNative.LocalFree(description);
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (var target in _targets)
        {
            foreach (var thread in target.Threads.Values)
            {
                UserModeNative.CloseHandle(thread.Handle);
            }

            UserModeNative.CloseHandle(target.Handle);
        }

        _targets.Clear();
        _writer.Dispose();
        NativeMemory.AlignedFree(_context);
    }
}

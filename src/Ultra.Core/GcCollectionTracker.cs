// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// Pure GC-bookkeeping logic shared by trace converters: pairs the GCStart/GCEnd events of one process
/// across all of its threads, and derives the concurrent phase of a background collection from the
/// stop-the-world pauses surrounding it.
/// </summary>
/// <typeparam name="TThread">The converter's per-thread state, so a completed collection can be reported on the thread that started it.</typeparam>
/// <typeparam name="TPayload">The converter's marker payload, carried unchanged from <see cref="OnGcStart"/> to the completed collection.</typeparam>
/// <remarks>
/// <para>
/// GCStart and GCEnd are not guaranteed to be raised on the same thread. Mono's SGen collector starts a
/// concurrent major collection on whichever mutator thread's allocation triggered it and finishes it, one
/// concurrent phase later, on whichever thread happens to trigger the next collection; CoreCLR raises a
/// background GC's GCEnd from its dedicated background GC thread. Pairing per thread silently loses every
/// one of those collections, so this pairs per process, keyed by the collection's (count, depth): CoreCLR's
/// count is a process-wide GC index, and Mono's profiler counts per generation and never overlaps two
/// collections of the same generation.
/// </para>
/// <para>
/// A collection has a concurrent phase when the world is restarted between its GCStart and its GCEnd: the
/// collector then runs alongside the mutator until the final pause. The phase runs from the GCRestartEEStop
/// closing the pause the collection started in, to the GCSuspendEEStart of the pause its GCEnd is raised
/// in (or, for a GCEnd raised after the world was already restarted - CoreCLR raises a background GC's
/// GCEnd once its concurrent sweep is done - the most recent pause). Nested collections that start and end
/// inside a single pause, such as a nursery collection during the concurrent phase, have no concurrent
/// phase. This is structural on purpose: the GCStart type field cannot be trusted to tell a concurrent
/// collection apart, and the pauses are reported by every runtime.
/// </para>
/// <para>
/// This has no dependency on <c>TraceLog</c>/<c>EventPipeEventSource</c> or any other trace-reading type -
/// on purpose, so it can be unit tested directly against the event sequences each runtime produces. Events
/// must be fed in timestamp order across all threads of the process.
/// </para>
/// </remarks>
internal sealed class GcCollectionTracker<TThread, TPayload> where TThread : class
{
    private readonly Dictionary<(int Count, int Depth), PendingCollection> _pending = new();

    /// <summary>
    /// The GCSuspendEEStart time of the pause the world is currently stopped in, or <c>null</c> while it runs.
    /// </summary>
    private double? _currentPauseStartTimeInMs;

    /// <summary>
    /// The GCSuspendEEStart time of the most recent pause, whether or not the world has been restarted since;
    /// <see cref="double.NaN"/> before the first pause.
    /// </summary>
    private double _lastPauseStartTimeInMs = double.NaN;

    /// <summary>
    /// Records a GCStart. A collection still pending under the same (count, depth) is discarded: the runtime
    /// never overlaps two such collections, so its GCEnd must have been lost.
    /// </summary>
    /// <param name="thread">The thread the GCStart was raised on - the thread the completed collection is reported for.</param>
    /// <param name="count">The collection count reported by the GCStart.</param>
    /// <param name="depth">The generation depth reported by the GCStart.</param>
    /// <param name="timeInMs">The GCStart timestamp, in milliseconds.</param>
    /// <param name="payload">The converter's payload for the collection, handed back with the completed collection.</param>
    public void OnGcStart(TThread thread, int count, int depth, double timeInMs, TPayload payload)
    {
        ArgumentNullException.ThrowIfNull(thread);
        _pending[(count, depth)] = new PendingCollection(thread, payload, timeInMs);
    }

    /// <summary>
    /// Records a GCEnd and completes the pending collection with the same (count, depth), whichever thread
    /// its GCStart was raised on.
    /// </summary>
    /// <param name="count">The collection count reported by the GCEnd.</param>
    /// <param name="depth">The generation depth reported by the GCEnd.</param>
    /// <param name="timeInMs">The GCEnd timestamp, in milliseconds.</param>
    /// <returns>The completed collection, or <c>null</c> when no GCStart is pending for it (e.g. the trace started mid-collection).</returns>
    public GcCollection<TThread, TPayload>? OnGcEnd(int count, int depth, double timeInMs)
    {
        if (!_pending.Remove((count, depth), out var pending))
        {
            return null;
        }

        double? concurrentStartTimeInMs = null;
        double? concurrentEndTimeInMs = null;
        if (pending.ConcurrentStartTimeInMs is { } restartTimeInMs)
        {
            concurrentStartTimeInMs = restartTimeInMs;
            // The final pause: the one the GCEnd is raised in, or else the most recent one - provided it
            // began after the world was restarted (otherwise no pause followed, and the phase ends with the
            // GCEnd itself).
            var finalPauseStartTimeInMs = _currentPauseStartTimeInMs ?? _lastPauseStartTimeInMs;
            concurrentEndTimeInMs = finalPauseStartTimeInMs > restartTimeInMs ? finalPauseStartTimeInMs : timeInMs;
        }

        return new GcCollection<TThread, TPayload>(pending.Thread, pending.Payload, pending.StartTimeInMs, timeInMs, concurrentStartTimeInMs, concurrentEndTimeInMs);
    }

    /// <summary>
    /// Records a GCSuspendEEStart: the world is about to stop.
    /// </summary>
    /// <param name="timeInMs">The GCSuspendEEStart timestamp, in milliseconds.</param>
    public void OnSuspendEEStart(double timeInMs)
    {
        _currentPauseStartTimeInMs = timeInMs;
        _lastPauseStartTimeInMs = timeInMs;
    }

    /// <summary>
    /// Records a GCRestartEEStop: the world runs again. Every collection still pending now has a concurrent
    /// phase starting here, unless one was already recorded for it by an earlier restart.
    /// </summary>
    /// <param name="timeInMs">The GCRestartEEStop timestamp, in milliseconds.</param>
    public void OnRestartEEStop(double timeInMs)
    {
        _currentPauseStartTimeInMs = null;
        foreach (var pending in _pending.Values)
        {
            pending.ConcurrentStartTimeInMs ??= timeInMs;
        }
    }

    private sealed class PendingCollection(TThread thread, TPayload payload, double startTimeInMs)
    {
        public TThread Thread { get; } = thread;
        public TPayload Payload { get; } = payload;
        public double StartTimeInMs { get; } = startTimeInMs;

        /// <summary>The first GCRestartEEStop after the GCStart, once seen: the start of the concurrent phase.</summary>
        public double? ConcurrentStartTimeInMs { get; set; }
    }
}

/// <summary>
/// A garbage collection whose GCStart and GCEnd have both been seen by a <see cref="GcCollectionTracker{TThread,TPayload}"/>.
/// </summary>
/// <typeparam name="TThread">The converter's per-thread state.</typeparam>
/// <typeparam name="TPayload">The converter's marker payload.</typeparam>
/// <param name="Thread">The thread the GCStart was raised on.</param>
/// <param name="Payload">The payload given to <see cref="GcCollectionTracker{TThread,TPayload}.OnGcStart"/>.</param>
/// <param name="StartTimeInMs">The GCStart timestamp, in milliseconds.</param>
/// <param name="EndTimeInMs">The GCEnd timestamp, in milliseconds.</param>
/// <param name="ConcurrentStartTimeInMs">When the collection has a concurrent phase, the GCRestartEEStop that began it; otherwise <c>null</c>.</param>
/// <param name="ConcurrentEndTimeInMs">When the collection has a concurrent phase, the GCSuspendEEStart of its final pause; otherwise <c>null</c>.</param>
internal readonly record struct GcCollection<TThread, TPayload>(
    TThread Thread,
    TPayload Payload,
    double StartTimeInMs,
    double EndTimeInMs,
    double? ConcurrentStartTimeInMs,
    double? ConcurrentEndTimeInMs) where TThread : class
{
    /// <summary>Whether the world was restarted between the GCStart and the GCEnd, i.e. the collector ran concurrently with the mutator.</summary>
    public bool HasConcurrentPhase => ConcurrentStartTimeInMs is not null;
}

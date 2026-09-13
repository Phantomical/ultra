// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// Pure thread-bookkeeping logic shared by trace converters: given each of a process's threads' CPU
/// time, decides the CPU-descending processing order, the profile-thread index each thread ends up
/// with, and the profile's default visible/selected threads for that process.
/// </summary>
/// <remarks>
/// This has no dependency on <c>TraceLog</c>/<c>EventPipeEventSource</c> or any other trace-reading type -
/// on purpose, so it can be unit tested directly. Thread order, index assignment, and default-visibility
/// fallback are exactly the kind of decisions a refactor can silently get wrong without any test noticing,
/// since the only symptom is a reordered/reindexed array deep inside the serialized profile JSON.
/// </remarks>
internal static class ThreadVisibilityPlanner
{
    /// <summary>
    /// Computes the CPU-descending processing order for a process's threads, the resulting profile-thread
    /// index for each, and the profile's default visible/selected threads.
    /// </summary>
    /// <param name="cpuTimesInMs">Each thread's CPU time in milliseconds, in the caller's original (pre-sort) order.</param>
    /// <param name="baseProfileThreadIndex">
    /// The profile-thread index the first (highest-CPU) thread will receive; subsequent threads receive
    /// consecutive indices in CPU-descending order. This is non-zero for every process after the first one
    /// in a multi-process conversion.
    /// </param>
    /// <param name="minimumCpuTimeBeforeThreadIsVisibleInMs">
    /// The minimum CPU time (in milliseconds) a thread must have consumed to be visible by default.
    /// </param>
    /// <returns>The computed <see cref="ThreadVisibilityPlan"/>.</returns>
    public static ThreadVisibilityPlan Plan(IReadOnlyList<double> cpuTimesInMs, int baseProfileThreadIndex, double minimumCpuTimeBeforeThreadIsVisibleInMs)
    {
        ArgumentNullException.ThrowIfNull(cpuTimesInMs);

        var threadCount = cpuTimesInMs.Count;

        // Sort the (original, pre-sort) thread indices by descending CPU time. This is the same
        // comparator used by every converter's own thread sort - just applied to plain indices instead of
        // TraceThread/UTraceThread instances, so it has no dependency on either trace type.
        var sortOrder = new int[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            sortOrder[i] = i;
        }
        Array.Sort(sortOrder, (a, b) => cpuTimesInMs[b].CompareTo(cpuTimesInMs[a]));

        var profileThreadIndexByOriginalIndex = new int[threadCount];
        var initialVisibleThreads = new List<int>();
        for (var position = 0; position < threadCount; position++)
        {
            var originalIndex = sortOrder[position];
            var profileThreadIndex = baseProfileThreadIndex + position;
            profileThreadIndexByOriginalIndex[originalIndex] = profileThreadIndex;

            // Make visible threads in the UI that consume a minimum amount of CPU time.
            if (cpuTimesInMs[originalIndex] > minimumCpuTimeBeforeThreadIsVisibleInMs)
            {
                initialVisibleThreads.Add(profileThreadIndex);
            }
        }

        // Threads are sorted CPU-descending above, so position 0 always has the maximum CPU time: its
        // profile-thread index is therefore already the final "thread with max CPU" answer for this
        // process, without needing a separate per-thread comparison.
        var threadIndexWithMaxCpuTime = threadCount > 0 ? baseProfileThreadIndex : -1;

        var initialSelectedThreads = new List<int>();
        if (threadCount > 0)
        {
            // Always make at least the top-CPU thread visible, even if it is below the visibility
            // threshold (e.g. every thread in the process is below it).
            if (!initialVisibleThreads.Contains(threadIndexWithMaxCpuTime))
            {
                initialVisibleThreads.Add(threadIndexWithMaxCpuTime);
            }

            initialSelectedThreads.Add(threadIndexWithMaxCpuTime);
        }

        return new ThreadVisibilityPlan(sortOrder, profileThreadIndexByOriginalIndex, initialVisibleThreads, initialSelectedThreads, threadIndexWithMaxCpuTime);
    }
}

/// <summary>
/// The result of <see cref="ThreadVisibilityPlanner.Plan"/>.
/// </summary>
/// <param name="SortOrder">
/// For each position (0-based) in the CPU-descending order, the original (pre-sort) index of the thread
/// that belongs there.
/// </param>
/// <param name="ProfileThreadIndexByOriginalIndex">
/// For each original (pre-sort) thread index, its assigned profile-thread index.
/// </param>
/// <param name="InitialVisibleThreads">
/// The profile-thread indices to mark visible by default, in the order they should be appended to the
/// profile's initial-visible-threads list (CPU-descending, with the top-CPU thread appended last if it was
/// below the visibility threshold).
/// </param>
/// <param name="InitialSelectedThreads">
/// The profile-thread indices to select by default (empty when there are no threads, otherwise exactly the
/// top-CPU thread).
/// </param>
/// <param name="ThreadIndexWithMaxCpuTime">The profile-thread index of the thread with the maximum CPU time, or -1 if there are no threads.</param>
internal readonly record struct ThreadVisibilityPlan(
    int[] SortOrder,
    int[] ProfileThreadIndexByOriginalIndex,
    List<int> InitialVisibleThreads,
    List<int> InitialSelectedThreads,
    int ThreadIndexWithMaxCpuTime);

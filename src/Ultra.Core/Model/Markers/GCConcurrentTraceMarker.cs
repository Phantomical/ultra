// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents the concurrent phase of a background GC: the span between the first stop-the-world pause
/// resuming the runtime and the second, final stop-the-world pause beginning, during which the collector
/// runs alongside the mutator instead of blocking it.
/// </summary>
public sealed record GCConcurrentTraceMarker : UTraceMarker;

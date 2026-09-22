// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a JIT compile event marker payload for the Firefox Profiler.
/// </summary>
public sealed record JitCompileTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the full name of the method.
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// Gets or sets the namespace of the method.
    /// </summary>
    public required string MethodNamespace { get; init; }

    /// <summary>
    /// Gets or sets the name of the method.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Gets or sets the signature of the method.
    /// </summary>
    public required string MethodSignature { get; init; }

    /// <summary>
    /// Gets or sets the IL size of the method.
    /// </summary>
    public required int MethodILSize { get; set; }

    /// <summary>
    /// Gets or sets the runtime's id of the module (assembly) that defines the method, as reported on the
    /// MethodJittingStarted event; <c>0</c> when unknown. Resolved to the module's file name at conversion
    /// time, once every module load event of the trace has been processed.
    /// </summary>
    public long ModuleID { get; init; }

    /// <summary>
    /// Gets or sets the optimization tier the method was compiled at. The runtime only reports it on the
    /// MethodLoadVerbose event that ends the compilation, so it is <see cref="OptimizationTier.Unknown"/>
    /// until then, and stays so for runtimes that do not report a tier.
    /// </summary>
    public OptimizationTier OptimizationTier { get; set; }
}
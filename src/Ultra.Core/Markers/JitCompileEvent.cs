// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ultra.Core.Markers;

/// <summary>
/// Represents a JIT compile event marker payload for the Firefox Profiler.
/// </summary>
public class JitCompileEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for the JIT compile event.
    /// </summary>
    public const string TypeId = "CC"; // Use CC instead of dotnet.jit.compile because Firefox Profiler is hardcoding styles based on names :( See https://github.com/firefox-devtools/profiler/blob/main/src/profile-logic/marker-styles.js

    /// <summary>
    /// Initializes a new instance of the <see cref="JitCompileEvent"/> class.
    /// </summary>
    public JitCompileEvent()
    {
        Type = TypeId;
        FullName = string.Empty;
    }

    /// <summary>
    /// Gets or sets the full name of the method.
    /// </summary>
    public string FullName { get; set; }

    /// <summary>
    /// Gets or sets the IL size of the method.
    /// </summary>
    public int MethodILSize { get; set; }

    /// <summary>
    /// Gets or sets the optimization tier the method was compiled at, as reported by the runtime on the
    /// MethodLoadVerbose event (<see cref="OptimizationTier.Unknown"/> for runtimes that do not report one).
    /// </summary>
    public OptimizationTier OptimizationTier { get; set; }

    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteString("fullName", FullName);
        writer.WriteNumber("methodILSize", MethodILSize);
        // Both the runtime's numeric tier id (stable for external analyzers) and its name (for humans).
        writer.WriteNumber("optimizationTier", (int)OptimizationTier);
        writer.WriteString("optimizationTierName", GetOptimizationTierName(OptimizationTier));
    }

    /// <summary>
    /// Names an optimization tier in the profile. Spelled out rather than <c>Enum.ToString()</c> so the
    /// names written to trace files are pinned here and cannot drift with TraceEvent's enum member names;
    /// a tier this version does not know is written as its numeric id.
    /// </summary>
    private static string GetOptimizationTierName(OptimizationTier tier)
        => tier switch
        {
            OptimizationTier.Unknown => "Unknown",
            OptimizationTier.MinOptJitted => "MinOptJitted",
            OptimizationTier.Optimized => "Optimized",
            OptimizationTier.QuickJitted => "QuickJitted",
            OptimizationTier.OptimizedTier1 => "OptimizedTier1",
            OptimizationTier.OptimizedTier1OSR => "OptimizedTier1OSR",
            OptimizationTier.QuickJittedInstrumented => "QuickJittedInstrumented",
            OptimizationTier.OptimizedTier1Instrumented => "OptimizedTier1Instrumented",
            OptimizationTier.ReadyToRun => "ReadyToRun",
            OptimizationTier.PreJIT => "PreJIT",
            _ => ((int)tier).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Gets the schema for the JIT compile event marker.
    /// </summary>
    /// <returns>The marker schema.</returns>
    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,

            ChartLabel = "JIT Compile: {marker.data.fullName}, ILSize: {marker.data.methodILSize}",
            TableLabel = "JIT Compile: {marker.data.fullName}, ILSize: {marker.data.methodILSize}",

            Display =
            {
                    FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
                    FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                    FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            },

            Data =
            {
                    new FirefoxProfiler.MarkerDataItem()
                    {
                        Format = FirefoxProfiler.MarkerFormatType.String,
                        Key = "fullName",
                        Label = "Full Name",
                    },
                    new FirefoxProfiler.MarkerDataItem()
                    {
                        Format = FirefoxProfiler.MarkerFormatType.Integer,
                        Key = "methodILSize",
                        Label = "Method IL Size",
                    },
                    new FirefoxProfiler.MarkerDataItem()
                    {
                        Format = FirefoxProfiler.MarkerFormatType.Integer,
                        Key = "optimizationTier",
                        Label = "Optimization Tier Id",
                    },
                    new FirefoxProfiler.MarkerDataItem()
                    {
                        Format = FirefoxProfiler.MarkerFormatType.String,
                        Key = "optimizationTierName",
                        Label = "Optimization Tier",
                    },
            },
        };
}

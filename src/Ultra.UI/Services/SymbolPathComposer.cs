// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.UI.Services;

/// <summary>
/// Builds the symbol path passed to the profiler (<see cref="UltraProfilerOptions.SymbolPathText"/>) from the symbol
/// path settings.
/// </summary>
internal static class SymbolPathComposer
{
    /// <summary>
    /// Builds the symbol path.
    /// </summary>
    /// <param name="entries">The custom symbol path entries, in search order.</param>
    /// <param name="includeDefault">Whether the default symbol path is searched after the custom entries.</param>
    /// <returns>The symbol path, or <c>null</c> to let the profiler use its default symbol path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <c>null</c>.</exception>
    public static string? Compose(IEnumerable<SymbolPathEntrySetting> entries, bool includeDefault)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var parts = new List<string>();
        foreach (var entry in entries)
        {
            if (!entry.Enabled)
            {
                continue;
            }

            // An entry can itself be a ;-separated list (e.g. pasted from _NT_SYMBOL_PATH).
            AddParts(parts, entry.Path);
        }

        if (parts.Count == 0)
        {
            // Nothing custom: the profiler default. Without the default either, there is nothing to search but the cache.
            return includeDefault ? null : "";
        }

        if (includeDefault)
        {
            AddParts(parts, UltraProfilerOptions.GetDefaultSymbolPathText());
        }

        return string.Join(';', parts);
    }

    /// <summary>
    /// Gets the default symbol path, normalized (without empty elements).
    /// </summary>
    /// <returns>The default symbol path.</returns>
    public static string GetDefaultSymbolPath()
    {
        var parts = new List<string>();
        AddParts(parts, UltraProfilerOptions.GetDefaultSymbolPathText());
        return string.Join(';', parts);
    }

    private static void AddParts(List<string> parts, string symbolPath)
    {
        foreach (var part in symbolPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!parts.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add(part);
            }
        }
    }
}

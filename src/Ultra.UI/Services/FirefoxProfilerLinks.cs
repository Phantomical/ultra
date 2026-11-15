// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.UI.Services;

/// <summary>
/// Builds links that open a profile in the Firefox Profiler web application.
/// </summary>
internal static class FirefoxProfilerLinks
{
    /// <summary>
    /// The Firefox Profiler web application.
    /// </summary>
    public const string FirefoxProfilerBaseUrl = "https://profiler.firefox.com";

    /// <summary>
    /// Builds a link that makes the Firefox Profiler download the profile at <paramref name="profileUrl"/>.
    /// </summary>
    /// <param name="profileUrl">Where the browser can download the profile from (it must allow cross-origin requests).</param>
    /// <param name="profileName">An optional name shown by the Firefox Profiler for the profile.</param>
    /// <returns>The link to open in a browser.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileUrl"/> is <c>null</c>.</exception>
    public static Uri BuildFromUrl(Uri profileUrl, string? profileName = null)
    {
        ArgumentNullException.ThrowIfNull(profileUrl);

        var link = $"{FirefoxProfilerBaseUrl}/from-url/{Uri.EscapeDataString(profileUrl.AbsoluteUri)}/";
        if (!string.IsNullOrEmpty(profileName))
        {
            link += $"?profileName={Uri.EscapeDataString(profileName)}";
        }

        return new Uri(link);
    }
}

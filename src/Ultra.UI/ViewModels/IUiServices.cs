// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.UI.ViewModels;

/// <summary>
/// What the view models need from the window: pickers, clipboard, shell and dialogs.
/// </summary>
internal interface IUiServices
{
    /// <summary>Lets the user pick a file.</summary>
    /// <param name="title">The title of the picker.</param>
    /// <param name="filterName">The name of the file type filter, or <c>null</c> for all files.</param>
    /// <param name="patterns">The patterns of the filter, e.g. <c>*.exe</c>.</param>
    /// <returns>The picked file, or <c>null</c>.</returns>
    Task<string?> PickFileAsync(string title, string? filterName, IReadOnlyList<string> patterns);

    /// <summary>Lets the user pick a folder.</summary>
    /// <param name="title">The title of the picker.</param>
    /// <returns>The picked folder, or <c>null</c>.</returns>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Copies text to the clipboard.</summary>
    /// <param name="text">The text.</param>
    Task CopyTextAsync(string text);

    /// <summary>Opens a link in the default browser.</summary>
    /// <param name="uri">The link.</param>
    void OpenUrl(Uri uri);

    /// <summary>Shows a file in the file manager.</summary>
    /// <param name="path">The file.</param>
    void RevealFile(string path);

    /// <summary>Shows the settings dialog.</summary>
    /// <param name="settings">The settings to edit.</param>
    /// <returns><c>true</c> if the user accepted the changes.</returns>
    Task<bool> ShowSettingsAsync(SettingsViewModel settings);
}

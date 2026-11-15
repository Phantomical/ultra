// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ultra.UI.ViewModels;

namespace Ultra.UI.Views;

/// <summary>
/// The main window of the ultra UI.
/// </summary>
internal sealed partial class MainWindow : Window, IUiServices
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // Drags start from the handle of a capture; handledEventsToo because the list box handles the press to select.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } viewModel)
        {
            viewModel.OutputFlushed += ScrollOutputToEnd;
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ViewModel?.Shutdown();
        base.OnClosing(e);
    }

    /// <inheritdoc />
    public async Task<string?> PickFileAsync(string title, string? filterName, IReadOnlyList<string> patterns)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (filterName is not null && patterns.Count > 0)
        {
            options.FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All];
        }

        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
        }
    }

    /// <inheritdoc />
    public void OpenUrl(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.IsFile ? uri.LocalPath : uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    /// <inheritdoc />
    public void RevealFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add($"/select,{path}");
            Process.Start(startInfo)?.Dispose();
        }
        else if (OperatingSystem.IsMacOS())
        {
            var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo)?.Dispose();
        }
        else if (Path.GetDirectoryName(path) is { } directory)
        {
            OpenUrl(new Uri(directory));
        }
    }

    /// <inheritdoc />
    public async Task<bool> ShowSettingsAsync(SettingsViewModel settings)
    {
        var window = new SettingsWindow { DataContext = settings };
        return await window.ShowDialog<bool>(this);
    }

    /// <inheritdoc />
    public async Task<bool> PickProcessesAsync(ProcessPickerViewModel picker)
    {
        var window = new ProcessPickerWindow { DataContext = picker };
        return await window.ShowDialog<bool>(this);
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var handle = source.FindAncestorOfType<Border>(includeSelf: true);
        if (handle is null || !handle.Classes.Contains("dragHandle") || handle.DataContext is not CaptureViewModel { IsCompleted: true, FilePath: { } path })
        {
            return;
        }

        try
        {
            var file = await StorageProvider.TryGetFileFromPathAsync(path);
            if (file is null)
            {
                return;
            }

            var dataTransfer = new DataTransfer();
            dataTransfer.Add(DataTransferItem.CreateFile(file));
            await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The file was moved or deleted: nothing to drag.
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || ViewModel is not { } viewModel)
        {
            return;
        }

        var paths = files.Select(x => x.TryGetLocalPath()).OfType<string>().ToList();
        viewModel.HandleDroppedFiles(paths);
    }

    private void ScrollOutputToEnd()
    {
        if (ViewModel is not { AutoScrollOutput: true } viewModel || viewModel.OutputLines.Count == 0)
        {
            return;
        }

        var last = viewModel.OutputLines[^1];
        Dispatcher.UIThread.Post(() => OutputList.ScrollIntoView(last), DispatcherPriority.Background);
    }
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using ByteSizeLib;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ultra.UI.ViewModels;

/// <summary>
/// The phase of a capture.
/// </summary>
internal enum CapturePhase
{
    /// <summary>The capture is profiling.</summary>
    Profiling,

    /// <summary>The capture stopped profiling and is being saved.</summary>
    Saving,

    /// <summary>The profile is saved.</summary>
    Completed,

    /// <summary>The capture could not be saved.</summary>
    Failed,
}

/// <summary>
/// A capture of a profiling session (or a profile opened from disk), shown in the captures list.
/// </summary>
internal sealed partial class CaptureViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptureViewModel"/> class.
    /// </summary>
    /// <param name="owner">The main view model, which opens and shares the profile.</param>
    /// <param name="title">The title of the capture.</param>
    /// <param name="programName">The profiled program.</param>
    public CaptureViewModel(MainWindowViewModel owner, string title, string programName)
    {
        _owner = owner;
        Title = title;
        ProgramName = programName;
        StartedAt = DateTime.Now;
    }

    /// <summary>Gets the title of the capture, e.g. <c>Session 1 · Capture 2</c>.</summary>
    public string Title { get; }

    /// <summary>Gets the profiled program.</summary>
    public string ProgramName { get; }

    /// <summary>Gets when the capture started.</summary>
    public DateTime StartedAt { get; }

    /// <summary>Gets the start time, for display.</summary>
    public string StartedAtText => StartedAt.ToString("HH:mm:ss");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseText), nameof(IsProfiling), nameof(IsSaving), nameof(IsCompleted), nameof(IsFailed), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenInFirefoxProfilerCommand), nameof(CopyFirefoxProfilerLinkCommand), nameof(CopyPathCommand), nameof(ShowInFolderCommand), nameof(RemoveCommand))]
    private CapturePhase _phase;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    private string? _filePath;

    [ObservableProperty]
    private string _sizeText = "";

    /// <summary>Gets the file name of the profile.</summary>
    public string FileName => FilePath is null ? "" : Path.GetFileName(FilePath);

    /// <summary>Gets the phase, for display.</summary>
    public string PhaseText => Phase switch
    {
        CapturePhase.Profiling => "Profiling",
        CapturePhase.Saving => "Saving",
        CapturePhase.Completed => "Ready",
        CapturePhase.Failed => "Failed",
        _ => "",
    };

    /// <summary>Gets a value indicating whether the capture is profiling.</summary>
    public bool IsProfiling => Phase == CapturePhase.Profiling;

    /// <summary>Gets a value indicating whether the capture is being saved.</summary>
    public bool IsSaving => Phase == CapturePhase.Saving;

    /// <summary>Gets a value indicating whether the profile is saved.</summary>
    public bool IsCompleted => Phase == CapturePhase.Completed;

    /// <summary>Gets a value indicating whether the capture failed.</summary>
    public bool IsFailed => Phase == CapturePhase.Failed;

    /// <summary>Gets a value indicating whether the capture is still profiling or saving.</summary>
    public bool IsBusy => Phase is CapturePhase.Profiling or CapturePhase.Saving;

    /// <summary>
    /// Marks the capture as saved to <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The profile file.</param>
    public void Complete(string path)
    {
        FilePath = path;
        try
        {
            SizeText = ByteSize.FromBytes(new FileInfo(path).Length).ToString("0.0");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SizeText = "";
        }

        StatusText = "Saved";
        Phase = CapturePhase.Completed;
    }

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private void OpenInFirefoxProfiler() => _owner.OpenInFirefoxProfiler(this);

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private Task CopyFirefoxProfilerLinkAsync() => _owner.CopyFirefoxProfilerLinkAsync(this);

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private Task CopyPathAsync() => _owner.CopyTextAsync(FilePath!);

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private void ShowInFolder() => _owner.RevealFile(FilePath!);

    private bool CanRemove() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => _owner.RemoveCapture(this);
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ultra.Core;
using Ultra.UI.Services;
using Ultra.UI.Worker;

namespace Ultra.UI.ViewModels;

/// <summary>
/// The state of the profiling session of the UI.
/// </summary>
internal enum SessionState
{
    /// <summary>No session.</summary>
    Idle,

    /// <summary>The worker is starting (possibly waiting for the user to accept the elevation prompt).</summary>
    Starting,

    /// <summary>The session is ready, no capture is profiling.</summary>
    Waiting,

    /// <summary>A capture is profiling.</summary>
    Profiling,

    /// <summary>The session was asked to end and is saving its last capture.</summary>
    Ending,
}

/// <summary>
/// An environment variable of the program to launch.
/// </summary>
internal sealed partial class EnvironmentVariableViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private bool _enabled = true;
}

/// <summary>
/// A line of the output panel.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="IsError">Whether it was written to stderr, or is an error of ultra.</param>
/// <param name="IsUltra">Whether it is a message of ultra rather than of the profiled program.</param>
internal sealed record OutputLine(string Text, bool IsError, bool IsUltra);

/// <summary>
/// The main window: what to profile, the current session, its captures and the output of the profiled program.
/// </summary>
internal sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int MaxOutputLines = 5000;
    private const int MaxRecentPrograms = 10;

    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly ProfileServer _server;
    private readonly ConcurrentQueue<OutputLine> _pendingOutput = new();
    private readonly DispatcherTimer _outputTimer;
    private readonly Dictionary<int, CaptureViewModel> _sessionCaptures = new();
    private IUiServices? _ui;
    private WorkerClient? _client;
    private CancellationTokenSource? _startCts;
    private int _sessionNumber;
    private bool _sessionEndReported;
    private string _sessionProgramName = "";

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    /// <param name="store">Where the settings are saved.</param>
    /// <param name="settings">The loaded settings.</param>
    /// <param name="server">The server that makes the profiles available to the Firefox Profiler.</param>
    public MainWindowViewModel(SettingsStore store, AppSettings settings, ProfileServer server)
    {
        _store = store;
        _settings = settings;
        _server = server;

        var launch = settings.Launch;
        _attachToProcess = launch.AttachToProcess;
        _programPath = launch.ProgramPath;
        _arguments = launch.Arguments;
        _workingDirectory = launch.WorkingDirectory;
        _processId = launch.ProcessId;
        _runAsAdministrator = launch.RunAsAdministrator;
        _userModeSampling = launch.UserModeSampling;
        _samplingInterval = launch.SamplingInterval;
        _maxCaptureDuration = launch.MaxCaptureDuration;
        _captureProgramOutput = launch.CaptureProgramOutput;
        _startCaptureImmediately = launch.StartCaptureImmediately;
        EnvironmentVariables = new ObservableCollection<EnvironmentVariableViewModel>(
            launch.EnvironmentVariables.Select(x => new EnvironmentVariableViewModel { Name = x.Name, Value = x.Value, Enabled = x.Enabled }));
        RecentPrograms = new ObservableCollection<string>(settings.RecentPrograms);

        IsWindows = OperatingSystem.IsWindows();
        IsAlreadyElevated = IsWindows && UltraProfiler.IsElevated();
        _sessionStatus = "Choose a program to profile, then start a session.";

        _outputTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => FlushOutput());
        _outputTimer.Start();
    }

    /// <summary>Gets the environment variables of the program to launch.</summary>
    public ObservableCollection<EnvironmentVariableViewModel> EnvironmentVariables { get; }

    /// <summary>Gets the most recently launched programs.</summary>
    public ObservableCollection<string> RecentPrograms { get; }

    /// <summary>Gets the captures of all the sessions, most recent first.</summary>
    public ObservableCollection<CaptureViewModel> Captures { get; } = new();

    /// <summary>Gets the output of the profiled program and the messages of ultra.</summary>
    public ObservableCollection<OutputLine> OutputLines { get; } = new();

    /// <summary>Gets a value indicating whether the UI runs on Windows (where administrator rights matter).</summary>
    public bool IsWindows { get; }

    /// <summary>Gets a value indicating whether the UI itself already runs as administrator.</summary>
    public bool IsAlreadyElevated { get; }

    /// <summary>Gets a value indicating whether the "run as administrator" option can be changed.</summary>
    public bool CanChooseElevation => IsWindows && !IsAlreadyElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLaunchMode))]
    private bool _attachToProcess;

    /// <summary>Gets or sets a value indicating whether a program is launched (rather than a process attached to).</summary>
    public bool IsLaunchMode
    {
        get => !AttachToProcess;
        set => AttachToProcess = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkingDirectoryPlaceholder))]
    private string _programPath;

    [ObservableProperty]
    private string _arguments;

    [ObservableProperty]
    private string _workingDirectory;

    [ObservableProperty]
    private string _processId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SamplingModeHint))]
    private bool _runAsAdministrator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SamplingModeHint))]
    private bool _userModeSampling;

    [ObservableProperty]
    private string _samplingInterval;

    [ObservableProperty]
    private string _maxCaptureDuration;

    [ObservableProperty]
    private bool _captureProgramOutput;

    [ObservableProperty]
    private bool _startCaptureImmediately;

    [ObservableProperty]
    private bool _autoScrollOutput = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsSessionActive), nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand), nameof(StartCaptureCommand), nameof(StopCaptureCommand), nameof(EndSessionCommand), nameof(AbortSessionCommand))]
    private SessionState _state;

    [ObservableProperty]
    private string _sessionStatus;

    [ObservableProperty]
    private string _sessionModeText = "";

    /// <summary>Gets a value indicating whether there is no session.</summary>
    public bool IsIdle => State == SessionState.Idle;

    /// <summary>Gets a value indicating whether there is a session.</summary>
    public bool IsSessionActive => State != SessionState.Idle;

    /// <summary>Gets the state, for display.</summary>
    public string StateText => State switch
    {
        SessionState.Idle => "No session",
        SessionState.Starting => "Starting",
        SessionState.Waiting => "Ready",
        SessionState.Profiling => "Profiling",
        SessionState.Ending => "Ending",
        _ => "",
    };

    /// <summary>Gets the working directory used when none is given: the folder of the program.</summary>
    public string WorkingDirectoryPlaceholder
    {
        get
        {
            try
            {
                var directory = string.IsNullOrWhiteSpace(ProgramPath) ? null : Path.GetDirectoryName(ProgramPath.Trim().Trim('"'));
                return string.IsNullOrEmpty(directory) ? "Folder of the program" : directory;
            }
            catch (ArgumentException)
            {
                return "Folder of the program";
            }
        }
    }

    /// <summary>Gets a hint about how the next session samples.</summary>
    public string SamplingModeHint
    {
        get
        {
            if (!IsWindows)
            {
                return "";
            }

            bool elevated = IsAlreadyElevated || RunAsAdministrator;
            return elevated && !UserModeSampling
                ? "Kernel ETW sampling: kernel and native stacks, context switches, 8190 Hz by default."
                : "User-mode sampling: no administrator rights needed, no kernel stacks, 1 kHz by default (Windows x64 only).";
        }
    }

    /// <summary>
    /// Connects the view model to its window.
    /// </summary>
    /// <param name="ui">The window services.</param>
    public void Attach(IUiServices ui) => _ui = ui;

    /// <summary>
    /// Saves the settings and ends the current session, saving the capture that is profiling (the worker carries on
    /// saving it after the UI is gone).
    /// </summary>
    public void Shutdown()
    {
        StoreLaunchSettings();
        SaveSettings();
        if (_client is not null && State is SessionState.Waiting or SessionState.Profiling)
        {
            _client.Send(WorkerMessageTypes.Quit);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _outputTimer.Stop();
        _startCts?.Cancel();
        _client?.Dispose();
    }

    /// <summary>
    /// Opens a profile in the Firefox Profiler, served from this machine.
    /// </summary>
    /// <param name="capture">The capture.</param>
    public void OpenInFirefoxProfiler(CaptureViewModel capture)
    {
        if (capture.FilePath is null || _ui is null) return;
        try
        {
            _ui.OpenUrl(GetFirefoxProfilerLink(capture));
        }
        catch (Exception ex)
        {
            AppendUltraLine($"Unable to open the Firefox Profiler: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Copies the Firefox Profiler link of a profile to the clipboard.
    /// </summary>
    /// <param name="capture">The capture.</param>
    public async Task CopyFirefoxProfilerLinkAsync(CaptureViewModel capture)
    {
        if (capture.FilePath is null) return;
        await CopyTextAsync(GetFirefoxProfilerLink(capture).AbsoluteUri);
        SessionStatus = "Copied the Firefox Profiler link. It works while the ultra UI is running.";
    }

    /// <summary>
    /// Copies text to the clipboard.
    /// </summary>
    /// <param name="text">The text.</param>
    public async Task CopyTextAsync(string text)
    {
        if (_ui is not null)
        {
            await _ui.CopyTextAsync(text);
        }
    }

    /// <summary>
    /// Shows a file in the file manager.
    /// </summary>
    /// <param name="path">The file.</param>
    public void RevealFile(string path) => _ui?.RevealFile(path);

    /// <summary>
    /// Removes a capture from the list (the profile file is kept).
    /// </summary>
    /// <param name="capture">The capture.</param>
    public void RemoveCapture(CaptureViewModel capture)
    {
        Captures.Remove(capture);
        if (capture.FilePath is not null && !Captures.Any(x => x.FilePath == capture.FilePath))
        {
            _server.Unregister(capture.FilePath);
        }
    }

    /// <summary>
    /// Adds a profile from disk to the captures list.
    /// </summary>
    /// <param name="path">The profile (<c>.json.gz</c> or <c>.json</c>).</param>
    /// <returns>The added capture.</returns>
    public CaptureViewModel AddExistingProfile(string path)
    {
        var existing = Captures.FirstOrDefault(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var capture = new CaptureViewModel(this, "Opened profile", Path.GetFileName(path));
        capture.Complete(path);
        capture.StatusText = Path.GetDirectoryName(path) ?? "";
        Captures.Insert(0, capture);
        return capture;
    }

    /// <summary>
    /// Handles files dropped on the window: a program to launch, or profiles to add.
    /// </summary>
    /// <param name="paths">The dropped files.</param>
    public void HandleDroppedFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (path.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                AddExistingProfile(path);
            }
            else if (IsIdle && (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows()))
            {
                AttachToProcess = false;
                ProgramPath = path;
            }
        }
    }

    private Uri GetFirefoxProfilerLink(CaptureViewModel capture)
    {
        var url = _server.Register(capture.FilePath!);
        return FirefoxProfilerLinks.BuildFromUrl(url, $"{capture.ProgramName} - {capture.Title}");
    }

    private bool CanStartSession() => State == SessionState.Idle;

    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private async Task StartSessionAsync()
    {
        WorkerSessionConfig config;
        try
        {
            config = BuildSessionConfig();
        }
        catch (ArgumentException ex)
        {
            SessionStatus = ex.Message;
            return;
        }

        StoreLaunchSettings();
        if (!AttachToProcess)
        {
            AddRecentProgram(config.ProgramPath!);
        }

        SaveSettings();

        bool elevate = IsWindows && RunAsAdministrator && !IsAlreadyElevated;
        _sessionNumber++;
        _sessionCaptures.Clear();
        _sessionEndReported = false;
        _sessionProgramName = config.ProgramPath is not null ? Path.GetFileNameWithoutExtension(config.ProgramPath) : $"pid {string.Join(", ", config.ProcessIds)}";
        SessionModeText = "";
        State = SessionState.Starting;
        SessionStatus = elevate ? "Starting the profiler as administrator: accept the Windows prompt to continue..." : "Starting the profiler...";
        AppendUltraLine($"Session {_sessionNumber}: {(config.ProgramPath is not null ? $"launching {config.ProgramPath} {string.Join(' ', config.Arguments)}" : $"attaching to {_sessionProgramName}")}", isError: false);

        _startCts = new CancellationTokenSource();
        try
        {
            var client = await WorkerClient.StartAsync(config, elevate, _startCts.Token);
            client.MessageReceived += message => Dispatcher.UIThread.Post(() => OnWorkerMessage(client, message));
            client.Disconnected += () => Dispatcher.UIThread.Post(() => OnWorkerDisconnected(client));
            _client = client;
            client.Run();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == WorkerLauncher.ErrorCancelled)
        {
            EndSession("The administrator prompt was declined. Untick \"Run as administrator\" to profile with user-mode sampling instead.", isError: true);
        }
        catch (OperationCanceledException)
        {
            EndSession("Session cancelled.", isError: false);
        }
        catch (Exception ex)
        {
            EndSession($"Unable to start the profiler: {ex.Message}", isError: true);
        }
        finally
        {
            _startCts.Dispose();
            _startCts = null;
        }
    }

    private bool CanStartCapture() => State == SessionState.Waiting;

    [RelayCommand(CanExecute = nameof(CanStartCapture))]
    private void StartCapture()
    {
        if (_client?.Send(WorkerMessageTypes.Start) == true)
        {
            SessionStatus = "Starting a capture...";
        }
    }

    private bool CanStopCapture() => State == SessionState.Profiling;

    [RelayCommand(CanExecute = nameof(CanStopCapture))]
    private void StopCapture()
    {
        if (_client?.Send(WorkerMessageTypes.Stop) == true)
        {
            SessionStatus = "Stopping the capture...";
        }
    }

    private bool CanEndSession() => State is SessionState.Waiting or SessionState.Profiling;

    [RelayCommand(CanExecute = nameof(CanEndSession))]
    private void EndSession()
    {
        if (_client?.Send(WorkerMessageTypes.Quit) == true)
        {
            State = SessionState.Ending;
            SessionStatus = "Ending the session (saving the current capture, if any)...";
        }
    }

    private bool CanAbortSession() => State != SessionState.Idle;

    [RelayCommand(CanExecute = nameof(CanAbortSession))]
    private void AbortSession()
    {
        if (State == SessionState.Starting)
        {
            _startCts?.Cancel();
            return;
        }

        if (_client is null)
        {
            return;
        }

        if (!_client.Send(WorkerMessageTypes.Abort))
        {
            _client.Kill();
        }

        State = SessionState.Ending;
        SessionStatus = "Aborting the session without saving...";
    }

    [RelayCommand]
    private async Task BrowseProgramAsync()
    {
        if (_ui is null) return;
        var file = await _ui.PickFileAsync("Program to profile", OperatingSystem.IsWindows() ? "Programs" : null, OperatingSystem.IsWindows() ? ["*.exe"] : []);
        if (file is not null)
        {
            ProgramPath = file;
        }
    }

    [RelayCommand]
    private async Task SelectProcessAsync()
    {
        if (_ui is null) return;
        var picker = new ProcessPickerViewModel(ProcessId);
        if (await _ui.PickProcessesAsync(picker) && picker.SelectedProcesses.Count > 0)
        {
            ProcessId = picker.SelectedProcessIdText;
        }
    }

    [RelayCommand]
    private async Task BrowseWorkingDirectoryAsync()
    {
        if (_ui is null) return;
        var folder = await _ui.PickFolderAsync("Working directory of the program");
        if (folder is not null)
        {
            WorkingDirectory = folder;
        }
    }

    [RelayCommand]
    private void AddEnvironmentVariable() => EnvironmentVariables.Add(new EnvironmentVariableViewModel());

    [RelayCommand]
    private void RemoveEnvironmentVariable(EnvironmentVariableViewModel? variable)
    {
        if (variable is not null)
        {
            EnvironmentVariables.Remove(variable);
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (_ui is null) return;
        var settings = new SettingsViewModel(_settings, _ui);
        if (await _ui.ShowSettingsAsync(settings))
        {
            settings.ApplyTo(_settings);
            SaveSettings();
            SessionStatus = IsIdle ? "Settings saved." : "Settings saved. They apply to the next session.";
        }
    }

    [RelayCommand]
    private async Task OpenProfileAsync()
    {
        if (_ui is null) return;
        var file = await _ui.PickFileAsync("Open a profile", "Firefox Profiler profiles", ["*.json.gz", "*.json"]);
        if (file is not null)
        {
            OpenInFirefoxProfiler(AddExistingProfile(file));
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var folder = GetOutputDirectory();
        Directory.CreateDirectory(folder);
        _ui?.OpenUrl(new Uri(folder));
    }

    [RelayCommand]
    private void ClearOutput()
    {
        _pendingOutput.Clear();
        OutputLines.Clear();
    }

    [RelayCommand]
    private Task CopyOutputAsync()
    {
        var builder = new StringBuilder();
        foreach (var line in OutputLines)
        {
            builder.AppendLine(line.Text);
        }

        return CopyTextAsync(builder.ToString());
    }

    [RelayCommand]
    private void ClearFinishedCaptures()
    {
        foreach (var capture in Captures.Where(x => !x.IsBusy).ToList())
        {
            RemoveCapture(capture);
        }
    }

    /// <summary>
    /// Builds the session configuration from the launch settings.
    /// </summary>
    /// <exception cref="ArgumentException">A setting is invalid; the message is meant for the user.</exception>
    internal WorkerSessionConfig BuildSessionConfig()
    {
        var config = new WorkerSessionConfig
        {
            OutputDirectory = GetOutputDirectory(),
            SymbolPath = SymbolPathComposer.Compose(_settings.SymbolPaths, _settings.IncludeDefaultSymbolPath),
            UserModeSampling = UserModeSampling,
            CaptureProgramOutput = CaptureProgramOutput && !AttachToProcess,
            StartCaptureImmediately = StartCaptureImmediately,
            SamplingIntervalInMs = ParseOptionalPositive(SamplingInterval, "sampling interval"),
            MaxCaptureDurationInSeconds = ParseOptionalPositive(MaxCaptureDuration, "maximum capture duration"),
        };

        if (AttachToProcess)
        {
            foreach (var part in (ProcessId ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                {
                    throw new ArgumentException($"\"{part}\" is not a process id.");
                }

                config.ProcessIds.Add(pid);
            }

            if (config.ProcessIds.Count == 0)
            {
                throw new ArgumentException("Enter the id of the process to attach to.");
            }

            return config;
        }

        var programPath = (ProgramPath ?? "").Trim().Trim('"');
        if (programPath.Length == 0)
        {
            throw new ArgumentException("Choose the program to profile.");
        }

        if (!File.Exists(programPath))
        {
            throw new ArgumentException($"The program {programPath} does not exist.");
        }

        if (OperatingSystem.IsWindows() && !programPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The program to profile must be an .exe.");
        }

        config.ProgramPath = Path.GetFullPath(programPath);
        config.Arguments = CommandLineSplitter.Split(Arguments);

        var workingDirectory = (WorkingDirectory ?? "").Trim().Trim('"');
        if (workingDirectory.Length == 0)
        {
            workingDirectory = Path.GetDirectoryName(config.ProgramPath)!;
        }
        else if (!Directory.Exists(workingDirectory))
        {
            throw new ArgumentException($"The working directory {workingDirectory} does not exist.");
        }

        config.WorkingDirectory = Path.GetFullPath(workingDirectory);

        foreach (var variable in EnvironmentVariables)
        {
            var name = variable.Name.Trim();
            if (!variable.Enabled || name.Length == 0)
            {
                continue;
            }

            if (name.Contains('='))
            {
                throw new ArgumentException($"The environment variable name \"{name}\" cannot contain '='.");
            }

            config.EnvironmentVariables[name] = variable.Value;
        }

        return config;
    }

    private static float? ParseOptionalPositive(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if ((float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && value > 0)
        {
            return value;
        }

        throw new ArgumentException($"The {what} must be a positive number, or empty for the default.");
    }

    private string GetOutputDirectory() => string.IsNullOrWhiteSpace(_settings.OutputDirectory) ? AppSettings.DefaultOutputDirectory : _settings.OutputDirectory;

    private void OnWorkerMessage(WorkerClient client, WorkerMessage message)
    {
        if (client == _client)
        {
            HandleWorkerMessage(message);
        }
    }

    /// <summary>
    /// Updates the session and its captures from a message of the worker.
    /// </summary>
    /// <param name="message">The message.</param>
    internal void HandleWorkerMessage(WorkerMessage message)
    {
        switch (message.Type)
        {
            case WorkerMessageTypes.Ready:
                if (State == SessionState.Starting)
                {
                    State = SessionState.Waiting;
                }

                SessionModeText = message.UserModeSampling == true ? "User-mode sampling" : "Kernel ETW sampling (administrator)";
                SessionStatus = $"Session {_sessionNumber} is ready: start a capture whenever you want.";
                break;

            case WorkerMessageTypes.CaptureStarted:
            {
                var capture = GetOrAddCapture(message.Capture ?? 0);
                capture.Phase = CapturePhase.Profiling;
                capture.StatusText = "Profiling...";
                if (State != SessionState.Ending)
                {
                    State = SessionState.Profiling;
                }

                SessionStatus = $"{capture.Title} is profiling. Stop it to save it and keep the session for more captures.";
                break;
            }

            case WorkerMessageTypes.CaptureSaving:
            {
                var capture = GetOrAddCapture(message.Capture ?? 0);
                capture.Phase = CapturePhase.Saving;
                capture.StatusText = "Saving...";
                if (State == SessionState.Profiling)
                {
                    State = SessionState.Waiting;
                    SessionStatus = $"{capture.Title} is saving in the background. You can start another capture.";
                }

                break;
            }

            case WorkerMessageTypes.CaptureStatus:
                if (message.Capture is { } index && _sessionCaptures.TryGetValue(index, out var statusCapture))
                {
                    statusCapture.StatusText = message.Text ?? "";
                }
                else if (!string.IsNullOrEmpty(message.Text))
                {
                    // Messages before a capture starts (e.g. the program being launched) belong to the session.
                    AppendUltraLine(message.Text, isError: false);
                }

                break;

            case WorkerMessageTypes.CaptureCompleted:
            {
                var capture = GetOrAddCapture(message.Capture ?? 0);
                if (message.Path is not null)
                {
                    capture.Complete(message.Path);
                    _server.Register(message.Path);
                    AppendUltraLine($"{capture.Title} saved to {message.Path}", isError: false);
                }

                break;
            }

            case WorkerMessageTypes.CaptureFailed:
            {
                var capture = GetOrAddCapture(message.Capture ?? 0);
                capture.Phase = CapturePhase.Failed;
                capture.StatusText = message.Text ?? "Failed";
                AppendUltraLine($"{capture.Title} failed: {message.Text}", isError: true);
                break;
            }

            case WorkerMessageTypes.Output:
                _pendingOutput.Enqueue(new OutputLine(message.Text ?? "", message.IsError == true, IsUltra: false));
                break;

            case WorkerMessageTypes.Log:
                AppendUltraLine(message.Text ?? "", message.IsError == true);
                break;

            case WorkerMessageTypes.SessionEnded:
                _sessionEndReported = true;
                EndSession(message.Text is null ? $"Session {_sessionNumber} ended." : $"Session {_sessionNumber} ended: {message.Text}", message.Text is not null);
                break;
        }
    }

    private void OnWorkerDisconnected(WorkerClient client)
    {
        if (client != _client)
        {
            return;
        }

        if (!_sessionEndReported)
        {
            EndSession("The profiler stopped unexpectedly.", isError: true);
        }
    }

    private void EndSession(string status, bool isError)
    {
        foreach (var capture in _sessionCaptures.Values.Where(x => x.IsBusy))
        {
            capture.Phase = CapturePhase.Failed;
            capture.StatusText = "The session ended before this capture was saved";
        }

        _client?.Dispose();
        _client = null;
        State = SessionState.Idle;
        SessionStatus = status;
        SessionModeText = "";
        AppendUltraLine(status, isError);
    }

    private CaptureViewModel GetOrAddCapture(int index)
    {
        if (!_sessionCaptures.TryGetValue(index, out var capture))
        {
            capture = new CaptureViewModel(this, $"Session {_sessionNumber} · Capture {index}", _sessionProgramName);
            _sessionCaptures[index] = capture;
            Captures.Insert(0, capture);
        }

        return capture;
    }

    private void AppendUltraLine(string text, bool isError) => _pendingOutput.Enqueue(new OutputLine(text, isError, IsUltra: true));

    private void FlushOutput()
    {
        if (_pendingOutput.IsEmpty)
        {
            return;
        }

        var lines = new List<OutputLine>();
        while (_pendingOutput.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        if (lines.Count > MaxOutputLines)
        {
            lines.RemoveRange(0, lines.Count - MaxOutputLines);
        }

        var overflow = OutputLines.Count + lines.Count - MaxOutputLines;
        for (int i = 0; i < overflow; i++)
        {
            OutputLines.RemoveAt(0);
        }

        foreach (var line in lines)
        {
            OutputLines.Add(line);
        }

        OutputFlushed?.Invoke();
    }

    /// <summary>
    /// Raised after new lines were added to <see cref="OutputLines"/>.
    /// </summary>
    public event Action? OutputFlushed;

    private void AddRecentProgram(string path)
    {
        var existing = RecentPrograms.FirstOrDefault(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentPrograms.Remove(existing);
        }

        RecentPrograms.Insert(0, path);
        while (RecentPrograms.Count > MaxRecentPrograms)
        {
            RecentPrograms.RemoveAt(RecentPrograms.Count - 1);
        }

        _settings.RecentPrograms = RecentPrograms.ToList();
    }

    private void StoreLaunchSettings()
    {
        _settings.Launch = new LaunchSettings
        {
            AttachToProcess = AttachToProcess,
            ProgramPath = ProgramPath ?? "",
            Arguments = Arguments ?? "",
            WorkingDirectory = WorkingDirectory ?? "",
            ProcessId = ProcessId ?? "",
            RunAsAdministrator = RunAsAdministrator,
            UserModeSampling = UserModeSampling,
            SamplingInterval = SamplingInterval ?? "",
            MaxCaptureDuration = MaxCaptureDuration ?? "",
            CaptureProgramOutput = CaptureProgramOutput,
            StartCaptureImmediately = StartCaptureImmediately,
            EnvironmentVariables = EnvironmentVariables
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new EnvironmentVariableSetting { Name = x.Name.Trim(), Value = x.Value, Enabled = x.Enabled })
                .ToList(),
        };
    }

    private void SaveSettings()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendUltraLine($"Unable to save the settings to {_store.FilePath}: {ex.Message}", isError: true);
        }
    }
}

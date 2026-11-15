// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ultra.UI;
using Ultra.UI.Services;
using Ultra.UI.ViewModels;
using Ultra.UI.Views;
using Ultra.UI.Worker;

namespace Ultra.Tests.UI;

/// <summary>
/// The Avalonia application used by the UI tests: headless, rendered with Skia so that frames can be captured.
/// </summary>
public static class UiTestAppBuilder
{
    /// <summary>
    /// Configures the application.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Tests the main window of the ultra UI and its view model, headless.
/// </summary>
[TestClass]
public class MainWindowTests
{
    private static HeadlessUnitTestSession? _session;
    private string _tempDirectory = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(UiTestAppBuilder));
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _session?.Dispose();
    }

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ultra-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [TestMethod]
    public void SessionFollowsTheMessagesOfTheWorker() => Run(viewModel =>
    {
        viewModel.State = SessionState.Starting;

        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.Ready, UserModeSampling = true });
        Assert.AreEqual(SessionState.Waiting, viewModel.State);
        Assert.IsTrue(viewModel.StartCaptureCommand.CanExecute(null));
        Assert.IsFalse(viewModel.StopCaptureCommand.CanExecute(null));

        // A status before the capture starts (e.g. the program being launched) is a session message, not a capture.
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureStatus, Capture = 1, Text = "Starting Process" });
        Assert.AreEqual(0, viewModel.Captures.Count);

        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureStarted, Capture = 1 });
        Assert.AreEqual(SessionState.Profiling, viewModel.State);
        Assert.IsTrue(viewModel.StopCaptureCommand.CanExecute(null));
        Assert.IsFalse(viewModel.StartCaptureCommand.CanExecute(null));
        var capture = viewModel.Captures.Single();
        Assert.IsTrue(capture.IsProfiling);

        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureStatus, Capture = 1, Text = "Profiling - 3s" });
        Assert.AreEqual("Profiling - 3s", capture.StatusText);

        // Saving runs in the background: another capture can start.
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureSaving, Capture = 1 });
        Assert.AreEqual(SessionState.Waiting, viewModel.State);
        Assert.IsTrue(capture.IsSaving);
        Assert.IsFalse(capture.RemoveCommand.CanExecute(null));

        var profile = Path.Combine(_tempDirectory, "ultra_app.json.gz");
        File.WriteAllBytes(profile, new byte[2048]);
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureCompleted, Capture = 1, Path = profile });
        Assert.IsTrue(capture.IsCompleted);
        Assert.AreEqual(profile, capture.FilePath);
        Assert.IsTrue(capture.OpenInFirefoxProfilerCommand.CanExecute(null));

        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureStarted, Capture = 2 });
        Assert.AreEqual(2, viewModel.Captures.Count);
        Assert.AreSame(capture, viewModel.Captures[1], "The most recent capture comes first");

        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.SessionEnded });
        Assert.AreEqual(SessionState.Idle, viewModel.State);
        Assert.IsTrue(viewModel.Captures[0].IsFailed, "A capture that was not saved when the session ended failed");
        Assert.IsTrue(capture.IsCompleted);
        Assert.IsTrue(viewModel.StartSessionCommand.CanExecute(null));
    });

    [TestMethod]
    public void SessionConfigurationOfALaunchedProgram() => Run(viewModel =>
    {
        var program = CreateProgram();
        viewModel.ProgramPath = $"\"{program}\"";
        viewModel.Arguments = "-popupwindow \"-logFile C:\\a b.log\"";
        viewModel.SamplingInterval = "0.5";
        viewModel.EnvironmentVariables.Add(new EnvironmentVariableViewModel { Name = " MONO_ENV_OPTIONS ", Value = "--debug" });
        viewModel.EnvironmentVariables.Add(new EnvironmentVariableViewModel { Name = "DISABLED", Value = "x", Enabled = false });
        viewModel.EnvironmentVariables.Add(new EnvironmentVariableViewModel { Name = "", Value = "ignored" });

        var config = viewModel.BuildSessionConfig();

        Assert.AreEqual(program, config.ProgramPath);
        CollectionAssert.AreEqual(new[] { "-popupwindow", "-logFile C:\\a b.log" }, config.Arguments);
        Assert.AreEqual(Path.GetDirectoryName(program), config.WorkingDirectory, "The working directory defaults to the folder of the program");
        Assert.AreEqual(1, config.EnvironmentVariables.Count);
        Assert.AreEqual("--debug", config.EnvironmentVariables["MONO_ENV_OPTIONS"]);
        Assert.AreEqual(0.5f, config.SamplingIntervalInMs);
        Assert.IsNull(config.MaxCaptureDurationInSeconds);
        Assert.IsFalse(string.IsNullOrEmpty(config.OutputDirectory));
    });

    [TestMethod]
    public void InvalidSessionConfigurationsAreRejected() => Run(viewModel =>
    {
        viewModel.ProgramPath = "";
        Assert.ThrowsExactly<ArgumentException>(() => viewModel.BuildSessionConfig());

        viewModel.ProgramPath = Path.Combine(_tempDirectory, "missing.exe");
        Assert.ThrowsExactly<ArgumentException>(() => viewModel.BuildSessionConfig());

        viewModel.ProgramPath = CreateProgram();
        viewModel.WorkingDirectory = Path.Combine(_tempDirectory, "missing");
        Assert.ThrowsExactly<ArgumentException>(() => viewModel.BuildSessionConfig());

        viewModel.WorkingDirectory = "";
        viewModel.SamplingInterval = "-1";
        Assert.ThrowsExactly<ArgumentException>(() => viewModel.BuildSessionConfig());

        viewModel.SamplingInterval = "";
        viewModel.EnvironmentVariables.Add(new EnvironmentVariableViewModel { Name = "A=B" });
        Assert.ThrowsExactly<ArgumentException>(() => viewModel.BuildSessionConfig());

        viewModel.EnvironmentVariables.Clear();
        viewModel.AttachToProcess = true;
        viewModel.ProcessId = "abc";
        Assert.ThrowsExactly<ArgumentException>(() => viewModel.BuildSessionConfig());

        viewModel.ProcessId = "12, 34";
        CollectionAssert.AreEqual(new[] { 12, 34 }, viewModel.BuildSessionConfig().ProcessIds);
    });

    [TestMethod]
    public void MainWindowShowsTheCaptures() => Run(viewModel =>
    {
        var profile = Path.Combine(_tempDirectory, "ultra_KSP_x64_2026-11-14_10_00_00_pid_1234.json.gz");
        File.WriteAllBytes(profile, new byte[4096]);

        viewModel.State = SessionState.Starting;
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.Ready, UserModeSampling = false });
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureStarted, Capture = 1 });
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureSaving, Capture = 1 });
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureCompleted, Capture = 1, Path = profile });
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureStarted, Capture = 2 });
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.CaptureStatus, Capture = 2, Text = "Profiling Process KSP_x64 (1234) - 12s - 85 MB" });
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.Output, Text = "[UnityMemory] Configuration Parameters" });
        viewModel.HandleWorkerMessage(new WorkerMessage { Type = WorkerMessageTypes.Output, Text = "Something failed", IsError = true });

        var window = new MainWindow { DataContext = viewModel };
        viewModel.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // Let the output timer flush.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (viewModel.OutputLines.Count < 2 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        var open = buttons.Where(x => Equals(x.Content, "Open in Firefox Profiler") && x.IsEffectivelyVisible).ToList();
        Assert.AreEqual(1, open.Count, "Only the saved capture can be opened");
        Assert.IsTrue(open[0].IsEffectivelyEnabled);
        Assert.IsTrue(buttons.Single(x => Equals(x.Content, "■ Stop capture")).IsEffectivelyEnabled);
        Assert.IsFalse(buttons.Single(x => Equals(x.Content, "Start session")).IsEffectivelyEnabled);
        Assert.AreEqual(1, window.GetVisualDescendants().OfType<Border>().Count(x => x.Classes.Contains("dragHandle") && x.IsEffectivelyVisible));

        SaveScreenshot(window, "main-window.png");

        var settings = new SettingsWindow
        {
            DataContext = new SettingsViewModel(
                new AppSettings { SymbolPaths = { new SymbolPathEntrySetting { Path = @"E:\KSP\mono\symbols" } } }, window),
        };
        settings.Show();
        Dispatcher.UIThread.RunJobs();
        SaveScreenshot(settings, "settings-window.png");
        settings.Close();
        window.Close();
    });

    private string CreateProgram()
    {
        var program = Path.Combine(_tempDirectory, OperatingSystem.IsWindows() ? "game.exe" : "game");
        File.WriteAllBytes(program, []);
        return program;
    }

    /// <summary>
    /// Saves a rendering of the window when ULTRA_UI_SCREENSHOT_DIR is set, to look at the UI without running it.
    /// </summary>
    private static void SaveScreenshot(TopLevel topLevel, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("ULTRA_UI_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var frame = topLevel.CaptureRenderedFrame();
#pragma warning disable CS0618 // The replacement overload needs an encoder-specific options type; PNG defaults are what is wanted.
        frame?.Save(Path.Combine(directory, fileName));
#pragma warning restore CS0618
    }

    private void Run(Action<MainWindowViewModel> test)
    {
        _session!.Dispatch(() =>
        {
            var store = new SettingsStore(Path.Combine(_tempDirectory, "settings.json"));
            using var server = new ProfileServer();
            server.Start();
            using var viewModel = new MainWindowViewModel(store, store.Load(), server);
            test(viewModel);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}

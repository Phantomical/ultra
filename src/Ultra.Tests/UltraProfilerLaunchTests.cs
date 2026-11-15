// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// Tests how <see cref="UltraProfiler.Run"/> launches the profiled program (working directory, environment variables)
/// and ends a paused session whose processes exit, with a profiler whose sessions are fakes so that it runs without
/// administrator rights.
/// </summary>
[TestClass]
public class UltraProfilerLaunchTests
{
    private string _tempDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ultra-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [TestMethod]
    public void LaunchSettingsAreAppliedToTheStartInfo()
    {
        var options = new UltraProfilerOptions { WorkingDirectory = _tempDirectory };
        options.EnvironmentVariables["ULTRA_TEST_VARIABLE"] = "42";
        options.EnvironmentVariables["PATH"] = null;

        var startInfo = new ProcessStartInfo("program");
        UltraProfiler.ApplyLaunchSettings(startInfo, options);

        Assert.AreEqual(_tempDirectory, startInfo.WorkingDirectory);
        Assert.AreEqual("42", startInfo.Environment["ULTRA_TEST_VARIABLE"]);
        Assert.IsFalse(startInfo.Environment.ContainsKey("PATH"), "A null value removes the variable");
    }

    [TestMethod]
    public void WithoutLaunchSettingsTheStartInfoIsUnchanged()
    {
        var startInfo = new ProcessStartInfo("program") { UseShellExecute = true };
        UltraProfiler.ApplyLaunchSettings(startInfo, new UltraProfilerOptions());

        Assert.AreEqual("", startInfo.WorkingDirectory);
        // Starting through the shell is only possible when the environment block was never touched.
        Assert.IsTrue(startInfo.UseShellExecute);
    }

    [TestMethod]
    public async Task LaunchedProgramGetsTheWorkingDirectoryAndEnvironment()
    {
        var lines = new ConcurrentQueue<string>();
        var options = CreateLaunchOptions("echo %ULTRA_TEST_VARIABLE%& cd", "echo $ULTRA_TEST_VARIABLE; pwd");
        options.ConsoleMode = UltraProfilerConsoleMode.Live;
        options.ProgramLogStdout = lines.Enqueue;

        using var profiler = new FakeProfiler();
        await profiler.Run(options).WaitAsync(TimeSpan.FromSeconds(30));

        // The output is read asynchronously and can arrive after the exit was noticed.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (lines.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var output = lines.Select(x => x.Trim()).ToList();
        CollectionAssert.Contains(output, "hello ultra");
        Assert.IsTrue(output.Any(x => string.Equals(Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar), _tempDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                                     || x.EndsWith(Path.GetFileName(_tempDirectory), StringComparison.Ordinal)),
            $"Expected the working directory {_tempDirectory} in: {string.Join(" | ", output)}");
    }

    [TestMethod]
    public async Task SilentLaunchCanSetEnvironmentVariables()
    {
        // A silent launch normally goes through the shell, which cannot pass environment variables.
        var options = CreateLaunchOptions("echo %ULTRA_TEST_VARIABLE%> out.txt", "echo $ULTRA_TEST_VARIABLE > out.txt");
        options.ConsoleMode = UltraProfilerConsoleMode.Silent;

        using var profiler = new FakeProfiler();
        await profiler.Run(options).WaitAsync(TimeSpan.FromSeconds(30));

        var outputFile = Path.Combine(_tempDirectory, "out.txt");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(outputFile) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual("hello ultra", File.ReadAllText(outputFile).Trim());
    }

    [TestMethod]
    public async Task PausedSessionEndsWhenTheProcessExitsBeforeACaptureStarts()
    {
        // Waiting for a start used to spin forever once the profiled program had exited.
        using var process = StartShell("ping -n 2 127.0.0.1 > nul", "sleep 1");

        var options = new UltraProfilerOptions
        {
            Paused = true,
            CheckDeltaTimeInMs = 1,
            ShouldStartProfiling = () =>
            {
                Thread.Sleep(10);
                return false;
            },
        };
        options.ProcessIds.Add(process.Id);

        using var profiler = new FakeProfiler();
        var result = await profiler.Run(options).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(string.Empty, result);
        Assert.AreEqual(0, profiler.CapturesEnabled);
    }

    [TestMethod]
    public void AllProcessesExitedNeedsAtLeastOneProcess()
    {
        Assert.IsFalse(PausedSessionPlanner.AllProcessesExited([]));
        using var current = Process.GetCurrentProcess();
        Assert.IsFalse(PausedSessionPlanner.AllProcessesExited([current]));
    }

    private UltraProfilerOptions CreateLaunchOptions(string windowsCommand, string unixCommand)
    {
        var options = new UltraProfilerOptions
        {
            DurationInSeconds = 30,
            CheckDeltaTimeInMs = 10,
            WorkingDirectory = _tempDirectory,
        };
        options.EnvironmentVariables["ULTRA_TEST_VARIABLE"] = "hello ultra";

        if (OperatingSystem.IsWindows())
        {
            options.ProgramPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            options.Arguments.Add("/c");
            options.Arguments.Add(windowsCommand);
        }
        else
        {
            options.ProgramPath = "/bin/sh";
            options.Arguments.Add("-c");
            options.Arguments.Add(unixCommand);
        }

        return options;
    }

    private static Process StartShell(string windowsCommand, string unixCommand)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { ArgumentList = { "/c", windowsCommand } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", unixCommand } };
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo)!;
    }

    private sealed class FakeProfiler : UltraProfiler
    {
        private int _capturesEnabled;

        public int CapturesEnabled => Volatile.Read(ref _capturesEnabled);

        private protected override void DisposeImpl()
        {
        }

        private protected override ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess) =>
            new(baseName)
            {
                OnStart = () => Task.CompletedTask,
                OnEnablingProfiling = () =>
                {
                    Interlocked.Increment(ref _capturesEnabled);
                    return Task.CompletedTask;
                },
                OnProfiling = () => 0,
                OnStop = () => Task.CompletedTask,
                OnCatch = () => Task.CompletedTask,
                OnFinally = () => Task.CompletedTask,
                FinishFileToConvert = () => Task.FromResult(new List<UltraProfilerTraceFile>()),
                OnFinalCleanup = () => Task.CompletedTask,
            };
    }
}

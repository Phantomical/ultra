// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.IO.Pipes;
using Ultra.Core;
using Ultra.UI.Worker;

namespace Ultra.Tests.UI;

/// <summary>
/// Tests for the profiling worker of the ultra UI: its protocol, its control queue, how it is launched and the
/// profiler options it builds.
/// </summary>
[TestClass]
public class WorkerTests
{
    [TestMethod]
    public async Task MessagesRoundTripOverAPipe()
    {
        var pipeName = $"ultra-tests-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, WorkerClient.PipeBufferSize, WorkerClient.PipeBufferSize);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());

        using var serverChannel = new WorkerChannel(server);
        using var clientChannel = new WorkerChannel(client);

        var config = new WorkerSessionConfig
        {
            ProgramPath = @"C:\Games\KSP\KSP_x64.exe",
            Arguments = { "-popupwindow", "a \"quoted\"\nline" },
            EnvironmentVariables = { ["MONO_ENV_OPTIONS"] = "--debug" },
            OutputDirectory = @"D:\profiles",
            SamplingIntervalInMs = 0.5f,
            MaxCaptureDurationInSeconds = null,
        };

        var receivedTask = clientChannel.ReadAsync(CancellationToken.None);
        var completedTask = serverChannel.ReadAsync(CancellationToken.None);
        Assert.IsTrue(serverChannel.Send(new WorkerMessage { Type = WorkerMessageTypes.Config, Config = config }));
        Assert.IsTrue(clientChannel.Send(new WorkerMessage { Type = WorkerMessageTypes.CaptureCompleted, Capture = 3, Path = @"D:\profiles\a.json.gz" }));

        var received = await receivedTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(received?.Config);
        Assert.AreEqual(WorkerMessageTypes.Config, received.Type);
        CollectionAssert.AreEqual(config.Arguments, received.Config.Arguments);
        Assert.AreEqual("--debug", received.Config.EnvironmentVariables["MONO_ENV_OPTIONS"]);
        Assert.AreEqual(0.5f, received.Config.SamplingIntervalInMs);
        Assert.IsNull(received.Config.MaxCaptureDurationInSeconds);

        var completed = await completedTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(WorkerMessageTypes.CaptureCompleted, completed?.Type);
        Assert.AreEqual(3, completed?.Capture);
        Assert.AreEqual(@"D:\profiles\a.json.gz", completed?.Path);

        clientChannel.Dispose();
        await client.DisposeAsync();
        Assert.IsNull(await serverChannel.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)), "A closed channel reads as the end of the messages");
    }

    [TestMethod]
    public void WaitingForAStartIgnoresStops()
    {
        var control = new WorkerControl();
        control.Post(ProfilingControlAction.Stop);
        control.Post(ProfilingControlAction.Start);

        Assert.IsTrue(control.TryTakeStart(TimeSpan.Zero));
        Assert.IsFalse(control.TryTakeStart(TimeSpan.Zero));
    }

    [TestMethod]
    public void QuitEndsTheWaitForAStart()
    {
        var control = new WorkerControl();
        control.Post(ProfilingControlAction.Quit);
        control.Post(ProfilingControlAction.Start);

        Assert.IsTrue(control.QuitRequested);
        Assert.IsFalse(control.TryTakeStart(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void PollingWhileProfilingIgnoresStarts()
    {
        var control = new WorkerControl();
        control.Post(ProfilingControlAction.Start);
        Assert.AreEqual(ProfilingControlAction.None, control.Poll());

        control.Post(ProfilingControlAction.Stop);
        Assert.AreEqual(ProfilingControlAction.Stop, control.Poll());
        Assert.AreEqual(ProfilingControlAction.None, control.Poll());

        control.Post(ProfilingControlAction.Quit);
        Assert.AreEqual(ProfilingControlAction.Quit, control.Poll());
        Assert.AreEqual(ProfilingControlAction.Quit, control.Poll(), "Quit stays requested until the session ends");
    }

    [TestMethod]
    public void ElevatedWorkerGoesThroughTheShell()
    {
        var startInfo = WorkerLauncher.CreateStartInfo("ultra-ui-1", elevated: true, @"C:\Users\me\.dotnet\tools\ultra-ui.exe", @"C:\store\ultra-ui.dll");

        Assert.IsTrue(startInfo.UseShellExecute);
        Assert.AreEqual("runas", startInfo.Verb);
        Assert.AreEqual(@"C:\Users\me\.dotnet\tools\ultra-ui.exe", startInfo.FileName);
        Assert.AreEqual("--worker ultra-ui-1", startInfo.Arguments);
    }

    [TestMethod]
    public void UnelevatedWorkerRunsWithoutAWindow()
    {
        var startInfo = WorkerLauncher.CreateStartInfo("ultra-ui-1", elevated: false, @"C:\app\ultra-ui.exe", @"C:\app\ultra-ui.dll");

        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
        CollectionAssert.AreEqual(new[] { "--worker", "ultra-ui-1" }, startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void WorkerRunThroughTheDotnetHostPassesTheAssembly()
    {
        var elevated = WorkerLauncher.CreateStartInfo("p", elevated: true, @"C:\Program Files\dotnet\dotnet.exe", @"C:\My Tools\ultra-ui.dll");
        Assert.AreEqual("\"C:\\My Tools\\ultra-ui.dll\" --worker p", elevated.Arguments);

        var unelevated = WorkerLauncher.CreateStartInfo("p", elevated: false, @"C:\Program Files\dotnet\dotnet.exe", @"C:\My Tools\ultra-ui.dll");
        CollectionAssert.AreEqual(new[] { @"C:\My Tools\ultra-ui.dll", "--worker", "p" }, unelevated.ArgumentList.ToArray());
    }

    [TestMethod]
    public void OptionsDescribeAPausedSessionOfTheProgram()
    {
        SkipUnlessUserModeSamplingIsSupported();

        var config = new WorkerSessionConfig
        {
            ProgramPath = @"C:\Games\KSP\KSP_x64.exe",
            Arguments = { "-popupwindow" },
            WorkingDirectory = @"C:\Games\KSP",
            EnvironmentVariables = { ["MONO_ENV_OPTIONS"] = "--debug" },
            OutputDirectory = @"D:\profiles",
            SymbolPath = @"E:\symbols",
            UserModeSampling = true,
            CaptureProgramOutput = true,
        };

        var options = WorkerHost.CreateOptions(config, channel: null);

        Assert.IsTrue(options.Paused);
        Assert.AreEqual(config.ProgramPath, options.ProgramPath);
        CollectionAssert.AreEqual(new[] { "-popupwindow" }, options.Arguments);
        Assert.AreEqual(@"C:\Games\KSP", options.WorkingDirectory);
        Assert.AreEqual("--debug", options.EnvironmentVariables["MONO_ENV_OPTIONS"]);
        Assert.AreEqual(@"E:\symbols", options.SymbolPathText);
        Assert.AreEqual(UltraProfilerConsoleMode.Live, options.ConsoleMode);
        Assert.IsTrue(double.IsPositiveInfinity(options.DurationInSeconds), "No maximum duration means captures are only stopped by the user");
        Assert.AreEqual(UltraProfilerOptions.UserModeSamplingDefaultIntervalInMs, options.CpuSamplingIntervalInMs);
    }

    [TestMethod]
    public void OptionsKeepExplicitSamplingAndDuration()
    {
        SkipUnlessUserModeSamplingIsSupported();

        var config = new WorkerSessionConfig
        {
            ProcessIds = { 1234 },
            OutputDirectory = @"D:\profiles",
            UserModeSampling = true,
            SamplingIntervalInMs = 2.5f,
            MaxCaptureDurationInSeconds = 30,
        };

        var options = WorkerHost.CreateOptions(config, channel: null);

        CollectionAssert.AreEqual(new[] { 1234 }, options.ProcessIds);
        Assert.IsNull(options.ProgramPath);
        Assert.AreEqual(UltraProfilerConsoleMode.Silent, options.ConsoleMode);
        Assert.AreEqual(2.5f, options.CpuSamplingIntervalInMs);
        Assert.AreEqual(30, options.DurationInSeconds);
    }

    [TestMethod]
    public void OptionsRequireATarget()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WorkerHost.CreateOptions(new WorkerSessionConfig { OutputDirectory = @"D:\profiles" }, channel: null));
        Assert.ThrowsExactly<ArgumentException>(() => WorkerHost.CreateOptions(new WorkerSessionConfig { ProcessIds = { 1 } }, channel: null));
    }

    private static void SkipUnlessUserModeSamplingIsSupported()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            Assert.Inconclusive("User-mode sampling is only supported on Windows x64");
        }
    }
}

// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using Ultra.Core;

namespace Ultra.UI.Worker;

/// <summary>
/// The profiling worker: a separate process (started as administrator when the session needs kernel ETW sampling)
/// that runs one paused profiling session with <see cref="UltraProfiler"/> and is driven by the UI through a named pipe.
/// </summary>
/// <remarks>
/// The UI creates the pipe and the worker connects to it: a pipe created by an unelevated process can be opened by
/// an elevated process of the same user, while the reverse is not true by default. If the UI goes away, the worker
/// stops and saves the capture that is profiling and ends the session, so that no ETW session is left recording.
/// </remarks>
internal static class WorkerHost
{
    /// <summary>
    /// The command line argument that makes the ultra UI executable run as a worker, followed by the pipe name.
    /// </summary>
    public const string WorkerArgument = "--worker";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the worker until its session ends.
    /// </summary>
    /// <param name="pipeName">The pipe created by the UI.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string pipeName)
    {
        // Same as the CLI: symbol server lookups block thread pool threads while the next capture keeps profiling.
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIoThreads);
        ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, Environment.ProcessorCount + 64), minIoThreads);

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var connectCts = new CancellationTokenSource(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 2;
        }

        using var channel = new WorkerChannel(pipe);
        var configMessage = await channel.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (configMessage?.Type != WorkerMessageTypes.Config || configMessage.Config is null)
        {
            channel.Send(new WorkerMessage { Type = WorkerMessageTypes.SessionEnded, Text = "The worker did not receive a session configuration" });
            return 3;
        }

        var error = await RunSessionAsync(configMessage.Config, channel).ConfigureAwait(false);
        channel.Send(new WorkerMessage { Type = WorkerMessageTypes.SessionEnded, Text = error, IsError = error is not null });
        return error is null ? 0 : 1;
    }

    /// <summary>
    /// Runs the profiling session.
    /// </summary>
    /// <returns>The error that ended the session, or <c>null</c>.</returns>
    private static async Task<string?> RunSessionAsync(WorkerSessionConfig config, WorkerChannel channel)
    {
        var control = new WorkerControl();
        UltraProfiler profiler;
        try
        {
            profiler = UltraProfiler.Create();
        }
        catch (PlatformNotSupportedException ex)
        {
            return ex.Message;
        }

        using var readerCts = new CancellationTokenSource();
        var readerTask = Task.Run(() => ReadCommandsAsync(channel, control, profiler, readerCts.Token));

        try
        {
            UltraProfilerOptions options;
            try
            {
                options = CreateOptions(config, channel);

                // The profiles (and the intermediate trace files) are written to the current directory, with the same
                // names as the CLI would give them.
                var outputDirectory = Path.GetFullPath(config.OutputDirectory);
                Directory.CreateDirectory(outputDirectory);
                Directory.SetCurrentDirectory(outputDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return ex.Message;
            }

            foreach (var message in UltraProfiler.StopOrphanedSessions())
            {
                channel.Send(new WorkerMessage { Type = WorkerMessageTypes.Log, Text = message });
            }

            options.ShouldStartProfiling = () =>
            {
                if (control.TryTakeStart(TimeSpan.FromMilliseconds(100)))
                {
                    return true;
                }

                if (control.QuitRequested)
                {
                    // Ends the wait for a start; the session ends without a new capture.
                    profiler.Cancel();
                }

                return false;
            };
            options.PollProfilingControlAction = control.Poll;

            channel.Send(new WorkerMessage
            {
                Type = WorkerMessageTypes.Ready,
                Elevated = UltraProfiler.IsElevated(),
                UserModeSampling = options.UserModeSampling,
            });

            if (config.StartCaptureImmediately)
            {
                control.Post(ProfilingControlAction.Start);
            }

            try
            {
                await profiler.Run(options).ConfigureAwait(false);
                return null;
            }
            catch (InvalidOperationException) when (control.QuitRequested)
            {
                // Ending a session before any capture completed is reported by the profiler as a cancellation.
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
        finally
        {
            readerCts.Cancel();
            profiler.Dispose();
        }
    }

    /// <summary>
    /// Builds the profiler options of the session.
    /// </summary>
    /// <exception cref="ArgumentException">The configuration is invalid.</exception>
    internal static UltraProfilerOptions CreateOptions(WorkerSessionConfig config, WorkerChannel? channel)
    {
        if (string.IsNullOrWhiteSpace(config.OutputDirectory))
        {
            throw new ArgumentException("No output folder was given");
        }

        if (config.ProgramPath is null && config.ProcessIds.Count == 0)
        {
            throw new ArgumentException("No program to launch or process to attach to was given");
        }

        var options = new UltraProfilerOptions
        {
            Paused = true,
            ProgramPath = config.ProgramPath,
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? null : config.WorkingDirectory,
            SymbolPathText = config.SymbolPath,
            UserModeSampling = config.UserModeSampling,
            DurationInSeconds = config.MaxCaptureDurationInSeconds is > 0 ? config.MaxCaptureDurationInSeconds.Value : double.PositiveInfinity,
            ConsoleMode = config.CaptureProgramOutput ? UltraProfilerConsoleMode.Live : UltraProfilerConsoleMode.Silent,
        };

        options.Arguments.AddRange(config.Arguments);
        options.ProcessIds.AddRange(config.ProcessIds);
        foreach (var (name, value) in config.EnvironmentVariables)
        {
            options.EnvironmentVariables[name] = value;
        }

        // Same rules as the CLI: without administrator rights, sample from user mode.
        if (OperatingSystem.IsWindows() && !options.UserModeSampling && !UltraProfiler.IsElevated())
        {
            channel?.Send(new WorkerMessage { Type = WorkerMessageTypes.Log, Text = "Not running with administrator rights: using user-mode sampling instead of the kernel ETW session" });
            options.UserModeSampling = true;
        }

        if (options.UserModeSampling && (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64))
        {
            throw new ArgumentException("User-mode sampling is only supported on Windows x64");
        }

        if (config.SamplingIntervalInMs is > 0)
        {
            options.CpuSamplingIntervalInMs = config.SamplingIntervalInMs.Value;
        }
        else if (options.UserModeSampling)
        {
            options.CpuSamplingIntervalInMs = UltraProfilerOptions.UserModeSamplingDefaultIntervalInMs;
        }

        if (channel is not null)
        {
            options.CaptureStarted = index => channel.Send(new WorkerMessage { Type = WorkerMessageTypes.CaptureStarted, Capture = index });
            options.CaptureSaving = index => channel.Send(new WorkerMessage { Type = WorkerMessageTypes.CaptureSaving, Capture = index });
            options.CaptureStatusChanged = (index, text) => channel.Send(new WorkerMessage { Type = WorkerMessageTypes.CaptureStatus, Capture = index, Text = text });
            options.CaptureCompleted = (index, path) => channel.Send(new WorkerMessage { Type = WorkerMessageTypes.CaptureCompleted, Capture = index, Path = Path.GetFullPath(path) });
            options.CaptureFailed = (index, ex) => channel.Send(new WorkerMessage { Type = WorkerMessageTypes.CaptureFailed, Capture = index, Text = ex.Message });
            if (config.CaptureProgramOutput)
            {
                options.ProgramLogStdout = text => channel.Send(new WorkerMessage { Type = WorkerMessageTypes.Output, Text = text });
                options.ProgramLogStderr = text => channel.Send(new WorkerMessage { Type = WorkerMessageTypes.Output, Text = text, IsError = true });
            }
        }

        return options;
    }

    private static async Task ReadCommandsAsync(WorkerChannel channel, WorkerControl control, UltraProfiler profiler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WorkerMessage? message;
            try
            {
                message = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            switch (message?.Type)
            {
                case null:
                    // The UI is gone: save what is being captured and end the session.
                    control.Post(ProfilingControlAction.Quit);
                    return;
                case WorkerMessageTypes.Start:
                    control.Post(ProfilingControlAction.Start);
                    break;
                case WorkerMessageTypes.Stop:
                    control.Post(ProfilingControlAction.Stop);
                    break;
                case WorkerMessageTypes.Quit:
                    control.Post(ProfilingControlAction.Quit);
                    break;
                case WorkerMessageTypes.Abort:
                    control.Post(ProfilingControlAction.Quit);
                    // A second cancel stops the sessions without saving (and gives up on a hung save).
                    _ = Task.Run(() =>
                    {
                        profiler.Cancel();
                        profiler.Cancel();
                    }, CancellationToken.None);
                    break;
            }
        }
    }
}

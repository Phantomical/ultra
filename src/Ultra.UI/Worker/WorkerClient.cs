// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.IO.Pipes;

namespace Ultra.UI.Worker;

/// <summary>
/// The UI side of a profiling session: starts the worker (<see cref="WorkerHost"/>), sends it the session
/// configuration and commands, and reports what it sends back.
/// </summary>
internal sealed class WorkerClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The buffer size of each direction of the pipe. Without buffers, a write blocks until the other side reads it.
    /// </summary>
    internal const int PipeBufferSize = 64 * 1024;

    private readonly NamedPipeServerStream _pipe;
    private readonly WorkerChannel _channel;
    private readonly Process? _process;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readTask;

    private WorkerClient(NamedPipeServerStream pipe, Process? process)
    {
        _pipe = pipe;
        _process = process;
        _channel = new WorkerChannel(pipe);
    }

    /// <summary>
    /// Raised, on a background thread, for each message sent by the worker.
    /// </summary>
    public event Action<WorkerMessage>? MessageReceived;

    /// <summary>
    /// Raised, on a background thread, once the worker is gone.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// Starts a worker and its session.
    /// </summary>
    /// <param name="config">The session configuration.</param>
    /// <param name="elevated">Whether the worker runs as administrator (the user is prompted for consent).</param>
    /// <param name="cancellationToken">Cancels waiting for the worker to connect.</param>
    /// <returns>The connected client. Call <see cref="Run"/> to start receiving messages.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The worker could not be started, e.g. the user declined
    /// the elevation prompt.</exception>
    /// <exception cref="InvalidOperationException">The worker exited or did not connect.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<WorkerClient> StartAsync(WorkerSessionConfig config, bool elevated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var pipeName = $"ultra-ui-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, PipeBufferSize, PipeBufferSize);
        Process? process = null;
        try
        {
            process = WorkerLauncher.Start(pipeName, elevated);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            var connectTask = pipe.WaitForConnectionAsync(connectCts.Token);
            var exitTask = process.WaitForExitAsync(connectCts.Token);
            var first = await Task.WhenAny(connectTask, exitTask).ConfigureAwait(false);
            if (first != connectTask)
            {
                connectCts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException(process.HasExited
                    ? $"The profiling worker exited before connecting (exit code {process.ExitCode})"
                    : "The profiling worker did not connect");
            }

            await connectTask.ConfigureAwait(false);

            var client = new WorkerClient(pipe, process);
            client._channel.Send(new WorkerMessage { Type = WorkerMessageTypes.Config, Config = config });
            return client;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (process is not null && !process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            process?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts receiving the messages of the worker.
    /// </summary>
    public void Run()
    {
        _readTask ??= Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Sends a command to the worker.
    /// </summary>
    /// <param name="type">The command (see <see cref="WorkerMessageTypes"/>).</param>
    /// <returns><c>true</c> if it was sent; <c>false</c> if the worker is gone.</returns>
    public bool Send(string type) => _channel.Send(WorkerMessage.Of(type));

    /// <summary>
    /// Kills the worker, as a last resort when it does not respond. Its ETW sessions are stopped by the next session.
    /// </summary>
    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // An elevated worker cannot be killed from an unelevated UI; closing the pipe makes it end its session.
        }

        _pipe.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _channel.Dispose();
        _pipe.Dispose();
        _process?.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                WorkerMessage? message;
                try
                {
                    message = await _channel.ReadAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }

                if (message is null)
                {
                    break;
                }

                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }
}

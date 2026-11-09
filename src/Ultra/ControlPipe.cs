// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.IO.Pipes;
using Ultra.Core;

namespace Ultra;

/// <summary>
/// Lets another process control a paused profiling session through a named pipe, one command per connection.
/// Commands are <c>start</c>, <c>stop</c>, <c>wait</c>, <c>status</c> and <c>quit</c>; each gets a single reply
/// line starting with <c>ok</c> or <c>error</c>.
/// </summary>
internal sealed class ControlPipe : IDisposable
{
    private readonly string _pipeName;
    private readonly BlockingCollection<ProfilingControlAction> _actions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskCompletionSource<string>> _completions = new();
    private TaskCompletionSource<int> _nextStart = NewTcs<int>();
    private int _activeCapture;
    private int _lastCapture;
    private Task? _serverTask;

    public ControlPipe(string pipeName)
    {
        _pipeName = pipeName;
    }

    public void Start()
    {
        _serverTask = Task.Run(ServeAsync);
    }

    public bool TryTakeStart(TimeSpan timeout)
    {
        while (_actions.TryTake(out var action, timeout))
        {
            if (action == ProfilingControlAction.Start || action == ProfilingControlAction.Quit)
            {
                return action == ProfilingControlAction.Start;
            }

            timeout = TimeSpan.Zero;
        }

        return false;
    }

    public bool QuitRequested { get; private set; }

    public ProfilingControlAction Poll()
    {
        while (_actions.TryTake(out var action))
        {
            if (action != ProfilingControlAction.Start)
            {
                return action;
            }
        }

        return ProfilingControlAction.None;
    }

    public void CaptureStarted(int index)
    {
        TaskCompletionSource<int> started;
        lock (_lock)
        {
            _activeCapture = index;
            _lastCapture = index;
            GetCompletion(index);
            started = _nextStart;
            _nextStart = NewTcs<int>();
        }

        started.TrySetResult(index);
    }

    public void CaptureSaving(int index)
    {
        lock (_lock)
        {
            if (_activeCapture == index)
            {
                _activeCapture = 0;
            }
        }
    }

    public void CaptureCompleted(int index, string path)
    {
        lock (_lock)
        {
            GetCompletion(index).TrySetResult(path);
        }
    }

    public void CaptureFailed(int index, Exception ex)
    {
        lock (_lock)
        {
            GetCompletion(index).TrySetException(ex);
        }
    }

    private TaskCompletionSource<string> GetCompletion(int index)
    {
        if (!_completions.TryGetValue(index, out var tcs))
        {
            tcs = NewTcs<string>();
            _completions[index] = tcs;
        }

        return tcs;
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }

            _ = HandleAsync(server);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        await using var _ = server;
        try
        {
            using var reader = new StreamReader(server, leaveOpen: true);
            await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            var command = (await reader.ReadLineAsync(_cts.Token))?.Trim().ToLowerInvariant() ?? "";
            string reply;
            try
            {
                reply = await ExecuteAsync(command);
            }
            catch (Exception ex)
            {
                reply = $"error {ex.Message}";
            }

            await writer.WriteLineAsync(reply);
        }
        catch (Exception)
        {
            // The client went away; nothing to report it to.
        }
    }

    private async Task<string> ExecuteAsync(string command)
    {
        switch (command)
        {
            case "start":
            {
                Task<int> started;
                lock (_lock)
                {
                    if (_activeCapture != 0)
                    {
                        return $"error capture {_activeCapture} is already profiling";
                    }

                    started = _nextStart.Task;
                }

                _actions.Add(ProfilingControlAction.Start);
                var index = await started.WaitAsync(_cts.Token);
                return $"ok started {index}";
            }
            case "stop":
            case "wait":
            {
                Task<string> completed;
                int index;
                lock (_lock)
                {
                    index = _activeCapture != 0 ? _activeCapture : _lastCapture;
                    if (index == 0)
                    {
                        return "error no capture has been started";
                    }

                    if (command == "stop" && _activeCapture != 0)
                    {
                        _actions.Add(ProfilingControlAction.Stop);
                    }

                    completed = GetCompletion(index).Task;
                }

                var path = await completed.WaitAsync(_cts.Token);
                return $"ok {index} {path}";
            }
            case "status":
                lock (_lock)
                {
                    return _activeCapture != 0 ? $"ok profiling {_activeCapture}" : $"ok idle {_lastCapture}";
                }
            case "quit":
                QuitRequested = true;
                _actions.Add(ProfilingControlAction.Quit);
                return "ok";
            default:
                return $"error unknown command '{command}' (expected start, stop, wait, status or quit)";
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }

    public static async Task<int> SendAsync(string pipeName, string command, TimeSpan timeout)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(connectCts.Token);
            }

            using var reader = new StreamReader(client, leaveOpen: true);
            await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(command);
            var reply = await reader.ReadLineAsync(cts.Token) ?? "error the session closed the pipe without replying";
            Console.WriteLine(reply);
            return reply.StartsWith("ok", StringComparison.Ordinal) ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(client.IsConnected ? $"error no reply within {timeout.TotalSeconds}s" : $"error no profiling session is listening on pipe '{pipeName}'");
            return 1;
        }
    }
}

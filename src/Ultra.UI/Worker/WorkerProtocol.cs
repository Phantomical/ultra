// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ultra.UI.Worker;

/// <summary>
/// The types of <see cref="WorkerMessage"/> exchanged between the UI and the profiling worker.
/// </summary>
internal static class WorkerMessageTypes
{
    // UI -> worker

    /// <summary>The session configuration, the first message sent to the worker (<see cref="WorkerMessage.Config"/>).</summary>
    public const string Config = "config";

    /// <summary>Starts a new capture.</summary>
    public const string Start = "start";

    /// <summary>Stops and saves the current capture, keeping the session.</summary>
    public const string Stop = "stop";

    /// <summary>Stops and saves the current capture, then ends the session.</summary>
    public const string Quit = "quit";

    /// <summary>Aborts the session without saving the current capture.</summary>
    public const string Abort = "abort";

    // Worker -> UI

    /// <summary>The worker is ready (<see cref="WorkerMessage.Elevated"/>, <see cref="WorkerMessage.UserModeSampling"/>).</summary>
    public const string Ready = "ready";

    /// <summary>A capture started profiling (<see cref="WorkerMessage.Capture"/>).</summary>
    public const string CaptureStarted = "captureStarted";

    /// <summary>A capture stopped profiling and is saving (<see cref="WorkerMessage.Capture"/>).</summary>
    public const string CaptureSaving = "captureSaving";

    /// <summary>The status of a capture changed (<see cref="WorkerMessage.Capture"/>, <see cref="WorkerMessage.Text"/>).</summary>
    public const string CaptureStatus = "captureStatus";

    /// <summary>A capture was saved (<see cref="WorkerMessage.Capture"/>, <see cref="WorkerMessage.Path"/>).</summary>
    public const string CaptureCompleted = "captureCompleted";

    /// <summary>A capture could not be saved (<see cref="WorkerMessage.Capture"/>, <see cref="WorkerMessage.Text"/>).</summary>
    public const string CaptureFailed = "captureFailed";

    /// <summary>A line written by the profiled program (<see cref="WorkerMessage.Text"/>, <see cref="WorkerMessage.IsError"/>).</summary>
    public const string Output = "output";

    /// <summary>A message about the session (<see cref="WorkerMessage.Text"/>, <see cref="WorkerMessage.IsError"/>).</summary>
    public const string Log = "log";

    /// <summary>The session ended; <see cref="WorkerMessage.Text"/> is the error that ended it, if any.</summary>
    public const string SessionEnded = "sessionEnded";
}

/// <summary>
/// A message exchanged between the UI and the profiling worker, one JSON object per line.
/// </summary>
internal sealed class WorkerMessage
{
    /// <summary>Gets or sets the type of the message (see <see cref="WorkerMessageTypes"/>).</summary>
    public string Type { get; set; } = "";

    /// <summary>Gets or sets the 1-based index of the capture the message is about.</summary>
    public int? Capture { get; set; }

    /// <summary>Gets or sets the text of the message.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="Text"/> is an error.</summary>
    public bool? IsError { get; set; }

    /// <summary>Gets or sets the path of the saved profile.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets a value indicating whether the worker runs as administrator.</summary>
    public bool? Elevated { get; set; }

    /// <summary>Gets or sets a value indicating whether the session uses user-mode sampling.</summary>
    public bool? UserModeSampling { get; set; }

    /// <summary>Gets or sets the session configuration.</summary>
    public WorkerSessionConfig? Config { get; set; }

    /// <summary>Creates a message of the given type.</summary>
    public static WorkerMessage Of(string type) => new() { Type = type };
}

/// <summary>
/// What a profiling session profiles and how, sent by the UI to the worker.
/// </summary>
internal sealed class WorkerSessionConfig
{
    /// <summary>Gets or sets the program to launch, or <c>null</c> to attach to <see cref="ProcessIds"/>.</summary>
    public string? ProgramPath { get; set; }

    /// <summary>Gets or sets the arguments of the program to launch.</summary>
    public List<string> Arguments { get; set; } = new();

    /// <summary>Gets or sets the working directory of the program to launch.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets the environment variables of the program to launch.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>Gets or sets the processes to attach to.</summary>
    public List<int> ProcessIds { get; set; } = new();

    /// <summary>Gets or sets the folder the profiles are written to.</summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>Gets or sets the symbol path, or <c>null</c> for the default one.</summary>
    public string? SymbolPath { get; set; }

    /// <summary>Gets or sets a value indicating whether user-mode sampling is used even when running as administrator.</summary>
    public bool UserModeSampling { get; set; }

    /// <summary>Gets or sets the sampling interval in milliseconds, or <c>null</c> for the default.</summary>
    public float? SamplingIntervalInMs { get; set; }

    /// <summary>Gets or sets the maximum duration of a capture in seconds, or <c>null</c> for no limit.</summary>
    public double? MaxCaptureDurationInSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether the output of the launched program is forwarded to the UI.</summary>
    public bool CaptureProgramOutput { get; set; }

    /// <summary>Gets or sets a value indicating whether the first capture starts as soon as the session starts.</summary>
    public bool StartCaptureImmediately { get; set; }
}

/// <summary>
/// The JSON serialization context of the worker protocol (source generated, so that no reflection is needed).
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(WorkerMessage))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <see cref="WorkerMessage"/>s as JSON lines over a stream. Sending is thread-safe.
/// </summary>
internal sealed class WorkerChannel : IDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerChannel"/> class.
    /// </summary>
    /// <param name="stream">The connected stream.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public WorkerChannel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader = new StreamReader(stream, leaveOpen: true);
        _writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>
    /// Sends a message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns><c>true</c> if the message was sent; <c>false</c> if the other side is gone.</returns>
    public bool Send(WorkerMessage message)
    {
        var line = JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerMessage);
        lock (_writeLock)
        {
            try
            {
                _writer.WriteLine(line);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Reads the next message.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The message, or <c>null</c> when the other side closed the channel.</returns>
    /// <exception cref="JsonException">A line is not a valid message.</exception>
    public async Task<WorkerMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return null;
            }

            if (line is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerMessage)
                   ?? throw new JsonException("Unexpected null message");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_writeLock)
        {
            _reader.Dispose();
            try
            {
                _writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The other side is gone: nothing left to flush to.
            }
        }
    }
}

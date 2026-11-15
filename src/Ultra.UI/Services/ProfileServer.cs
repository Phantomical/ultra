// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ultra.UI.Services;

/// <summary>
/// A minimal HTTP server, listening on the loopback interface only, that serves the profiles produced by ultra so that
/// the Firefox Profiler web application can open them directly from a link (see <see cref="FirefoxProfilerLinks"/>)
/// instead of having the file dragged and dropped onto it.
/// </summary>
/// <remarks>
/// Only files explicitly registered with <see cref="Register"/> are served, each under an unguessable random token, so
/// that a web page cannot use the server to read other local files. Responses allow any origin because the profile is
/// fetched by the Firefox Profiler page (https://profiler.firefox.com, or a self-hosted copy of it), and preflight
/// requests are answered with <c>Access-Control-Allow-Private-Network</c> for browsers that guard requests from public
/// sites to the local machine.
/// </remarks>
internal sealed class ProfileServer : IDisposable
{
    private const int MaxRequestHeaderBytes = 32 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, string> _filesByToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokensByFile = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptTask;

    /// <summary>
    /// Gets the port the server listens on, once started.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Starts listening on the loopback interface.
    /// </summary>
    /// <param name="port">The port to listen on, or 0 to let the system choose a free one.</param>
    /// <exception cref="InvalidOperationException">The server is already started.</exception>
    /// <exception cref="SocketException">The port could not be bound.</exception>
    public void Start(int port = 0)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The profile server is already started");
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Makes <paramref name="filePath"/> downloadable and returns its URL. Registering the same file again returns the
    /// same URL.
    /// </summary>
    /// <param name="filePath">The profile file to serve.</param>
    /// <returns>The URL of the file on this server.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The server is not started.</exception>
    public Uri Register(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (_listener is null)
        {
            throw new InvalidOperationException("The profile server is not started");
        }

        var fullPath = Path.GetFullPath(filePath);
        var token = _tokensByFile.GetOrAdd(fullPath, static _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        _filesByToken[token] = fullPath;
        return new Uri($"http://127.0.0.1:{Port}/profiles/{token}/{Uri.EscapeDataString(Path.GetFileName(fullPath))}");
    }

    /// <summary>
    /// Stops serving <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">A file previously passed to <see cref="Register"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <c>null</c>.</exception>
    public void Unregister(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (_tokensByFile.TryRemove(Path.GetFullPath(filePath), out var token))
        {
            _filesByToken.TryRemove(token, out _);
        }
    }

    /// <summary>
    /// Stops the server.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        CancellationTokenSource cts;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        using var __ = cts;
        cts.CancelAfter(RequestTimeout);
        try
        {
            var stream = client.GetStream();
            var request = await ReadRequestHeadAsync(stream, cts.Token).ConfigureAwait(false);
            if (request is null)
            {
                await WriteResponseHeadAsync(stream, 400, "Bad Request", null, false, cts.Token).ConfigureAwait(false);
                return;
            }

            var (method, target) = request.Value;
            if (method == "OPTIONS")
            {
                await WriteResponseHeadAsync(stream, 204, "No Content", null, true, cts.Token).ConfigureAwait(false);
                return;
            }

            if (method != "GET" && method != "HEAD")
            {
                await WriteResponseHeadAsync(stream, 405, "Method Not Allowed", null, false, cts.Token).ConfigureAwait(false);
                return;
            }

            var filePath = ResolveFile(target);
            if (filePath is null || !File.Exists(filePath))
            {
                await WriteResponseHeadAsync(stream, 404, "Not Found", null, false, cts.Token).ConfigureAwait(false);
                return;
            }

            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            await WriteResponseHeadAsync(stream, 200, "OK", file.Length, false, cts.Token).ConfigureAwait(false);
            if (method == "GET")
            {
                await file.CopyToAsync(stream, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // The browser went away, or the file could not be read: there is nobody to report it to.
        }
    }

    /// <summary>
    /// Maps a request target <c>/profiles/{token}/{file name}</c> to the registered file.
    /// </summary>
    private string? ResolveFile(string target)
    {
        var queryStart = target.IndexOfAny(['?', '#']);
        if (queryStart >= 0)
        {
            target = target[..queryStart];
        }

        const string prefix = "/profiles/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = target.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');
        var token = (slash >= 0 ? rest[..slash] : rest).ToString();
        return _filesByToken.TryGetValue(token, out var path) ? path : null;
    }

    private static async Task<(string Method, string Target)?> ReadRequestHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeaderBytes];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        if (lineEnd <= 0)
        {
            return null;
        }

        var parts = head[..lineEnd].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return null;
        }

        return (parts[0].ToUpperInvariant(), parts[1]);
    }

    private static async Task WriteResponseHeadAsync(Stream stream, int statusCode, string reason, long? contentLength, bool preflight, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
        builder.Append("Access-Control-Allow-Origin: *\r\n");
        if (preflight)
        {
            builder.Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n");
            builder.Append("Access-Control-Allow-Headers: *\r\n");
            builder.Append("Access-Control-Allow-Private-Network: true\r\n");
            builder.Append("Access-Control-Max-Age: 600\r\n");
        }

        if (contentLength is { } length)
        {
            // Served as is: the Firefox Profiler detects and decompresses gzip itself.
            builder.Append("Content-Type: application/octet-stream\r\n");
            builder.Append($"Content-Length: {length}\r\n");
        }
        else
        {
            builder.Append("Content-Length: 0\r\n");
        }

        builder.Append("Cache-Control: no-store\r\n");
        builder.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
    }
}

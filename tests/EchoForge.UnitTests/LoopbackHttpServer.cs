using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EchoForge.UnitTests;

/// <summary>
/// A tiny HTTP/1.1 server on the loopback interface, for testing the downloader.
///
/// <para>
/// Hand-rolled on a <see cref="TcpListener"/> rather than built on <c>HttpListener</c> for two
/// reasons: HttpListener needs a URL reservation on Windows, and the interesting cases here are
/// misbehaviours — ignoring a range request, closing halfway through a body, lying about
/// Content-Length — which a well-behaved server framework goes out of its way to prevent.
/// </para>
///
/// <para>
/// Routine tests must never depend on a public network. Everything the downloader is judged on
/// happens against this.
/// </para>
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private int _requests;
    private int _rangeRequests;

    public LoopbackHttpServer(byte[] content)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public byte[] Content { get; set; }

    public int Port { get; }

    /// <summary>The URL of the served file. Loopback http, which the manifest reader allows only here.</summary>
    public string Url => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/artifact.bin");

    /// <summary>When false, a Range request is answered with the whole file and a 200.</summary>
    public bool SupportsRange { get; set; } = true;

    /// <summary>Send at most this many body bytes, then close. Simulates a dropped connection.</summary>
    public int? TruncateBodyAfter { get; set; }

    /// <summary>Advertise a Content-Length that is not the truth.</summary>
    public long? DeclaredLengthOverride { get; set; }

    /// <summary>Send this many bytes more than asked for, to prove the client stops.</summary>
    public int ExtraTrailingBytes { get; set; }

    /// <summary>
    /// Send no Content-Length at all and let the connection close delimit the body, as an
    /// HTTP/1.0-era server does. With no declared length there is nothing to check the response
    /// against up front, so the running size guard is the only protection left.
    /// </summary>
    public bool OmitContentLength { get; set; }

    /// <summary>Wait before answering at all. For timeout and cancellation tests.</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Answer everything with this status instead. For 404 and 403 tests.</summary>
    public int? ForceStatus { get; set; }

    public int Requests => Volatile.Read(ref _requests);

    public int RangeRequests => Volatile.Read(ref _rangeRequests);

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string? request = await ReadHeadersAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);

                if (ResponseDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ResponseDelay, _stopping.Token).ConfigureAwait(false);
                }

                if (ForceStatus is { } forced)
                {
                    await WriteAsync(stream, $"HTTP/1.1 {forced} Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    return;
                }

                long from = ParseRangeStart(request);
                if (from > 0)
                {
                    Interlocked.Increment(ref _rangeRequests);
                }

                byte[] content = Content;

                if (from > 0 && SupportsRange)
                {
                    if (from >= content.Length)
                    {
                        await WriteAsync(stream,
                            $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{content.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                            .ConfigureAwait(false);
                        return;
                    }

                    long length = content.Length - from;
                    await WriteAsync(stream,
                        $"HTTP/1.1 206 Partial Content\r\nAccept-Ranges: bytes\r\n" +
                        $"Content-Range: bytes {from}-{content.Length - 1}/{content.Length}\r\n" +
                        $"Content-Length: {DeclaredLengthOverride ?? length}\r\nConnection: close\r\n\r\n")
                        .ConfigureAwait(false);

                    await WriteBodyAsync(stream, content, (int)from).ConfigureAwait(false);
                    return;
                }

                // Either no range was asked for, or this server refuses to honour one.
                string lengthHeader = OmitContentLength
                    ? string.Empty
                    : $"Content-Length: {DeclaredLengthOverride ?? content.Length}\r\n";

                await WriteAsync(stream,
                    $"HTTP/1.1 200 OK\r\n{(SupportsRange ? "Accept-Ranges: bytes\r\n" : string.Empty)}" +
                    $"{lengthHeader}Connection: close\r\n\r\n")
                    .ConfigureAwait(false);

                await WriteBodyAsync(stream, content, 0).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // A client that hung up mid-transfer is one of the things being tested.
            }
        }
    }

    private async Task WriteBodyAsync(NetworkStream stream, byte[] content, int offset)
    {
        int available = content.Length - offset;
        int toSend = TruncateBodyAfter is { } cap ? Math.Min(cap, available) : available;

        await stream.WriteAsync(content.AsMemory(offset, toSend), _stopping.Token).ConfigureAwait(false);

        if (ExtraTrailingBytes > 0)
        {
            await stream.WriteAsync(new byte[ExtraTrailingBytes], _stopping.Token).ConfigureAwait(false);
        }

        await stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream)
    {
        StringBuilder headers = new();
        byte[] one = new byte[1];

        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(one).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            headers.Append((char)one[0]);

            if (headers.Length > 8192)
            {
                return null;
            }
        }

        return headers.ToString();
    }

    private static long ParseRangeStart(string request)
    {
        foreach (string line in request.Split("\r\n"))
        {
            if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            int dash = line.IndexOf('-', StringComparison.Ordinal);
            if (equals < 0 || dash < equals)
            {
                return 0;
            }

            return long.TryParse(
                line.AsSpan(equals + 1, dash - equals - 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long from)
                ? from
                : 0;
        }

        return 0;
    }

    private async Task WriteAsync(NetworkStream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stopping.Token).ConfigureAwait(false);

    public void Dispose()
    {
        _stopping.Cancel();

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already down.
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends by being cancelled; that is the intended path.
        }

        _stopping.Dispose();
    }
}

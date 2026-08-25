using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SteamFinish.Tests;

/// <summary>
/// Stands in for the DevTools endpoint Steam opens: serves the page list over HTTP, then upgrades to
/// a WebSocket and answers one <c>Runtime.evaluate</c>.
///
/// Written straight onto a socket rather than with HttpListener, which needs a URL reservation or an
/// elevated process on Windows and would make the test pass or fail depending on who is running it.
/// </summary>
internal sealed class FakeDevToolsEndpoint : IDisposable
{
    /// <summary>The constant every WebSocket handshake hashes the client key with.</summary>
    private const string HandshakeSalt = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly string? _pageListJson;
    private readonly Func<string, string>? _answer;
    private readonly Task _serving;

    /// <param name="pageListJson">
    /// What <c>/json/list</c> returns. <c>null</c> serves an HTML page instead, standing in for
    /// something that is not DevTools at all holding the port.
    /// </param>
    /// <param name="answer">Given the evaluated expression, returns the JSON frame to send back.</param>
    private FakeDevToolsEndpoint(Func<int, string>? pageListJson, Func<string, string>? answer)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _pageListJson = pageListJson?.Invoke(Port);
        _answer = answer;
        _serving = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>An endpoint that behaves like Steam: one shared context that answers as told.</summary>
    public static FakeDevToolsEndpoint LikeSteam(Func<string, string> answer) => new(
        port => $$"""
                  [{"title":"Steam","url":"https://steamloopback.host/","webSocketDebuggerUrl":"ws://127.0.0.1:{{port}}/devtools/page/AAAA"},
                   {"title":"SharedJSContext","url":"about:blank","webSocketDebuggerUrl":"ws://127.0.0.1:{{port}}/devtools/page/BBBB"}]
                  """,
        answer);

    /// <summary>A DevTools endpoint with no shared context — the right shape, the wrong program.</summary>
    public static FakeDevToolsEndpoint WithoutSharedContext() => new(
        port => $$"""
                  [{"title":"New Tab","url":"about:blank","webSocketDebuggerUrl":"ws://127.0.0.1:{{port}}/devtools/page/AAAA"}]
                  """,
        answer: null);

    /// <summary>Something else entirely holding the port.</summary>
    public static FakeDevToolsEndpoint NotDevToolsAtAll() => new(pageListJson: null, answer: null);

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await HandleAsync(client).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A client that hangs up mid-handshake is one of the cases under test.
                    }
                }
            });
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var stream = client.GetStream();
        var headers = await ReadHeadersAsync(stream).ConfigureAwait(false);
        if (headers.Length == 0)
        {
            return;
        }

        var key = HeaderValue(headers, "Sec-WebSocket-Key");
        if (key is null)
        {
            await WriteHttpAsync(stream, headers).ConfigureAwait(false);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeSalt)));
        var upgrade = "HTTP/1.1 101 Switching Protocols\r\n"
                      + "Upgrade: websocket\r\n"
                      + "Connection: Upgrade\r\n"
                      + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(upgrade)).ConfigureAwait(false);

        var request = await ReadFrameAsync(stream).ConfigureAwait(false);
        if (request is null || _answer is null)
        {
            return;
        }

        // Real DevTools pushes events nobody asked for. Sending one first proves they are read past
        // rather than mistaken for the answer.
        await stream.WriteAsync(EncodeFrame("""{"method":"Runtime.consoleAPICalled","params":{}}"""))
            .ConfigureAwait(false);

        await stream.WriteAsync(EncodeFrame(_answer(request))).ConfigureAwait(false);
    }

    private async Task WriteHttpAsync(NetworkStream stream, string headers)
    {
        var (contentType, body) = _pageListJson is { } json
            ? ("application/json", json)
            : ("text/html", "<!doctype html><html><body>Sign in</body></html>");

        var bytes = Encoding.UTF8.GetBytes(body);
        var response = "HTTP/1.1 200 OK\r\n"
                       + $"Content-Type: {contentType}\r\n"
                       + $"Content-Length: {bytes.Length}\r\n"
                       + "Connection: close\r\n\r\n";

        _ = headers;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];

        while (!builder.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append((char)buffer[0]);
        }

        return builder.ToString();
    }

    private static string? HeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase))
            {
                return line[(name.Length + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>Reads one client text frame. Client frames are always masked.</summary>
    private static async Task<string?> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[2];
        if (!await FillAsync(stream, header).ConfigureAwait(false))
        {
            return null;
        }

        long length = header[1] & 0x7F;
        if (length == 126)
        {
            var extended = new byte[2];
            if (!await FillAsync(stream, extended).ConfigureAwait(false))
            {
                return null;
            }

            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            if (!await FillAsync(stream, extended).ConfigureAwait(false))
            {
                return null;
            }

            length = BitConverter.ToInt64([.. extended.Reverse()]);
        }

        var mask = new byte[4];
        if ((header[1] & 0x80) != 0 && !await FillAsync(stream, mask).ConfigureAwait(false))
        {
            return null;
        }

        var payload = new byte[length];
        if (!await FillAsync(stream, payload).ConfigureAwait(false))
        {
            return null;
        }

        if ((header[1] & 0x80) != 0)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i % 4];
            }
        }

        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>Builds one unmasked server text frame; server frames are never masked.</summary>
    private static byte[] EncodeFrame(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new List<byte> { 0x81 };

        if (payload.Length < 126)
        {
            frame.Add((byte)payload.Length);
        }
        else
        {
            frame.Add(126);
            frame.Add((byte)(payload.Length >> 8));
            frame.Add((byte)(payload.Length & 0xFF));
        }

        frame.AddRange(payload);
        return [.. frame];
    }

    private static async Task<bool> FillAsync(NetworkStream stream, byte[] buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled)).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();

        try
        {
            _serving.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutting the stand-in down is best effort.
        }

        _stopping.Dispose();
    }
}

using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using SteamFinish.Core.Logging;

namespace SteamFinish.Core.Control;

/// <summary>One debuggable page inside the Steam client.</summary>
internal sealed record BridgeTarget(string Title, string Url, string WebSocketUrl);

/// <summary>What came back from evaluating one expression inside Steam.</summary>
internal sealed record BridgeReply(ControlOutcome Outcome, string? Value = null, string? Detail = null);

/// <summary>
/// The channel into the Steam client's own JavaScript.
///
/// Steam has no command line, registry key or URL that pauses a download — the steam:// protocol
/// covers installing, validating and launching, and nothing else. What it does have is a Chromium
/// client whose UI drives the downloader through a privileged <c>SteamClient</c> object, and
/// Chromium can be asked to expose that object over the DevTools protocol. So the only honest way
/// to pause a download from outside Steam is to say what the Downloads page itself says when its
/// pause button is pressed: <c>SteamClient.Downloads.EnableAllDownloads(false)</c>.
///
/// Steam opens that channel only when a marker file sits in its install folder, and only reads the
/// marker at start-up — which is why <see cref="ControlOutcome.RestartSteam"/> is a separate answer
/// from <see cref="ControlOutcome.BridgeDisabled"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamCefBridge : IDisposable
{
    /// <summary>Steam looks for this in its install folder; the contents are never read.</summary>
    public const string MarkerFileName = ".cef-enable-remote-debugging";

    /// <summary>Where Steam's Chromium listens unless it was told otherwise.</summary>
    public const int DefaultDebugPort = 8080;

    /// <summary>The offscreen window that runs Steam's own logic, and the only one that has SteamClient.</summary>
    private const string SharedContext = "SharedJSContext";

    private readonly ILog _log;
    private readonly int[]? _fixedPorts;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// The port the shared context was last reached on. Kept so the usual case is one request, not a
    /// sweep, and so the UI can show which port ended up being used.
    /// </summary>
    public int? LastGoodPort { get; private set; }

    /// <param name="ports">
    /// Restricts the search to these ports. Used by the tests, which stand a scripted DevTools
    /// endpoint up on a free port rather than fighting whatever holds 8080 on the machine.
    /// </param>
    public SteamCefBridge(ILog? log = null, params int[]? ports)
    {
        _log = log ?? NullLog.Instance;
        _fixedPorts = ports is { Length: > 0 } ? ports : null;
    }

    /// <summary>
    /// The ports worth trying, best first: the one that worked last time, then Steam's default, then
    /// every other port a running steam.exe is holding — which is how a client started with
    /// <c>-devtools-port</c> is found without anyone having to say so.
    /// </summary>
    private IEnumerable<int> Candidates()
    {
        if (_fixedPorts is { } fixedPorts)
        {
            return fixedPorts;
        }

        var ordered = new List<int>();

        void Add(int port)
        {
            if (port > 0 && !ordered.Contains(port))
            {
                ordered.Add(port);
            }
        }

        if (LastGoodPort is { } remembered)
        {
            Add(remembered);
        }

        Add(DefaultDebugPort);

        foreach (var port in SteamPorts.ListeningPorts())
        {
            Add(port);
        }

        return ordered;
    }

    /// <summary>Where the marker file belongs, or <c>null</c> when Steam cannot be found.</summary>
    public static string? MarkerPath(string? steamPath) =>
        string.IsNullOrWhiteSpace(steamPath) ? null : Path.Combine(steamPath, MarkerFileName);

    public static bool MarkerExists(string? steamPath) =>
        MarkerPath(steamPath) is { } path && File.Exists(path);

    /// <summary>
    /// Creates the marker file. Steam's install folder is writable by the user in a normal install —
    /// the client updates itself in place — so this does not need administrator rights.
    /// </summary>
    public static BridgeSetupResult CreateMarker(string? steamPath)
    {
        if (MarkerPath(steamPath) is not { } path)
        {
            return new BridgeSetupResult(BridgeSetupOutcome.SteamNotFound);
        }

        if (File.Exists(path))
        {
            return new BridgeSetupResult(BridgeSetupOutcome.AlreadyEnabled);
        }

        try
        {
            File.WriteAllText(path, string.Empty);
            return new BridgeSetupResult(BridgeSetupOutcome.Enabled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new BridgeSetupResult(BridgeSetupOutcome.Failed, e.Message);
        }
    }

    public static bool IsSteamRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("steam");
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return processes.Length > 0;
        }
        catch (Exception)
        {
            // If the process list cannot be read, let the connection attempt be the judge.
            return true;
        }
    }

    /// <summary>
    /// Runs one expression inside Steam's shared context and hands back whatever string it returned.
    /// The expression is expected to catch its own errors and answer with a word.
    /// </summary>
    internal async Task<BridgeReply> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var located = await FindSharedContextAsync(cancellationToken).ConfigureAwait(false);
        if (located.Outcome != ControlOutcome.Done || located.Value is not { } webSocketUrl)
        {
            return located;
        }

        try
        {
            using var socket = new ClientWebSocket();

            // Steam's DevTools endpoint has no use for keep-alive pings on a connection this short.
            socket.Options.KeepAliveInterval = TimeSpan.Zero;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await socket.ConnectAsync(new Uri(webSocketUrl), timeout.Token).ConfigureAwait(false);

            var request = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                    userGesture = true,
                },
            });

            await socket
                .SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, timeout.Token)
                .ConfigureAwait(false);

            var answer = await ReadReplyAsync(socket, timeout.Token).ConfigureAwait(false);

            try
            {
                await socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The channel has served its purpose; an abrupt close costs nothing.
            }

            return answer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BridgeReply(ControlOutcome.Unreachable, Detail: "Cancelled.");
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or UriFormatException)
        {
            _log.Warn($"The Steam control channel dropped: {e.Message}");
            return new BridgeReply(ControlOutcome.Unreachable, Detail: e.Message);
        }
    }

    /// <summary>
    /// Tries each candidate port until one of them turns out to be Steam, and returns the shared
    /// context's socket URL in <see cref="BridgeReply.Value"/>.
    ///
    /// The failure is reported from whichever port got furthest, because "another program holds
    /// 8080" and "Steam is not listening at all" need different fixes and look identical from a
    /// request that simply did not answer.
    /// </summary>
    private async Task<BridgeReply> FindSharedContextAsync(CancellationToken cancellationToken)
    {
        var ports = Candidates().ToList();
        BridgeReply? best = null;

        foreach (var port in ports)
        {
            var attempt = await ProbePortAsync(port, cancellationToken).ConfigureAwait(false);

            if (attempt.Outcome == ControlOutcome.Done)
            {
                if (LastGoodPort != port)
                {
                    _log.Info($"Steam's control channel answered on port {port}.");
                    LastGoodPort = port;
                }

                return attempt;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return attempt;
            }

            // A port that answered with something — even the wrong something — says more than one
            // that did not answer at all.
            best = Rank(attempt, port) > Rank(best, ExpectedPort) ? attempt : best;
        }

        // Nothing worked, so whatever was remembered is stale.
        LastGoodPort = null;

        return best ?? new BridgeReply(
            IsSteamRunning() ? ControlOutcome.RestartSteam : ControlOutcome.SteamNotRunning,
            Detail: "Nothing is listening.");

        // "Another program holds the port" is only worth saying about the port Steam would have used
        // on its own. Steam's other ports — the friends service, the game overlay — answer with
        // things that are not DevTools all the time, and reporting those as a clash would send the
        // user hunting for a program that does not exist. The real answer there is a restart.
        int Rank(BridgeReply? reply, int port) => reply?.Outcome switch
        {
            ControlOutcome.Refused => 3,
            ControlOutcome.PortBusy when port == ExpectedPort => 2,
            ControlOutcome.RestartSteam => 1,
            _ => 0,
        };
    }

    /// <summary>Where Steam would be listening if nobody had interfered: 8080, or the first pinned port.</summary>
    private int ExpectedPort => _fixedPorts is { Length: > 0 } pinned ? pinned[0] : DefaultDebugPort;

    private async Task<BridgeReply> ProbePortAsync(int port, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            using var response = await _http
                .GetAsync($"http://127.0.0.1:{port}/json/list", cancellationToken)
                .ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BridgeReply(ControlOutcome.Unreachable, Detail: "Cancelled.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Nothing answered here. Either Steam is closed, or it is open but was started before
            // the marker file existed — the user cannot tell those apart, so we do it for them.
            return new BridgeReply(
                IsSteamRunning() ? ControlOutcome.RestartSteam : ControlOutcome.SteamNotRunning,
                Detail: e.Message);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new BridgeReply(ControlOutcome.PortBusy, Detail: "The port answered with something else.");
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (ReadTarget(element) is { WebSocketUrl.Length: > 0 } target && IsSharedContext(target))
                {
                    return new BridgeReply(ControlOutcome.Done, target.WebSocketUrl);
                }
            }

            // A DevTools endpoint that is not Steam's: right shape, wrong program.
            return new BridgeReply(ControlOutcome.Refused, Detail: $"{SharedContext} was not among the pages.");
        }
        catch (JsonException)
        {
            // Something is listening here and it is not a DevTools endpoint at all.
            return new BridgeReply(ControlOutcome.PortBusy, Detail: "The port is held by another program.");
        }
    }

    private static bool IsSharedContext(BridgeTarget target) =>
        target.Title.Contains(SharedContext, StringComparison.OrdinalIgnoreCase)
        || target.Url.Contains(SharedContext, StringComparison.OrdinalIgnoreCase);

    private static BridgeTarget? ReadTarget(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string Text(string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        return new BridgeTarget(Text("title"), Text("url"), Text("webSocketDebuggerUrl"));
    }

    /// <summary>Reads frames until the answer to our one request arrives.</summary>
    private static async Task<BridgeReply> ReadReplyAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        while (socket.State == WebSocketState.Open)
        {
            var text = new StringBuilder();
            WebSocketReceiveResult received;

            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return new BridgeReply(ControlOutcome.Unreachable, Detail: "Steam closed the channel.");
                }

                text.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
            }
            while (!received.EndOfMessage);

            // DevTools also pushes events nobody asked for; keep reading past them.
            if (ReadEvaluation(text.ToString()) is { } reply)
            {
                return reply;
            }
        }

        return new BridgeReply(ControlOutcome.Unreachable, Detail: "Steam closed the channel.");
    }

    /// <summary>Pulls the value out of a <c>Runtime.evaluate</c> answer, or <c>null</c> if this is not one.</summary>
    private static BridgeReply? ReadEvaluation(string frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;

            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value) || value != 1)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : "rejected";
                return new BridgeReply(ControlOutcome.Refused, Detail: message);
            }

            if (!root.TryGetProperty("result", out var outer) || outer.ValueKind != JsonValueKind.Object)
            {
                return new BridgeReply(ControlOutcome.Refused, Detail: "Steam answered with nothing.");
            }

            if (outer.TryGetProperty("exceptionDetails", out var thrown))
            {
                var message = thrown.TryGetProperty("text", out var t) ? t.GetString() : "threw";
                return new BridgeReply(ControlOutcome.Refused, Detail: message);
            }

            if (outer.TryGetProperty("result", out var inner)
                && inner.TryGetProperty("value", out var payload)
                && payload.ValueKind == JsonValueKind.String)
            {
                return new BridgeReply(ControlOutcome.Done, payload.GetString());
            }

            return new BridgeReply(ControlOutcome.Refused, Detail: "Steam answered with nothing.");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

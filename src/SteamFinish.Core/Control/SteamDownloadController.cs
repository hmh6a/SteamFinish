using System.Globalization;
using System.Runtime.Versioning;
using SteamFinish.Core.Logging;
using SteamFinish.Core.Steam;

namespace SteamFinish.Core.Control;

/// <summary>
/// Pauses and resumes Steam downloads by pressing, from the outside, the same buttons the client's
/// own Downloads page presses from the inside. See <see cref="SteamCefBridge"/> for why this is the
/// route rather than a command line or a registry key.
///
/// Xbox downloads have no equivalent — Gaming Services exposes no control surface at all — so this
/// covers Steam only, and says so rather than pretending to have paused something it cannot reach.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamDownloadController : IDownloadController, IDisposable
{
    private const string ProbeScript =
        """
        (function(){try{return ((window.SteamClient||{}).Downloads)?"ok":"no-api";}catch(e){return "error:"+e;}})()
        """;

    private readonly SteamCefBridge _bridge;
    private readonly ILog _log;
    private readonly Func<string?> _steamPath;

    /// <summary>Only one command at a time: two overlapping flips of the same switch settle at random.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly SteamRelauncher _relauncher;

    /// <param name="ports">Pins the search to these ports; only the tests do that.</param>
    public SteamDownloadController(
        ILog? log = null,
        Func<string?>? steamPath = null,
        params int[]? ports)
    {
        _log = log ?? NullLog.Instance;
        _bridge = new SteamCefBridge(_log, ports);
        _relauncher = new SteamRelauncher(_log);
        _steamPath = steamPath ?? SteamLocator.FindSteamPath;
    }

    public bool BridgeMarkerPresent => SteamCefBridge.MarkerExists(_steamPath());

    public int? ActivePort => _bridge.LastGoodPort;

    /// <summary>
    /// Restarts Steam on a free port. The marker file is written first, so a single press of the
    /// button in Settings is enough even when nothing has been set up yet.
    /// </summary>
    public async Task<RelaunchResult> RestartSteamAsync(CancellationToken cancellationToken = default)
    {
        var steamPath = _steamPath();
        EnableBridge();

        var result = await _relauncher.RestartWithControlAsync(steamPath, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            _log.Warn($"Could not restart Steam with download control: {result.Outcome} {result.Detail}");
        }

        return result;
    }

    public BridgeSetupResult EnableBridge()
    {
        var result = SteamCefBridge.CreateMarker(_steamPath());
        if (result.Outcome == BridgeSetupOutcome.Enabled)
        {
            _log.Info("Steam download control enabled; it takes effect the next time Steam starts.");
        }
        else if (result.Outcome == BridgeSetupOutcome.Failed)
        {
            _log.Warn($"Could not enable Steam download control: {result.Detail}");
        }

        return result;
    }

    public Task<ControlResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ProbeScript, cancellationToken);

    public Task<ControlResult> ApplyAsync(
        DownloadCommand command,
        uint? appId = null,
        CancellationToken cancellationToken = default)
    {
        if (appId is not { } id)
        {
            // Both calls name a game. With nothing downloading there is nothing to name, and
            // nothing to pause either.
            return Task.FromResult(ControlResult.Fail(ControlOutcome.NothingDownloading));
        }

        return RunAsync(CommandScript(command, id), cancellationToken);
    }

    /// <summary>
    /// Steam's per-game pause, which is the pair of buttons on each row of the Downloads page.
    ///
    /// Deliberately not the global <c>EnableAllDownloads</c> switch behind the "pause all" button:
    /// that one is one-way. <c>EnableAllDownloads(false)</c> does stop a running download, but
    /// nothing puts it back — not <c>EnableAllDownloads(true)</c>, not <c>ResumeAppUpdate</c>, not
    /// <c>QueueAppUpdate</c>. Tested against a live download: it stayed paused through all three and
    /// only a Steam restart cleared it. A pause that cannot be undone from the phone is worse than
    /// no pause at all, so this uses the per-game calls, which undo each other.
    /// </summary>
    private static string CommandScript(DownloadCommand command, uint appId)
    {
        var call = command == DownloadCommand.Pause ? "PauseAppUpdate" : "ResumeAppUpdate";

        // Concatenated rather than interpolated: the script is mostly braces, and every one of them
        // would have to be doubled to survive an interpolated literal.
        return """
               (function(){try{var d=(window.SteamClient||{}).Downloads;if(!d)return "no-api";
               d.
               """
               + call
               + string.Create(CultureInfo.InvariantCulture, $"({appId});")
               + """
                 return "ok";}catch(e){return "error:"+e;}})()
                 """;
    }

    private async Task<ControlResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        if (_steamPath() is not { } steamPath)
        {
            return ControlResult.Fail(ControlOutcome.SteamNotFound);
        }

        if (!SteamCefBridge.MarkerExists(steamPath))
        {
            return ControlResult.Fail(ControlOutcome.BridgeDisabled);
        }

        // Whether Steam is running is not checked up front: an endpoint that answers settles the
        // question, and the bridge looks at the process list only to explain a connection that did
        // not answer. Asking first would just be a second way to get the same answer wrong.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reply = await _bridge.EvaluateAsync(script, cancellationToken).ConfigureAwait(false);

            if (reply.Outcome != ControlOutcome.Done)
            {
                _log.Warn($"Steam download control failed ({reply.Outcome}): {reply.Detail}");
                return ControlResult.Fail(reply.Outcome, reply.Detail);
            }

            if (reply.Value == "ok")
            {
                return ControlResult.Done();
            }

            // The channel worked; Steam itself would not do it.
            _log.Warn($"Steam refused the download command: {reply.Value}");
            return ControlResult.Fail(ControlOutcome.Refused, reply.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _bridge.Dispose();
    }
}

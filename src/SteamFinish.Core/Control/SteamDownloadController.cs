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
    /// <summary>
    /// The global switch behind Steam's "pause all" button. Wrapped so a client too old to have the
    /// API answers with a word instead of throwing across the wire.
    /// </summary>
    private const string PauseScript =
        """
        (function(){try{var d=(window.SteamClient||{}).Downloads;if(!d)return "no-api";
        d.EnableAllDownloads(false);return "ok";}catch(e){return "error:"+e;}})()
        """;

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
        CancellationToken cancellationToken = default) =>
        RunAsync(command == DownloadCommand.Pause ? PauseScript : ResumeScript(appId), cancellationToken);

    /// <summary>
    /// Flipping the global switch back on is not enough on its own: a game paused by hand in Steam's
    /// UI carries its own paused flag and stays put. When the caller knows which download is at the
    /// front of the queue, that one is nudged as well.
    /// </summary>
    private static string ResumeScript(uint? appId)
    {
        var resumeApp = appId is { } id
            ? string.Create(CultureInfo.InvariantCulture, $"try{{d.ResumeAppUpdate({id});}}catch(e){{}}")
            : string.Empty;

        // Concatenated rather than interpolated: the script is mostly braces, and every one of them
        // would have to be doubled to survive an interpolated literal.
        return """
               (function(){try{var d=(window.SteamClient||{}).Downloads;if(!d)return "no-api";
               d.EnableAllDownloads(true);
               """
               + resumeApp
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

namespace SteamFinish.Core.Control;

/// <summary>What the phone asked the download to do.</summary>
public enum DownloadCommand
{
    Pause,

    Resume,
}

/// <summary>
/// How a pause or resume ended. Every failure is a different thing for the user to fix, so they are
/// kept apart rather than collapsed into a bool — the chat reply explains each one.
/// </summary>
public enum ControlOutcome
{
    /// <summary>Steam took the call.</summary>
    Done,

    /// <summary>Steam is not running, so there is nothing to talk to.</summary>
    SteamNotRunning,

    /// <summary>Steam could not be located on this PC.</summary>
    SteamNotFound,

    /// <summary>The marker file that opens Steam's control channel has not been created yet.</summary>
    BridgeDisabled,

    /// <summary>The marker file exists but Steam has not been restarted since, so the channel is shut.</summary>
    RestartSteam,

    /// <summary>
    /// Something that is not Steam is holding the port the channel would use, and Steam has no
    /// other one open. Restarting Steam on a free port is the way out.
    /// </summary>
    PortBusy,

    /// <summary>Steam answered but refused the call — a client too old to have the download API.</summary>
    Refused,

    /// <summary>The channel was open a moment ago and is not now.</summary>
    Unreachable,
}

/// <summary>The result of one pause or resume, with the raw detail kept for the log.</summary>
public sealed record ControlResult(ControlOutcome Outcome, string? Detail = null)
{
    public bool Success => Outcome == ControlOutcome.Done;

    public static ControlResult Done() => new(ControlOutcome.Done);

    public static ControlResult Fail(ControlOutcome outcome, string? detail = null) => new(outcome, detail);
}

/// <summary>What happened when the app tried to open Steam's control channel.</summary>
public enum BridgeSetupOutcome
{
    /// <summary>The marker file was already there; only a Steam restart may still be needed.</summary>
    AlreadyEnabled,

    /// <summary>The marker file has just been written. Steam has to be restarted to pick it up.</summary>
    Enabled,

    SteamNotFound,

    /// <summary>The Steam folder would not accept the file.</summary>
    Failed,
}

public sealed record BridgeSetupResult(BridgeSetupOutcome Outcome, string? Detail = null);

/// <summary>
/// Pausing and resuming downloads, kept behind an interface so the Telegram side can be tested
/// without a Steam client on the other end.
/// </summary>
public interface IDownloadController
{
    /// <summary>
    /// Carries out one command. <paramref name="appId"/> is the download currently at the front of
    /// the queue, when it is known; a resume needs it because a game paused by hand in Steam's own
    /// UI stays paused after the global switch is flipped back on.
    /// </summary>
    Task<ControlResult> ApplyAsync(
        DownloadCommand command,
        uint? appId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether the channel is open, without changing anything.</summary>
    Task<ControlResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the marker file that makes Steam open its control channel on the next start.</summary>
    BridgeSetupResult EnableBridge();

    /// <summary>True when the marker file is in place — which is not the same as the channel being open.</summary>
    bool BridgeMarkerPresent { get; }

    /// <summary>
    /// The port the channel was last reached on, or <c>null</c> when it has not been reached. The
    /// port is discovered rather than configured: Steam defaults to 8080 but can be started on any
    /// other, so the app asks Windows which ports Steam holds instead of assuming.
    /// </summary>
    int? ActivePort { get; }

    /// <summary>
    /// Closes Steam and starts it again with its control channel on a port nothing else is using.
    /// The only way out when the default port was taken at the moment Steam started.
    /// </summary>
    Task<RelaunchResult> RestartSteamAsync(CancellationToken cancellationToken = default);
}

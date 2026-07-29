namespace SteamFinish.Core.Monitoring;

/// <summary>Tuning knobs read fresh on every engine update so settings changes apply immediately.</summary>
public sealed record MonitorOptions
{
    /// <summary>How long Steam must stay quiet before the countdown starts.</summary>
    public TimeSpan ConfirmationWindow { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>The cancellable countdown shown before the power action runs.</summary>
    public TimeSpan Countdown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When true (the default) the action only arms after at least one download has been observed,
    /// so enabling monitoring on an idle machine cannot shut it down straight away.
    /// </summary>
    public bool RequireDownloadFirst { get; init; } = true;

    /// <summary>When true, a paused download no longer blocks the countdown.</summary>
    public bool IgnorePaused { get; init; }
}

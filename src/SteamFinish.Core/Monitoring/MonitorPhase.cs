namespace SteamFinish.Core.Monitoring;

public enum MonitorPhase
{
    /// <summary>Monitoring is off; nothing is watched and no action can fire.</summary>
    Disabled,

    /// <summary>Monitoring is on but no download has been seen yet, so the action stays disarmed.</summary>
    WaitingForDownload,

    /// <summary>Steam is downloading, installing, validating or has queued/paused work.</summary>
    Busy,

    /// <summary>Everything looks finished; waiting out the confirmation window before committing.</summary>
    Confirming,

    /// <summary>The confirmation window elapsed; the cancellable countdown is running.</summary>
    Countdown,

    /// <summary>The countdown reached zero and the power action has been handed off.</summary>
    Executing,

    /// <summary>Steam's state could not be read, so "finished" cannot be concluded.</summary>
    Blocked,
}

public enum CountdownCancelReason
{
    User,
    NewActivity,
    StateUnavailable,
}

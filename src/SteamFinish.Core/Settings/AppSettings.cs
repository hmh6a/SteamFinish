using SteamFinish.Core.Localization;
using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Notifications;
using SteamFinish.Core.Power;

namespace SteamFinish.Core.Settings;

/// <summary>Everything the user can configure, persisted as JSON next to the log file.</summary>
public sealed class AppSettings
{
    public const int MinCountdownSeconds = 5;
    public const int MaxCountdownSeconds = 3600;
    public const int MinConfirmationSeconds = 10;
    public const int MaxConfirmationSeconds = 900;

    /// <summary>What to do once every download has finished.</summary>
    public PowerAction Action { get; set; } = PowerAction.Shutdown;

    /// <summary>Length of the cancellable countdown shown before the action runs.</summary>
    public int CountdownSeconds { get; set; } = 60;

    /// <summary>How long Steam must stay quiet before the countdown starts (the 30–60 s safety wait).</summary>
    public int ConfirmationSeconds { get; set; } = 45;

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public bool TrayNotifications { get; set; } = true;

    public bool SoundNotification { get; set; } = true;

    /// <summary>Watch Steam downloads.</summary>
    public bool WatchSteam { get; set; } = true;

    /// <summary>Watch Xbox app / Microsoft Store game installs.</summary>
    public bool WatchXbox { get; set; } = true;

    public bool AutoDetectLibraries { get; set; } = true;

    /// <summary>Extra library roots (folders that contain <c>steamapps</c>) added by hand.</summary>
    public List<string> ManualLibraries { get; set; } = [];

    /// <summary>Require at least one observed download before the action can arm.</summary>
    public bool RequireDownloadBeforeAction { get; set; } = true;

    /// <summary>Treat a paused download as "finished" instead of waiting for it.</summary>
    public bool IgnorePausedDownloads { get; set; }

    /// <summary>Pass <c>/f</c> to shutdown.exe, closing apps without saving.</summary>
    public bool ForceCloseApps { get; set; }

    /// <summary>Closing the window hides it to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool EnableLogging { get; set; } = true;

    /// <summary>Seconds between disk scans. One second keeps the progress read-out live; scans are cached.</summary>
    public int PollIntervalSeconds { get; set; } = 1;

    /// <summary>Keep reading Steam's state while the window is open, even with monitoring off.</summary>
    public bool LiveStatusWhileOpen { get; set; } = true;

    /// <summary>Language of the app's own interface, independent of the Telegram message language.</summary>
    public UiLanguage Language { get; set; } = UiLanguage.English;

    /// <summary>Light, dark, or whatever Windows itself is set to.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// How long the counters must sit still before the download is reported as paused. Generous by
    /// default because Steam rewrites its manifests in bursts, not continuously.
    /// </summary>
    public int StalledAfterSeconds { get; set; } = 60;

    public TelegramOptions Telegram { get; set; } = new();

    public MonitorOptions ToMonitorOptions() => new()
    {
        ConfirmationWindow = TimeSpan.FromSeconds(ConfirmationSeconds),
        Countdown = TimeSpan.FromSeconds(CountdownSeconds),
        RequireDownloadFirst = RequireDownloadBeforeAction,
        IgnorePaused = IgnorePausedDownloads,
    };

    /// <summary>Clamps values that may have been edited by hand in the JSON file.</summary>
    public AppSettings Normalize()
    {
        CountdownSeconds = Math.Clamp(CountdownSeconds, MinCountdownSeconds, MaxCountdownSeconds);
        ConfirmationSeconds = Math.Clamp(ConfirmationSeconds, MinConfirmationSeconds, MaxConfirmationSeconds);
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 1, 60);
        StalledAfterSeconds = Math.Clamp(StalledAfterSeconds, 15, 900);
        ManualLibraries = ManualLibraries
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!Enum.IsDefined(Action))
        {
            Action = PowerAction.Shutdown;
        }

        if (!Enum.IsDefined(Language))
        {
            Language = UiLanguage.English;
        }

        if (!Enum.IsDefined(Theme))
        {
            Theme = AppTheme.System;
        }

        Telegram = (Telegram ?? new TelegramOptions()).Normalize();
        return this;
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ManualLibraries = [.. ManualLibraries];
        copy.Telegram = Telegram.Clone();
        return copy;
    }
}

namespace SteamFinish.Core.Steam;

/// <summary>An immutable read of Steam's download state at one moment.</summary>
public sealed record SteamSnapshot
{
    public static SteamSnapshot Unavailable(DateTimeOffset takenAt, string error) =>
        new() { TakenAt = takenAt, Error = error };

    public required DateTimeOffset TakenAt { get; init; }

    public IReadOnlyList<AppActivity> Apps { get; init; } = [];

    public IReadOnlyList<string> LibraryRoots { get; init; } = [];

    public bool SteamRunning { get; init; }

    /// <summary>A <c>steamapps\downloading</c> or workshop download folder still holds live content.</summary>
    public bool DownloadFolderBusy { get; init; }

    /// <summary>
    /// The app whose byte counters were last seen to grow, stamped in by the transfer meter.
    /// Steam's own flags do not distinguish the running download from the queue behind it — both
    /// commonly read <c>UpdateRequired|UpdateStarted</c> — so movement over time is the real signal.
    /// </summary>
    public uint? ActiveAppId { get; init; }

    /// <summary>
    /// True when the current download's counters have stopped moving. Steam leaves the StateFlags
    /// unchanged when a download is paused — a paused Khazan still reads 1026, exactly like an
    /// active one — so this is stamped in by the transfer meter rather than read from the manifest.
    /// </summary>
    public bool ActiveStalled { get; init; }

    /// <summary>Set when the scan could not be trusted (no libraries, unreadable folders, …).</summary>
    public string? Error { get; init; }

    /// <summary>Only a reliable snapshot may be used to decide that everything has finished.</summary>
    public bool IsReliable => Error is null && LibraryRoots.Count > 0;

    /// <summary>Everything with work left, in no particular order.</summary>
    public IEnumerable<AppActivity> Outstanding => Apps.Where(a => a.IsOutstanding);

    public IEnumerable<AppActivity> PausedApps => Apps.Where(a => a.IsPaused);

    /// <summary>
    /// How recently Steam must have rewritten a manifest for the download to count as moving, before
    /// any movement has actually been measured. It covers the gap right after launch.
    /// </summary>
    private static readonly TimeSpan FreshManifestWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The app Steam is working on, or was last working on. Note this says nothing about whether it
    /// is moving — a paused download is still the current one.
    /// </summary>
    public AppActivity? Current
    {
        get
        {
            var outstanding = Outstanding.ToList();
            if (outstanding.Count == 0)
            {
                return null;
            }

            if (ActiveAppId is { } id
                && outstanding.FirstOrDefault(a => a.AppId == id) is { } measured)
            {
                return measured;
            }

            return outstanding.FirstOrDefault(a => a.HasJobFlags) ?? GuessLive(outstanding);
        }
    }

    public bool IsCurrent(AppActivity app) => Current?.AppId == app.AppId;

    /// <summary>
    /// True when the current download is not moving. Either the meter watched it stand still, or —
    /// before anything has been measured — Steam has not rewritten its manifest for a while, which
    /// is how a download that was already paused at startup is recognised.
    /// </summary>
    public bool IsStopped =>
        Current is { } current
        && (ActiveStalled
            || current.IsPaused
            || (ActiveAppId is null
                && !current.HasJobFlags
                && TakenAt - current.ManifestWrittenAt >= FreshManifestWindow));

    /// <summary>True for the one app Steam is currently moving bytes for.</summary>
    public bool IsLive(AppActivity app) => IsCurrent(app) && !IsStopped;

    /// <summary>The current download first, then everything waiting behind it.</summary>
    public IReadOnlyList<AppActivity> Pipeline
    {
        get
        {
            var outstanding = Outstanding.ToList();
            if (outstanding.Count == 0)
            {
                return [];
            }

            var current = Current;

            return
            [
                .. current is null ? Array.Empty<AppActivity>() : [current],
                .. outstanding
                    .Where(a => a != current)
                    .OrderByDescending(a => a.IsPaused ? 0 : 1)
                    .ThenByDescending(a => a.BytesRemaining),
            ];
        }
    }

    /// <summary>The app to show in the UI: whatever Steam is actually working on.</summary>
    public AppActivity? Headline => Pipeline.FirstOrDefault();

    /// <summary>Everything behind the current download — the "up next" list, excluding the live one.</summary>
    public IReadOnlyList<AppActivity> Waiting => [.. Pipeline.Skip(1)];

    /// <summary>True when the current download is sitting still, whether paused by hand or stalled.</summary>
    public bool IsPausedOrStalled(AppActivity app) => IsCurrent(app) && IsStopped;

    public long TotalDownloadBytesRemaining => Outstanding.Sum(a => a.DownloadBytesRemaining);

    public long TotalBytesToDownload => Outstanding.Sum(a => a.BytesToDownload);

    public long TotalBytesDownloaded => Outstanding.Sum(a => a.BytesDownloaded);

    /// <summary>Completion across the whole queue, weighted by download size.</summary>
    public double? QueueProgress =>
        TotalBytesToDownload > 0
            ? Math.Clamp((double)TotalBytesDownloaded / TotalBytesToDownload, 0, 1)
            : null;

    /// <summary>True while Steam still has downloading, installing, queued or paused work.</summary>
    public bool HasPendingWork(bool ignorePaused = false)
    {
        if (DownloadFolderBusy)
        {
            return true;
        }

        foreach (var app in Apps)
        {
            if (app.HasJobFlags || app.IsQueued)
            {
                return true;
            }

            if (app.IsPaused && !ignorePaused)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Used until the meter has seen the counters move — right after launch, for instance. Steam
    /// rewrites the live download's manifest constantly, so the freshest one that still has bytes
    /// outstanding is the best available guess.
    /// </summary>
    private static AppActivity? GuessLive(List<AppActivity> outstanding) =>
        outstanding
            .Where(a => !a.IsPaused && a.HasIncompleteBytes)
            .OrderByDescending(a => a.ManifestWrittenAt)
            .ThenByDescending(a => a.BytesDownloaded)
            .FirstOrDefault();
}

namespace SteamFinish.Core.Steam;

/// <summary>What a single installed/queued app is currently doing, read from its app manifest.</summary>
public sealed record AppActivity
{
    /// <summary>Flags that name a specific job Steam is carrying out.</summary>
    public const AppStateFlags WorkingMask =
        AppStateFlags.UpdateRunning
        | AppStateFlags.Uninstalling
        | AppStateFlags.BackupRunning
        | AppStateFlags.Reconfiguring
        | AppStateFlags.Validating
        | AppStateFlags.AddingFiles
        | AppStateFlags.Preallocating
        | AppStateFlags.Downloading
        | AppStateFlags.Staging
        | AppStateFlags.Committing
        | AppStateFlags.UpdateStopping;

    /// <summary>Flags that mean an update exists for this app, whether or not it is running yet.</summary>
    public const AppStateFlags UpdateWantedMask =
        AppStateFlags.UpdateRequired | AppStateFlags.UpdateStarted | AppStateFlags.UpdateRunning;

    /// <summary>
    /// Flags that mean the app is not in a clean, settled "installed and done" state.
    /// Leftover byte counters only count as unfinished work when one of these is also set,
    /// otherwise stale counters on finished games would block the countdown forever.
    /// </summary>
    public const AppStateFlags UnsettledMask =
        WorkingMask
        | AppStateFlags.UpdateRequired
        | AppStateFlags.UpdateStarted
        | AppStateFlags.UpdatePaused
        | AppStateFlags.FilesMissing
        | AppStateFlags.FilesCorrupt;

    public required uint AppId { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// Which launcher this came from. Xbox downloads are translated into the same flags and byte
    /// counters as Steam's, so everything downstream — the meter, the engine, the UI — is unchanged.
    /// </summary>
    public GamePlatform Platform { get; init; } = GamePlatform.Steam;

    public required AppStateFlags State { get; init; }

    /// <summary>Compressed bytes pulled from the network so far.</summary>
    public long BytesDownloaded { get; init; }

    public long BytesToDownload { get; init; }

    /// <summary>Bytes written to disk so far; this is what Steam shows as "Installing files".</summary>
    public long BytesStaged { get; init; }

    public long BytesToStage { get; init; }

    public string LibraryPath { get; init; } = string.Empty;

    /// <summary>
    /// When Steam last rewrote this manifest. The live download is rewritten continuously while the
    /// rest of the queue goes stale, which is how the current download is picked out before its
    /// byte counters have been seen to move.
    /// </summary>
    public DateTimeOffset ManifestWrittenAt { get; init; }

    /// <summary>A paused update is explicitly not a finished download.</summary>
    public bool IsPaused => (State & AppStateFlags.UpdatePaused) != 0;

    /// <summary>
    /// Steam has named a job for this app. Note that Steam often runs a download with none of these
    /// set — a queued and a downloading game can both read 1026 — so this is a positive signal only,
    /// never proof that an app is idle. Which app is live is decided in <see cref="DownloadSnapshot"/>.
    /// </summary>
    public bool HasJobFlags => !IsPaused && (State & WorkingMask) != 0;

    /// <summary>Outstanding work with no named job running.</summary>
    public bool IsQueued =>
        !IsPaused
        && !HasJobFlags
        && ((State & AppStateFlags.UpdateStarted) != 0
            || (HasIncompleteBytes && (State & UnsettledMask) != 0));

    public bool HasIncompleteBytes =>
        (BytesToDownload > 0 && BytesDownloaded < BytesToDownload)
        || (BytesToStage > 0 && BytesStaged < BytesToStage);

    /// <summary>Outstanding work that is not running: queued items and paused updates.</summary>
    public bool IsPending => IsQueued || IsPaused;

    /// <summary>Anything with work left to do, whichever of the three states it is in.</summary>
    public bool IsOutstanding => HasJobFlags || IsQueued || IsPaused;

    /// <summary>True once the bytes are down and Steam is only writing them out.</summary>
    public bool IsInstalling =>
        BytesToDownload > 0
        && BytesDownloaded >= BytesToDownload
        && BytesToStage > 0
        && BytesStaged < BytesToStage;

    public bool IsValidating => (State & AppStateFlags.Validating) != 0;

    /// <summary>Network transfer completion in the range 0..1 — Steam's "Downloading data" bar.</summary>
    public double? DownloadProgress =>
        BytesToDownload > 0 ? Math.Clamp((double)BytesDownloaded / BytesToDownload, 0, 1) : null;

    /// <summary>Disk write completion in the range 0..1 — Steam's "Installing files" bar.</summary>
    public double? InstallProgress =>
        BytesToStage > 0 ? Math.Clamp((double)BytesStaged / BytesToStage, 0, 1) : null;

    /// <summary>
    /// The single figure Steam shows next to a game in the download queue. It is the staged share,
    /// not the downloaded share, which is why a game can read 4% while 9% of its bytes have arrived.
    /// </summary>
    public double? Progress => InstallProgress ?? DownloadProgress;

    public long DownloadBytesRemaining => Math.Max(0, BytesToDownload - BytesDownloaded);

    public long InstallBytesRemaining => Math.Max(0, BytesToStage - BytesStaged);

    /// <summary>Used to rank the queue; the download side is what actually costs time.</summary>
    public long BytesRemaining => DownloadBytesRemaining;
}

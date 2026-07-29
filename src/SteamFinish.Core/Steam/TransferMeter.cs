namespace SteamFinish.Core.Steam;

/// <summary>
/// Derives live transfer rates from consecutive snapshots. Steam rewrites its manifests in bursts
/// rather than continuously, so a rate is measured across the gap since the counters last moved,
/// not across the polling interval — otherwise the reading would flip between zero and a spike.
/// </summary>
public sealed class TransferMeter
{
    /// <summary>Weight given to a fresh sample; the rest carries over, which steadies the display.</summary>
    private const double Smoothing = 0.4;

    /// <summary>After this long without movement the transfer is reported as stalled.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(25);

    private Dictionary<uint, Counters> _previous = [];
    private DateTimeOffset? _previousAt;
    private DateTimeOffset? _lastChangeAt;

    /// <summary>
    /// The app whose counters were last seen to grow. This is how the running download is told apart
    /// from the queue, because Steam's StateFlags do not distinguish them.
    /// </summary>
    public uint? ActiveAppId { get; private set; }

    /// <summary>Compressed bytes per second arriving from the network.</summary>
    public double NetworkBytesPerSecond { get; private set; }

    /// <summary>The best network rate seen since the last reset.</summary>
    public double PeakNetworkBytesPerSecond { get; private set; }

    /// <summary>Bytes per second being written to disk while Steam installs the files.</summary>
    public double DiskBytesPerSecond { get; private set; }

    /// <summary>Time left for everything still queued, or <c>null</c> while the rate is unknown.</summary>
    public TimeSpan? Eta { get; private set; }

    /// <summary>When any counter was last seen to grow, or <c>null</c> if none ever has.</summary>
    public DateTimeOffset? LastMovementAt { get; private set; }

    public bool HasReading => _lastChangeAt is not null;

    /// <summary>
    /// True when the current download has stopped moving. Steam does not set <c>UpdatePaused</c>
    /// when the user pauses — the flags read the same as an active download — so a pause can only
    /// be told from the counters standing still.
    /// </summary>
    public bool IsStalled(DateTimeOffset now, TimeSpan threshold) =>
        LastMovementAt is { } moved && now - moved > threshold;

    public void Observe(SteamSnapshot snapshot)
    {
        if (!snapshot.IsReliable)
        {
            return;
        }

        var now = snapshot.TakenAt;
        var current = snapshot.Outstanding.ToDictionary(
            app => app.AppId,
            app => new Counters(app.BytesDownloaded, app.BytesStaged));

        // A game that has left the queue can no longer be the live one.
        if (ActiveAppId is { } active && !current.ContainsKey(active))
        {
            ActiveAppId = null;
        }

        if (_previousAt is null)
        {
            // Anchor the first measurement window so the next change is not divided by a
            // single poll interval and reported several times too fast.
            _previous = current;
            _previousAt = now;
            _lastChangeAt = now;

            // Anchored, not observed: a download that is already paused when the app starts is
            // reported as stalled once the threshold passes, rather than staying "unknown" forever.
            LastMovementAt = now;
            return;
        }

        var (downloadedDelta, stagedDelta) = Delta(current);

        if (downloadedDelta > 0 || stagedDelta > 0)
        {
            var window = (now - (_lastChangeAt ?? _previousAt.Value)).TotalSeconds;
            if (window >= 0.05)
            {
                Blend(downloadedDelta / window, stagedDelta / window);
            }

            _lastChangeAt = now;
            LastMovementAt = now;
        }
        else if (_lastChangeAt is { } lastChange && now - lastChange > StaleAfter)
        {
            NetworkBytesPerSecond = 0;
            DiskBytesPerSecond = 0;
        }

        _previous = current;
        _previousAt = now;
        Eta = EstimateEta(snapshot);
    }

    public void Reset()
    {
        _previous = [];
        _previousAt = null;
        _lastChangeAt = null;
        LastMovementAt = null;
        ActiveAppId = null;
        NetworkBytesPerSecond = 0;
        PeakNetworkBytesPerSecond = 0;
        DiskBytesPerSecond = 0;
        Eta = null;
    }

    /// <summary>Forgets the peak only; used when a new batch of downloads begins.</summary>
    public void ResetPeak() => PeakNetworkBytesPerSecond = NetworkBytesPerSecond;

    private (long Downloaded, long Staged) Delta(Dictionary<uint, Counters> current)
    {
        long downloaded = 0;
        long staged = 0;
        long bestMovement = 0;
        uint? mover = null;

        foreach (var (appId, counters) in current)
        {
            if (!_previous.TryGetValue(appId, out var before))
            {
                continue;
            }

            // Only growth counts: Steam zeroes the counters when a game leaves the queue.
            var downloadedDelta = Math.Max(0, counters.Downloaded - before.Downloaded);
            var stagedDelta = Math.Max(0, counters.Staged - before.Staged);
            downloaded += downloadedDelta;
            staged += stagedDelta;

            // Whichever app moved the most bytes is the one Steam is working on.
            var movement = downloadedDelta + stagedDelta;
            if (movement > bestMovement)
            {
                bestMovement = movement;
                mover = appId;
            }
        }

        if (mover is not null)
        {
            ActiveAppId = mover;
        }

        return (downloaded, staged);
    }

    private void Blend(double network, double disk)
    {
        NetworkBytesPerSecond = (NetworkBytesPerSecond * (1 - Smoothing)) + (network * Smoothing);
        DiskBytesPerSecond = (DiskBytesPerSecond * (1 - Smoothing)) + (disk * Smoothing);

        if (NetworkBytesPerSecond > PeakNetworkBytesPerSecond)
        {
            PeakNetworkBytesPerSecond = NetworkBytesPerSecond;
        }
    }

    private TimeSpan? EstimateEta(SteamSnapshot snapshot)
    {
        var remaining = snapshot.TotalDownloadBytesRemaining;
        if (remaining <= 0 || NetworkBytesPerSecond < 1024)
        {
            return null;
        }

        var seconds = remaining / NetworkBytesPerSecond;
        return seconds > TimeSpan.MaxValue.TotalSeconds
            ? null
            : TimeSpan.FromSeconds(seconds);
    }

    private readonly record struct Counters(long Downloaded, long Staged);
}

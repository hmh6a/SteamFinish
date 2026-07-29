using SteamFinish.Core.Steam;

namespace SteamFinish.Core.Monitoring;

/// <summary>One game that took part in a download session.</summary>
public sealed record SessionGame(uint AppId, string Name, long BytesToDownload, long BytesToStage);

/// <summary>What finished, how big it was and how long it took — the basis of the finish message.</summary>
public sealed record DownloadSummary(
    TimeSpan Duration,
    IReadOnlyList<SessionGame> Games,
    long TotalDownloadBytes,
    long TotalInstallBytes)
{
    public double AverageBytesPerSecond =>
        Duration.TotalSeconds > 1 ? TotalDownloadBytes / Duration.TotalSeconds : 0;
}

/// <summary>
/// Remembers everything Steam worked on between the first download appearing and the queue running
/// dry, so the notification can say what finished, how large it was and how long it took.
/// </summary>
public sealed class DownloadSession
{
    private readonly Dictionary<uint, SessionGame> _games = [];

    public DateTimeOffset? StartedAt { get; private set; }

    public bool IsRunning => StartedAt is not null;

    public IReadOnlyCollection<SessionGame> Games => _games.Values;

    /// <summary>Folds a snapshot in. Snapshots with no outstanding work are ignored.</summary>
    public void Observe(SteamSnapshot snapshot)
    {
        if (!snapshot.IsReliable)
        {
            return;
        }

        var pipeline = snapshot.Pipeline;
        if (pipeline.Count == 0)
        {
            return;
        }

        StartedAt ??= snapshot.TakenAt;

        foreach (var app in pipeline)
        {
            // Keep the largest sizes seen: Steam fills the totals in only once it has the manifest.
            _games[app.AppId] = _games.TryGetValue(app.AppId, out var existing)
                ? existing with
                {
                    Name = app.Name,
                    BytesToDownload = Math.Max(existing.BytesToDownload, app.BytesToDownload),
                    BytesToStage = Math.Max(existing.BytesToStage, app.BytesToStage),
                }
                : new SessionGame(app.AppId, app.Name, app.BytesToDownload, app.BytesToStage);
        }
    }

    /// <summary>Builds the summary for the session, or <c>null</c> when nothing was ever downloaded.</summary>
    public DownloadSummary? Summarize(DateTimeOffset now)
    {
        if (StartedAt is not { } startedAt || _games.Count == 0)
        {
            return null;
        }

        var games = _games.Values.OrderByDescending(g => g.BytesToDownload).ToArray();
        return new DownloadSummary(
            now - startedAt,
            games,
            games.Sum(g => g.BytesToDownload),
            games.Sum(g => g.BytesToStage));
    }

    public void Reset()
    {
        _games.Clear();
        StartedAt = null;
    }
}

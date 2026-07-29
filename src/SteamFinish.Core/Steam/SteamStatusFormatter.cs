using SteamFinish.Core.Formatting;
using SteamFinish.Core.Localization;

namespace SteamFinish.Core.Steam;

/// <summary>The human-readable form of a snapshot, as shown in the window and the tray tooltip.</summary>
public sealed record SteamStatusText(string Headline, string Detail, double? Progress);

public static class SteamStatusFormatter
{
    public static SteamStatusText Describe(SteamSnapshot snapshot)
    {
        if (!snapshot.IsReliable)
        {
            return new SteamStatusText(
                Loc.Get("Status.Unavailable"),
                snapshot.Error ?? Loc.Get("Status.NoLibraries"),
                null);
        }

        var app = snapshot.Headline;

        if (app is not null && snapshot.IsPausedOrStalled(app))
        {
            return new SteamStatusText(
                Loc.F("Status.Paused", NameWithPercent(app)),
                Loc.Get("Status.PausedDetail"),
                app.Progress);
        }

        if (app is not null && snapshot.IsLive(app))
        {
            var key = app.IsValidating ? "Status.Validating"
                : app.IsInstalling ? "Status.Installing"
                : "Status.Downloading";

            return new SteamStatusText(
                Loc.F(key, NameWithPercent(app)),
                DescribeQueue(snapshot, app),
                app.Progress);
        }

        if (app is not null)
        {
            return new SteamStatusText(
                Loc.F("Status.Queued", Loc.Ltr(app.Name)),
                DescribeQueue(snapshot, app),
                app.Progress);
        }

        if (snapshot.DownloadFolderBusy)
        {
            return new SteamStatusText(
                Loc.Get("Status.WritingFiles"),
                Loc.Get("Status.WritingDetail"),
                null);
        }

        var libraries = snapshot.LibraryRoots.Count == 1
            ? Loc.Get("Status.OneLibraryWatched")
            : Loc.F("Status.LibrariesWatched", snapshot.LibraryRoots.Count);

        return new SteamStatusText(
            Loc.Get("Status.NoDownloads"),
            Loc.F(snapshot.SteamRunning ? "Status.SteamIdle" : "Status.SteamNotRunning", libraries),
            null);
    }

    /// <summary>"Khazan (76%)" kept together as one left-to-right run.</summary>
    private static string NameWithPercent(AppActivity app) =>
        Loc.Ltr($"{app.Name} ({Humanize.Percent(app.Progress)})");

    private static string DescribeQueue(SteamSnapshot snapshot, AppActivity headline)
    {
        var others = snapshot.Pipeline.Count(a => a.AppId != headline.AppId);
        var parts = new List<string>(2);

        if (headline.BytesToDownload > 0)
        {
            parts.Add(Loc.F(
                "Status.OfTotal",
                Loc.Ltr(Humanize.Bytes(headline.BytesDownloaded)),
                Loc.Ltr(Humanize.Bytes(headline.BytesToDownload))));
        }

        if (others > 0)
        {
            parts.Add(Loc.F("Status.MoreInQueue", others));
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.Get("Status.FinishingUp");
    }
}

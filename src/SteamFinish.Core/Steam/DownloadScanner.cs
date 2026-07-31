using System.Runtime.Versioning;
using SteamFinish.Core.Xbox;

namespace SteamFinish.Core.Steam;

/// <summary>
/// Merges every watched launcher into a single snapshot. Downstream — the transfer meter, the
/// monitor engine, the whole UI — nothing knows or cares which platform a download came from.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DownloadScanner(
    SteamScanner steam,
    XboxScanner xbox,
    Func<bool> watchSteam,
    Func<bool> watchXbox)
{
    public DownloadSnapshot Scan(DateTimeOffset now)
    {
        var wantSteam = watchSteam();
        var wantXbox = watchXbox();

        if (!wantSteam && !wantXbox)
        {
            return DownloadSnapshot.Unavailable(now, "No launcher is being watched.");
        }

        var steamSnapshot = wantSteam ? steam.Scan(now) : null;
        var xboxResult = wantXbox ? xbox.Scan(now) : null;

        var apps = new List<AppActivity>();
        if (steamSnapshot is { IsReliable: true })
        {
            apps.AddRange(steamSnapshot.Apps);
        }

        if (xboxResult is { Available: true })
        {
            apps.AddRange(xboxResult.Apps);
        }

        var steamOk = steamSnapshot is { IsReliable: true };
        var xboxOk = xboxResult is { Available: true };

        // Unreliable only when nothing that was asked for could be read. One launcher answering is
        // enough to make a decision about it; a launcher that cannot be read is reported instead.
        if (!steamOk && !xboxOk)
        {
            return DownloadSnapshot.Unavailable(
                now,
                steamSnapshot?.Error ?? xboxResult?.Error ?? "No launcher could be read.");
        }

        return new DownloadSnapshot
        {
            TakenAt = now,
            Apps = apps,
            LibraryRoots = steamOk ? steamSnapshot!.LibraryRoots : [],
            SteamRunning = steamSnapshot?.SteamRunning ?? false,
            DownloadFolderBusy = steamSnapshot?.DownloadFolderBusy ?? false,
            HasXboxSource = xboxOk,

            // A watched launcher that failed is surfaced as a warning, not as an outage, as long as
            // the other one answered.
            Warning = steamOk ? xboxResult?.Error : steamSnapshot?.Error,
        };
    }
}

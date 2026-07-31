using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using SteamFinish.Core.Logging;
using SteamFinish.Core.Vdf;

namespace SteamFinish.Core.Steam;

/// <summary>
/// Reads Steam's on-disk state into a <see cref="DownloadSnapshot"/>. Parsed manifests are cached
/// by write time so repeated polling stays cheap.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamScanner(ILibrarySource libraries, ILog? log = null)
{
    /// <summary>How recently a download folder must have changed to count as live activity.</summary>
    private static readonly TimeSpan RecentActivityWindow = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ProcessProbeInterval = TimeSpan.FromSeconds(3);

    private readonly ILog _log = log ?? NullLog.Instance;
    private readonly Dictionary<string, CachedManifest> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null means "never probed"; subtracting a sentinel tick value would overflow.</summary>
    private long? _lastProcessProbeTicks;

    private bool _steamRunning;

    public DownloadSnapshot Scan(DateTimeOffset now)
    {
        var roots = libraries.GetLibraryRoots();
        if (roots.Count == 0)
        {
            return DownloadSnapshot.Unavailable(now, "No Steam library folder was found.");
        }

        var apps = new List<AppActivity>();
        var byAppId = new Dictionary<uint, AppActivity>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readable = 0;
        string? failure = null;

        foreach (var root in roots)
        {
            var steamApps = Path.Combine(root, "steamapps");
            try
            {
                foreach (var file in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                {
                    touched.Add(file);
                    var activity = ReadManifest(file, root);
                    if (activity is null)
                    {
                        continue;
                    }

                    apps.Add(activity);
                    byAppId[activity.AppId] = activity;
                }

                readable++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failure = $"Cannot read '{steamApps}': {e.Message}";
                _log.Warn(failure);
            }
        }

        PruneCache(touched);

        if (readable == 0)
        {
            return DownloadSnapshot.Unavailable(now, failure ?? "No Steam library folder could be read.");
        }

        var folderBusy = false;
        foreach (var root in roots)
        {
            folderBusy |= IsDownloadFolderBusy(root, byAppId, now);
        }

        return new DownloadSnapshot
        {
            TakenAt = now,
            Apps = apps,
            LibraryRoots = roots,
            SteamRunning = IsSteamRunning(),
            DownloadFolderBusy = folderBusy,
            // A library we could not read means "unknown", never "finished".
            Error = failure,
        };
    }

    /// <summary>Parses one <c>appmanifest_*.acf</c>, reusing the cached result when unchanged.</summary>
    private AppActivity? ReadManifest(string path, string libraryRoot)
    {
        try
        {
            var info = new FileInfo(path);
            if (_cache.TryGetValue(path, out var cached)
                && cached.WriteTimeUtc == info.LastWriteTimeUtc
                && cached.Length == info.Length)
            {
                return cached.Activity;
            }

            var state = VdfParser.ParseFile(path).Unwrap("AppState");
            var appIdText = state.GetString("appid") ?? state.GetString("AppID");
            if (!uint.TryParse(appIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId))
            {
                return null;
            }

            var activity = new AppActivity
            {
                AppId = appId,
                Name = state.GetString("name") is { Length: > 0 } name ? name : $"App {appId}",
                State = (AppStateFlags)state.GetInt64("StateFlags"),
                BytesDownloaded = state.GetInt64("BytesDownloaded"),
                BytesToDownload = state.GetInt64("BytesToDownload"),
                BytesStaged = state.GetInt64("BytesStaged"),
                BytesToStage = state.GetInt64("BytesToStage"),
                LibraryPath = libraryRoot,
                ManifestWrittenAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            };

            _cache[path] = new CachedManifest(info.LastWriteTimeUtc, info.Length, activity);
            return activity;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Steam rewrites manifests in place; a transient sharing violation is expected.
            return _cache.TryGetValue(path, out var stale) ? stale.Activity : null;
        }
        catch (Exception e)
        {
            _log.Warn($"Failed to parse '{path}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks <c>steamapps\downloading</c> and the workshop download folder. Leftovers from a crash
    /// are ignored unless a matching manifest is unsettled or the content changed recently.
    /// </summary>
    private bool IsDownloadFolderBusy(string root, IReadOnlyDictionary<uint, AppActivity> byAppId, DateTimeOffset now)
    {
        var downloading = Path.Combine(root, "steamapps", "downloading");
        try
        {
            if (Directory.Exists(downloading))
            {
                foreach (var directory in Directory.EnumerateDirectories(downloading))
                {
                    var name = Path.GetFileName(directory);
                    if (!uint.TryParse(name, out var appId))
                    {
                        continue;
                    }

                    if (byAppId.TryGetValue(appId, out var app))
                    {
                        if ((app.State & AppActivity.UnsettledMask) != 0)
                        {
                            return true;
                        }
                    }
                    else if (ChangedRecently(directory, now))
                    {
                        // A download that started before its manifest was written.
                        return true;
                    }
                }

                foreach (var patch in Directory.EnumerateFiles(downloading, "state_*.patch", SearchOption.TopDirectoryOnly))
                {
                    if (ChangedRecently(patch, now))
                    {
                        return true;
                    }
                }
            }

            var workshop = Path.Combine(root, "steamapps", "workshop", "downloads");
            if (Directory.Exists(workshop)
                && Directory.EnumerateFileSystemEntries(workshop).Any(entry => ChangedRecently(entry, now)))
            {
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Cannot inspect download folders under '{root}': {e.Message}");
        }

        return false;
    }

    private static bool ChangedRecently(string path, DateTimeOffset now)
    {
        try
        {
            var writeTime = Directory.Exists(path)
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
            return now - new DateTimeOffset(writeTime, TimeSpan.Zero) < RecentActivityWindow;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsSteamRunning()
    {
        var now = Environment.TickCount64;
        if (_lastProcessProbeTicks is { } probedAt && now - probedAt < ProcessProbeInterval.TotalMilliseconds)
        {
            return _steamRunning;
        }

        _lastProcessProbeTicks = now;
        try
        {
            var processes = Process.GetProcessesByName("steam");
            _steamRunning = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        catch (Exception e)
        {
            _log.Warn($"Cannot enumerate processes: {e.Message}");
        }

        return _steamRunning;
    }

    private void PruneCache(HashSet<string> stillPresent)
    {
        if (_cache.Count <= stillPresent.Count)
        {
            return;
        }

        foreach (var key in _cache.Keys.Where(k => !stillPresent.Contains(k)).ToList())
        {
            _cache.Remove(key);
        }
    }

    private readonly record struct CachedManifest(DateTime WriteTimeUtc, long Length, AppActivity Activity);
}

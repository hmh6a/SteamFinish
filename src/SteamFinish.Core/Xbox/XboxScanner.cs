using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Win32;
using SteamFinish.Core.Logging;
using SteamFinish.Core.Steam;

namespace SteamFinish.Core.Xbox;

public sealed record XboxScanResult(bool Available, IReadOnlyList<AppActivity> Apps, string? Error);

/// <summary>Where the checkpoints come from; substituted in tests so no registry is needed.</summary>
public interface IXboxCheckpointSource
{
    /// <summary>Value name to JSON payload, or <c>null</c> when Gaming Services is not installed.</summary>
    IReadOnlyDictionary<string, string>? Read();
}

/// <summary>
/// Reads Gaming Services' streaming checkpoints from the registry. Each in-flight Xbox download has
/// one, carrying its total and streamed byte counts — the same numbers the Xbox app itself shows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryCheckpointSource : IXboxCheckpointSource
{
    private const string CheckpointsKey = @"SOFTWARE\Microsoft\GamingServices\StreamingCheckpoints";

    public IReadOnlyDictionary<string, string>? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CheckpointsKey);
            if (key is null)
            {
                return null;
            }

            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string json && json.Length > 0)
                {
                    entries[name] = json;
                }
            }

            return entries;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// Turns Xbox download checkpoints into the same <see cref="AppActivity"/> shape Steam produces.
/// Display names are resolved from each game's <c>MicrosoftGame.config</c> and cached, because the
/// checkpoint itself only carries the package identity.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class XboxScanner(IXboxCheckpointSource? source = null, ILog? log = null)
{
    private readonly IXboxCheckpointSource _source = source ?? new RegistryCheckpointSource();
    private readonly ILog _log = log ?? NullLog.Instance;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overridable so tests can point the name lookup at a temporary folder.</summary>
    public Func<IReadOnlyList<string>> GamesRoots { get; init; } = XboxLocator.FindGamesRoots;

    public XboxScanResult Scan(DateTimeOffset now)
    {
        var checkpoints = _source.Read();
        if (checkpoints is null)
        {
            return new XboxScanResult(false, [], "The Xbox app (Gaming Services) was not found.");
        }

        var apps = new List<AppActivity>();

        foreach (var (key, json) in checkpoints)
        {
            var checkpoint = XboxCheckpointReader.Read(key, json);
            if (checkpoint is null)
            {
                _log.Warn($"Unreadable Xbox checkpoint '{key}'.");
                continue;
            }

            apps.Add(new AppActivity
            {
                AppId = XboxCheckpointReader.AppIdFor(checkpoint.ContentId),
                Name = ResolveName(checkpoint),
                Platform = GamePlatform.Xbox,
                State = checkpoint.ToStateFlags(),
                BytesDownloaded = checkpoint.StreamedBytes,
                BytesToDownload = checkpoint.TotalBytes,
                LibraryPath = checkpoint.ContentId,

                // Gaming Services streams straight into the final container, so there is no separate
                // install phase to report; the download bar is the whole story.
                BytesStaged = 0,
                BytesToStage = 0,
                ManifestWrittenAt = now,
            });
        }

        return new XboxScanResult(true, apps, null);
    }

    /// <summary>
    /// Looks for the friendly title in <c>&lt;games root&gt;\&lt;content id&gt;\Content\MicrosoftGame.config</c>,
    /// falling back to the package identity while the download is too young to have one.
    /// </summary>
    private string ResolveName(XboxCheckpoint checkpoint)
    {
        if (_names.TryGetValue(checkpoint.ContentId, out var cached))
        {
            return cached;
        }

        foreach (var root in GamesRoots())
        {
            var config = Path.Combine(root, checkpoint.ContentId, "Content", "MicrosoftGame.config");
            var name = ReadDisplayName(config);
            if (name is null)
            {
                continue;
            }

            _names[checkpoint.ContentId] = name;
            return name;
        }

        return checkpoint.FallbackName;
    }

    private string? ReadDisplayName(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            // The file is written once at the start of the install and not held open afterwards,
            // but share everything anyway: Gaming Services may still have it.
            using var stream = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var name = XDocument.Load(stream).Root?
                .Element("ShellVisuals")?
                .Attribute("DefaultDisplayName")?
                .Value;

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _log.Warn($"Cannot read '{configPath}': {e.Message}");
            return null;
        }
    }
}

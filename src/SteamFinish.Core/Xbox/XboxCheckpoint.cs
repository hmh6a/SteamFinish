using System.Text.Json;
using SteamFinish.Core.Steam;

namespace SteamFinish.Core.Xbox;

/// <summary>
/// One entry from Gaming Services' <c>StreamingCheckpoints</c> registry key — the Xbox app's
/// equivalent of a Steam app manifest. Kept separate from the parser so the JSON shapes the Xbox
/// app writes can be tested without touching the registry.
/// </summary>
public sealed record XboxCheckpoint
{
    /// <summary>The registry value name, of the form <c>{instance}#{content}</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The content GUID, which is also the folder name under <c>XboxGames</c>.</summary>
    public required string ContentId { get; init; }

    /// <summary>"Running", "Paused", "Queued" … as written by Gaming Services.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>"Streaming", "Installing", "None" …</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>"Install" or "Update".</summary>
    public string Type { get; init; } = string.Empty;

    public int QueueOrder { get; init; }

    public long TotalBytes { get; init; }

    public long StreamedBytes { get; init; }

    /// <summary>e.g. <c>WarnerBros.Interactive.PHX_1.0.16.0_x64__ktmk1xygcecda</c>.</summary>
    public string PackageFullName { get; init; } = string.Empty;

    public string StoreId { get; init; } = string.Empty;

    public bool IsComplete => TotalBytes > 0 && StreamedBytes >= TotalBytes;

    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gaming Services spells this differently across versions, so match on the stem.</summary>
    public bool IsPaused => State.Contains("Paus", StringComparison.OrdinalIgnoreCase);

    /// <summary>True once the bytes are down and the package is being written out.</summary>
    public bool IsInstalling =>
        IsRunning && !Operation.Equals("Streaming", StringComparison.OrdinalIgnoreCase);

    /// <summary>A readable name derived from the package identity, used until the real one is found.</summary>
    public string FallbackName
    {
        get
        {
            var identity = PackageFullName.Split('_', 2)[0];
            if (identity.Length == 0)
            {
                return ContentId;
            }

            // "WarnerBros.Interactive.PHX" reads better as its last segment.
            var lastSegment = identity.Split('.')[^1];
            return lastSegment.Length > 0 ? lastSegment : identity;
        }
    }

    /// <summary>
    /// Translates the Xbox state into Steam's flag vocabulary so the rest of the app treats both
    /// platforms identically.
    /// </summary>
    public AppStateFlags ToStateFlags()
    {
        if (IsComplete && !IsRunning)
        {
            return AppStateFlags.FullyInstalled;
        }

        if (IsPaused)
        {
            return AppStateFlags.UpdatePaused | AppStateFlags.UpdateStarted;
        }

        if (IsInstalling)
        {
            return AppStateFlags.UpdateRunning | AppStateFlags.Staging;
        }

        if (IsRunning)
        {
            return AppStateFlags.UpdateRunning | AppStateFlags.Downloading;
        }

        // Queued, suspended, or a state this build has not seen: outstanding either way, which is
        // the safe direction — it blocks the countdown rather than letting it fire early.
        return AppStateFlags.UpdateStarted | AppStateFlags.UpdateRequired;
    }
}

public static class XboxCheckpointReader
{
    /// <summary>Parses one checkpoint value; returns <c>null</c> when the JSON is not usable.</summary>
    public static XboxCheckpoint? Read(string key, string json)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var progress = Descend(root, "Status", "Progress", "Package");

            return new XboxCheckpoint
            {
                Key = key,
                ContentId = ContentIdOf(key),
                State = Text(root, "State"),
                Type = Text(root, "Type"),
                QueueOrder = root.TryGetProperty("QueueOrder", out var order) && order.TryGetInt32(out var value)
                    ? value
                    : 0,
                Operation = Text(Descend(root, "Status"), "Operation"),
                TotalBytes = Number(progress, "TotalBytes"),
                StreamedBytes = Number(progress, "StreamedBytes"),
                PackageFullName = Text(Descend(root, "PC"), "PackageFullName"),
                StoreId = Text(Descend(root, "Request"), "StoreId"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The value name is <c>{instance}#{content}</c>; the second GUID names the folder under
    /// <c>XboxGames</c>, which is how the display name is found.
    /// </summary>
    public static string ContentIdOf(string key)
    {
        var hash = key.LastIndexOf('#');
        var tail = hash >= 0 ? key[(hash + 1)..] : key;
        return tail.Trim('{', '}');
    }

    /// <summary>
    /// A stable id in the top half of the range, so a synthetic Xbox key can never collide with a
    /// real Steam app id.
    /// </summary>
    public static uint AppIdFor(string contentId)
    {
        // FNV-1a: short, stable across runs, and good enough for identity within one machine.
        var hash = 2166136261u;
        foreach (var c in contentId)
        {
            hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
        }

        return hash | 0x8000_0000u;
    }

    private static JsonElement Descend(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
            {
                return default;
            }

            current = next;
        }

        return current;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var number)
            ? number
            : 0;
}

using System.Text.Json;

namespace SteamFinish.Core.Updates;

/// <summary>What the newest GitHub release offers, once it has been compared with what is running.</summary>
public sealed record UpdateInfo(
    string Version,
    string Tag,
    string DownloadUrl,
    long SizeBytes,
    string? ChecksumUrl,
    string ReleaseUrl);

public sealed record UpdateCheckResult(
    bool Checked,
    UpdateInfo? Available,
    string? Message)
{
    public static UpdateCheckResult Failed(string message) => new(false, null, message);

    public static UpdateCheckResult UpToDate() => new(true, null, null);
}

/// <summary>
/// Reads a GitHub "latest release" payload and decides whether it is newer than what is running.
/// Kept away from the HTTP call so the version rules can be tested on their own.
/// </summary>
public static class ReleaseReader
{
    /// <summary>
    /// Compares two version strings numerically, tolerating a leading "v" and differing lengths, so
    /// 1.10.0 correctly beats 1.9.0 — which a string comparison gets wrong.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!TryParse(candidate, out var left) || !TryParse(current, out var right))
        {
            return false;
        }

        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b)
            {
                return a > b;
            }
        }

        return false;
    }

    /// <summary>Pulls the release out of GitHub's JSON, or <c>null</c> when it carries no build.</summary>
    public static UpdateInfo? Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tag = Text(root, "tag_name");
            if (tag.Length == 0 || (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True))
            {
                return null;
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement? zip = null;
            string? checksumUrl = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zip ??= asset;
                }
                else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = Text(asset, "browser_download_url");
                }
            }

            if (zip is not { } payload)
            {
                return null;
            }

            return new UpdateInfo(
                Version: tag.TrimStart('v', 'V'),
                Tag: tag,
                DownloadUrl: Text(payload, "browser_download_url"),
                SizeBytes: payload.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                ChecksumUrl: checksumUrl,
                ReleaseUrl: Text(root, "html_url"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Trims the build metadata the SDK appends, e.g. "1.2.3+abc123" or "1.2.3.0".</summary>
    public static string Normalize(string version)
    {
        var text = version.TrimStart('v', 'V');
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }

    private static bool TryParse(string version, out int[] parts)
    {
        var text = Normalize(version);
        var pieces = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>(pieces.Length);

        foreach (var piece in pieces)
        {
            if (!int.TryParse(piece, out var number))
            {
                break;
            }

            numbers.Add(number);
        }

        parts = [.. numbers];
        return parts.Length > 0;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

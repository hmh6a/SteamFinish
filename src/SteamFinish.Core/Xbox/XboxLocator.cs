using System.Runtime.Versioning;
using System.Text;

namespace SteamFinish.Core.Xbox;

/// <summary>
/// Finds the Xbox games folders. Every drive that can hold Xbox games carries a hidden
/// <c>.GamingRoot</c> file at its root naming the folder, which is how the Xbox app itself
/// locates them.
/// </summary>
[SupportedOSPlatform("windows")]
public static class XboxLocator
{
    /// <summary>"RGBX" — the four bytes every .GamingRoot file starts with.</summary>
    private static readonly byte[] Magic = [0x52, 0x47, 0x42, 0x58];

    public static IReadOnlyList<string> FindGamesRoots()
    {
        var roots = new List<string>();

        foreach (var drive in SafeDrives())
        {
            var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            var relative = ReadGamingRoot(marker);
            if (relative is null)
            {
                continue;
            }

            var folder = Path.Combine(drive.RootDirectory.FullName, relative);
            if (Directory.Exists(folder))
            {
                roots.Add(folder);
            }
        }

        return roots;
    }

    /// <summary>
    /// Reads the folder name out of a <c>.GamingRoot</c> file: the magic, a version word, then a
    /// null-terminated UTF-16 relative path. Returns <c>null</c> when the file is absent or foreign.
    /// </summary>
    public static string? ReadGamingRoot(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 10 || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            {
                return null;
            }

            // 4 bytes magic + 4 bytes version, then the UTF-16 path.
            var text = Encoding.Unicode.GetString(bytes, 8, ((bytes.Length - 8) / 2) * 2);
            var end = text.IndexOf('\0');
            if (end >= 0)
            {
                text = text[..end];
            }

            text = text.Trim().Trim('\\');
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            var usable = false;
            try
            {
                // Touching IsReady on a disconnected network drive can throw or hang.
                usable = drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                usable = false;
            }

            if (usable)
            {
                yield return drive;
            }
        }
    }
}

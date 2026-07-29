using System.Runtime.Versioning;
using Microsoft.Win32;
using SteamFinish.Core.Vdf;

namespace SteamFinish.Core.Steam;

/// <summary>Finds the Steam installation and every configured library folder.</summary>
[SupportedOSPlatform("windows")]
public static class SteamLocator
{
    /// <summary>Returns the Steam install folder, or <c>null</c> when Steam cannot be located.</summary>
    public static string? FindSteamPath()
    {
        foreach (var candidate in ReadRegistryCandidates())
        {
            var normalized = Normalize(candidate);
            if (normalized is not null && Directory.Exists(Path.Combine(normalized, "steamapps")))
            {
                return normalized;
            }
        }

        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "ProgramW6432" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var guess = Path.Combine(root, "Steam");
            if (Directory.Exists(Path.Combine(guess, "steamapps")))
            {
                return guess;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns every library root (the folder that contains <c>steamapps</c>), starting with the
    /// Steam install itself. Paths are de-duplicated case-insensitively.
    /// </summary>
    public static IReadOnlyList<string> FindLibraries(string? steamPath = null)
    {
        steamPath ??= FindSteamPath();
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            var normalized = Normalize(path);
            if (normalized is null || !Directory.Exists(Path.Combine(normalized, "steamapps")))
            {
                return;
            }

            if (seen.Add(normalized))
            {
                roots.Add(normalized);
            }
        }

        if (steamPath is null)
        {
            return roots;
        }

        Add(steamPath);

        foreach (var manifest in new[]
                 {
                     Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamPath, "config", "libraryfolders.vdf"),
                 })
        {
            foreach (var path in ReadLibraryFolders(manifest))
            {
                Add(path);
            }
        }

        return roots;
    }

    /// <summary>Reads the library paths declared in a <c>libraryfolders.vdf</c> file.</summary>
    public static IReadOnlyList<string> ReadLibraryFolders(string manifestPath)
    {
        if (!File.Exists(manifestPath) || !VdfParser.TryParseFile(manifestPath, out var document))
        {
            return [];
        }

        var folders = document.Unwrap("libraryfolders");
        var paths = new List<string>();

        foreach (var (key, node) in folders.Children)
        {
            // Modern layout: "0" { "path" "D:\\SteamLibrary" ... }
            if (node.IsObject)
            {
                if (node.GetString("path") is { Length: > 0 } path)
                {
                    paths.Add(path);
                }

                continue;
            }

            // Legacy layout: "1" "D:\\SteamLibrary"
            if (uint.TryParse(key, out _) && node.Value is { Length: > 0 } legacy)
            {
                paths.Add(legacy);
            }
        }

        return paths;
    }

    private static IEnumerable<string> ReadRegistryCandidates()
    {
        var sources = new (RegistryHive Hive, RegistryView View, string SubKey, string Value)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };

        foreach (var (hive, view, subKey, valueName) in sources)
        {
            string? value = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                value = key?.GetValue(valueName) as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable hive: fall through to the next candidate.
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            // Steam stores forward slashes in the registry and escaped backslashes in VDF files.
            var cleaned = path.Trim().Replace('/', '\\').TrimEnd('\\');
            if (cleaned.Length == 0)
            {
                return null;
            }

            // The registry hands back a lower-case drive letter; upper case reads better in the UI.
            var full = Path.GetFullPath(cleaned);
            return full.Length > 1 && full[1] == ':' ? char.ToUpperInvariant(full[0]) + full[1..] : full;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

namespace SteamFinish.Core;

/// <summary>Where SteamFinish keeps its own files.</summary>
public static class AppPaths
{
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SteamFinish");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string LogFile => Path.Combine(DataFolder, "steamfinish.log");

    public static void EnsureDataFolder() => Directory.CreateDirectory(DataFolder);
}

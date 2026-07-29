namespace SteamFinish.Core.Steam;

/// <summary>
/// Values of the <c>StateFlags</c> field inside <c>appmanifest_*.acf</c>.
/// Mirrors Valve's <c>EAppState</c>.
/// </summary>
[Flags]
public enum AppStateFlags : long
{
    Invalid = 0,
    Uninstalled = 1 << 0,
    UpdateRequired = 1 << 1,
    FullyInstalled = 1 << 2,
    Encrypted = 1 << 3,
    Locked = 1 << 4,
    FilesMissing = 1 << 5,
    AppRunning = 1 << 6,
    FilesCorrupt = 1 << 7,
    UpdateRunning = 1 << 8,
    UpdatePaused = 1 << 9,
    UpdateStarted = 1 << 10,
    Uninstalling = 1 << 11,
    BackupRunning = 1 << 12,
    Reconfiguring = 1 << 16,
    Validating = 1 << 17,
    AddingFiles = 1 << 18,
    Preallocating = 1 << 19,
    Downloading = 1 << 20,
    Staging = 1 << 21,
    Committing = 1 << 22,
    UpdateStopping = 1 << 23,
}

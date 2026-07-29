using SteamFinish.Core.Steam;

namespace SteamFinish.Tests;

internal static class TestData
{
    public static AppActivity App(
        AppStateFlags state,
        long downloaded = 0,
        long toDownload = 0,
        long staged = 0,
        long toStage = 0,
        uint appId = 570,
        string name = "Test Game") => new()
    {
        AppId = appId,
        Name = name,
        State = state,
        BytesDownloaded = downloaded,
        BytesToDownload = toDownload,
        BytesStaged = staged,
        BytesToStage = toStage,
        LibraryPath = @"C:\Steam",
    };

    public static SteamSnapshot Snapshot(params AppActivity[] apps) => new()
    {
        TakenAt = DateTimeOffset.UnixEpoch,
        Apps = apps,
        LibraryRoots = [@"C:\Steam"],
        SteamRunning = true,
    };

    public static SteamSnapshot Idle() => Snapshot(App(AppStateFlags.FullyInstalled));

    public static SteamSnapshot Downloading() =>
        Snapshot(App(AppStateFlags.UpdateRunning | AppStateFlags.Downloading, downloaded: 50, toDownload: 100));

    public static SteamSnapshot Unavailable() =>
        SteamSnapshot.Unavailable(DateTimeOffset.UnixEpoch, "libraries missing");
}

/// <summary>A throwaway directory that cleans itself up at the end of a test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "SteamFinishTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Creates a library root with an empty <c>steamapps</c> folder and returns its path.</summary>
    public string CreateLibrary(string name = "Library")
    {
        var library = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        return library;
    }

    public void WriteManifest(
        string library,
        uint appId,
        string name,
        long stateFlags,
        long bytesDownloaded = 0,
        long bytesToDownload = 0)
    {
        var content = $$"""
            "AppState"
            {
            	"appid"		"{{appId}}"
            	"name"		"{{name}}"
            	"StateFlags"		"{{stateFlags}}"
            	"installdir"		"{{name}}"
            	"BytesDownloaded"		"{{bytesDownloaded}}"
            	"BytesToDownload"		"{{bytesToDownload}}"
            	"BytesStaged"		"0"
            	"BytesToStage"		"0"
            }
            """;

        File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"), content);
    }

    public string CreateDownloadingFolder(string library, uint appId)
    {
        var path = Path.Combine(library, "steamapps", "downloading", appId.ToString());
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "chunk.bin"), "data");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }
}

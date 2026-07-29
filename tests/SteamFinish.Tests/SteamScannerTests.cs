using SteamFinish.Core.Steam;

namespace SteamFinish.Tests;

public class SteamScannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static SteamScanner ScannerFor(params string[] libraries) =>
        new(new FixedLibrarySource(libraries));

    [Fact]
    public void ReportsUnavailableWhenThereAreNoLibraries()
    {
        var snapshot = ScannerFor().Scan(Now);

        Assert.False(snapshot.IsReliable);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public void ReadsManifestsFromEveryLibrary()
    {
        using var temp = new TempFolder();
        var first = temp.CreateLibrary("Main");
        var second = temp.CreateLibrary("Extra");
        temp.WriteManifest(first, 570, "Dota 2", stateFlags: 4);
        temp.WriteManifest(second, 730, "Counter-Strike", stateFlags: 4);

        var snapshot = ScannerFor(first, second).Scan(Now);

        Assert.True(snapshot.IsReliable);
        Assert.Equal(2, snapshot.Apps.Count);
        Assert.Contains(snapshot.Apps, a => a is { AppId: 570, Name: "Dota 2" });
        Assert.Contains(snapshot.Apps, a => a is { AppId: 730, Name: "Counter-Strike" });
        Assert.False(snapshot.HasPendingWork());
    }

    [Fact]
    public void DetectsAnActiveDownload()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        // 1048580 = FullyInstalled | Downloading, with UpdateRunning added on top.
        temp.WriteManifest(library, 1_091_500, "Cyberpunk", stateFlags: 4 | 256 | 1_048_576,
            bytesDownloaded: 8_200, bytesToDownload: 10_000);

        var snapshot = ScannerFor(library).Scan(Now);

        Assert.True(snapshot.HasPendingWork());
        var headline = snapshot.Headline!;
        Assert.Equal("Cyberpunk", headline.Name);
        Assert.Equal(0.82, headline.Progress!.Value, 3);

        var status = SteamStatusFormatter.Describe(snapshot);
        Assert.Equal("Downloading Cyberpunk (82%)", status.Headline);
    }

    [Fact]
    public void AnInstalledLibraryReportsNothingInProgress()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4);

        var status = SteamStatusFormatter.Describe(ScannerFor(library).Scan(Now));

        Assert.Equal("No downloads in progress", status.Headline);
    }

    [Fact]
    public void ADownloadFolderBackedByAnUnsettledManifestCountsAsBusy()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4 | 1_024); // UpdateStarted
        temp.CreateDownloadingFolder(library, 570);

        var snapshot = ScannerFor(library).Scan(Now);

        Assert.True(snapshot.DownloadFolderBusy);
        Assert.True(snapshot.HasPendingWork());
    }

    [Fact]
    public void LeftoverDownloadFoldersFromASettledGameAreIgnored()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4);
        var folder = temp.CreateDownloadingFolder(library, 570);
        // Old enough that the "changed recently" fallback cannot rescue it either.
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddHours(-5));

        var snapshot = ScannerFor(library).Scan(Now);

        Assert.False(snapshot.DownloadFolderBusy);
        Assert.False(snapshot.HasPendingWork());
    }

    [Fact]
    public void ADownloadFolderWithoutAManifestCountsAsBusyWhileItIsFresh()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.CreateDownloadingFolder(library, 9_999);

        var snapshot = ScannerFor(library).Scan(Now);

        Assert.True(snapshot.DownloadFolderBusy);
    }

    [Fact]
    public void ManifestsAreReReadAfterTheyChange()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        var scanner = ScannerFor(library);

        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4 | 256 | 1_048_576,
            bytesDownloaded: 1, bytesToDownload: 100);
        Assert.True(scanner.Scan(Now).HasPendingWork());

        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4, bytesDownloaded: 100, bytesToDownload: 100);
        File.SetLastWriteTimeUtc(
            Path.Combine(library, "steamapps", "appmanifest_570.acf"),
            DateTime.UtcNow.AddSeconds(1));

        Assert.False(scanner.Scan(Now).HasPendingWork());
    }

    [Fact]
    public void UnreadableLibrariesMakeTheSnapshotUnreliable()
    {
        using var temp = new TempFolder();
        var good = temp.CreateLibrary("Main");
        temp.WriteManifest(good, 570, "Dota 2", stateFlags: 4);
        var missing = Path.Combine(temp.Root, "Gone");

        var snapshot = ScannerFor(good, missing).Scan(Now);

        Assert.NotNull(snapshot.Error);
        Assert.False(snapshot.IsReliable);
    }

    [Fact]
    public void BrokenManifestsAreSkippedRatherThanFailingTheScan()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4);
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_bad.acf"), "{{{ not vdf");

        var snapshot = ScannerFor(library).Scan(Now);

        Assert.True(snapshot.IsReliable);
        Assert.Single(snapshot.Apps);
    }
}

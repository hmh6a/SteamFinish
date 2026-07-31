using SteamFinish.Core.Steam;
using SteamFinish.Core.Xbox;

namespace SteamFinish.Tests;

/// <summary>
/// The two launchers are merged into one snapshot, and the countdown must wait for whichever of them
/// still has work. These cover the merge itself and the "which failures matter" rules.
/// </summary>
public class DownloadScannerTests
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private const string XboxKey = "{656D2CCB-8D2B-4E9F-8255-081B45DFB75A}#{C1084505-ABC1-4C27-B3FE-AB7040A5F302}";

    private const string XboxDownloading = """
        {"State":"Running","Type":"Install","QueueOrder":0,
         "Status":{"Operation":"Streaming",
           "Progress":{"Package":{"TotalBytes":77016702976,"StreamedBytes":563023872}}},
         "PC":{"PackageFullName":"WarnerBros.Interactive.PHX_1.0.16.0_x64__ktmk1xygcecda"}}
        """;

    [Fact]
    public void DownloadsFromBothLaunchersAppearTogether()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4 | 256 | 1_048_576, bytesDownloaded: 10, bytesToDownload: 100);

        var snapshot = Build(temp, library, XboxDownloading).Scan(DateTimeOffset.Now);

        Assert.True(snapshot.IsReliable);
        Assert.Equal(2, snapshot.Apps.Count);
        Assert.Contains(snapshot.Apps, a => a.Platform == GamePlatform.Steam && a.Name == "Dota 2");
        Assert.Contains(snapshot.Apps, a => a.Platform == GamePlatform.Xbox);
        Assert.True(snapshot.HasPendingWork());
    }

    [Fact]
    public void AnIdleSteamStillWaitsForAnXboxInstall()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4); // installed, nothing to do

        var snapshot = Build(temp, library, XboxDownloading).Scan(DateTimeOffset.Now);

        Assert.True(snapshot.HasPendingWork());
        Assert.Equal(GamePlatform.Xbox, snapshot.Headline!.Platform);
    }

    [Fact]
    public void WithBothLaunchersIdleNothingIsOutstanding()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4);

        var snapshot = Build(temp, library, checkpoints: Empty).Scan(DateTimeOffset.Now);

        Assert.True(snapshot.IsReliable);
        Assert.False(snapshot.HasPendingWork());
    }

    [Fact]
    public void UntickingALauncherHidesItsDownloads()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4);

        var scanner = Build(temp, library, XboxDownloading, watchXbox: false);
        var snapshot = scanner.Scan(DateTimeOffset.Now);

        Assert.True(snapshot.IsReliable);
        Assert.False(snapshot.HasPendingWork());
        Assert.DoesNotContain(snapshot.Apps, a => a.Platform == GamePlatform.Xbox);
    }

    [Fact]
    public void AMachineWithoutSteamStillWatchesXbox()
    {
        using var temp = new TempFolder();

        // No Steam library at all: the Steam scan fails, but Xbox answered.
        var scanner = new DownloadScanner(
            new SteamScanner(new FixedLibrarySource([])),
            XboxScannerFor(temp, XboxDownloading),
            () => true,
            () => true);

        var snapshot = scanner.Scan(DateTimeOffset.Now);

        Assert.True(snapshot.IsReliable);
        Assert.True(snapshot.HasPendingWork());
        Assert.NotNull(snapshot.Warning);
    }

    [Fact]
    public void AMachineWithoutTheXboxAppStillWatchesSteam()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Dota 2", stateFlags: 4 | 256 | 1_048_576, bytesDownloaded: 10, bytesToDownload: 100);

        var scanner = new DownloadScanner(
            new SteamScanner(new FixedLibrarySource([library])),
            new XboxScanner(new FakeSource(null)),
            () => true,
            () => true);

        var snapshot = scanner.Scan(DateTimeOffset.Now);

        Assert.True(snapshot.IsReliable);
        Assert.True(snapshot.HasPendingWork());
        Assert.NotNull(snapshot.Warning);
    }

    [Fact]
    public void WhenNeitherLauncherCanBeReadTheSnapshotIsUnusable()
    {
        var scanner = new DownloadScanner(
            new SteamScanner(new FixedLibrarySource([])),
            new XboxScanner(new FakeSource(null)),
            () => true,
            () => true);

        var snapshot = scanner.Scan(DateTimeOffset.Now);

        Assert.False(snapshot.IsReliable);
        Assert.NotNull(snapshot.Error);
    }

    [Fact]
    public void TurningEverythingOffIsTreatedAsUnknownRatherThanFinished()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();

        var snapshot = Build(temp, library, checkpoints: Empty, watchSteam: false, watchXbox: false)
            .Scan(DateTimeOffset.Now);

        // Never "everything is done": with nothing watched there is nothing to conclude.
        Assert.False(snapshot.IsReliable);
    }

    private static DownloadScanner Build(
        TempFolder temp,
        string library,
        string? checkpoint,
        bool watchSteam = true,
        bool watchXbox = true) =>
        Build(
            temp,
            library,
            checkpoint is null ? Empty : new Dictionary<string, string> { [XboxKey] = checkpoint },
            watchSteam,
            watchXbox);

    private static DownloadScanner Build(
        TempFolder temp,
        string library,
        IReadOnlyDictionary<string, string> checkpoints,
        bool watchSteam = true,
        bool watchXbox = true) =>
        new(
            new SteamScanner(new FixedLibrarySource([library])),
            new XboxScanner(new FakeSource(checkpoints)) { GamesRoots = () => [temp.Root] },
            () => watchSteam,
            () => watchXbox);

    private static XboxScanner XboxScannerFor(TempFolder temp, string checkpoint) =>
        new(new FakeSource(new Dictionary<string, string> { [XboxKey] = checkpoint }))
        {
            GamesRoots = () => [temp.Root],
        };

    private sealed class FakeSource(IReadOnlyDictionary<string, string>? entries) : IXboxCheckpointSource
    {
        public IReadOnlyDictionary<string, string>? Read() => entries;
    }
}

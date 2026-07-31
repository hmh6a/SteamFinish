using System.Text;
using SteamFinish.Core.Steam;
using SteamFinish.Core.Xbox;

namespace SteamFinish.Tests;

public class XboxCheckpointTests
{
    /// <summary>
    /// Trimmed from a real Gaming Services checkpoint captured while Hogwarts Legacy was streaming.
    /// </summary>
    private const string RealCheckpoint = """
        {"VersionId":"264bae7e-ec1e-4a88-a7f5-22b9c59850be","QueueOrder":0,"Type":"Install",
         "State":"Running",
         "Request":{"StoreId":"9MT5NJ5W7B8Z","SkuId":"0010"},
         "Status":{"Operation":"Streaming","Result":0,
           "Progress":{"Package":{"TotalBytes":77016702976,"StreamedBytes":563023872},
                       "Install":{"TotalBytes":77016702976,"StreamedBytes":563023872},
                       "Launch":{"TotalBytes":32932347904,"StreamedBytes":563023872},
                       "DataRate":27.830912}},
         "PC":{"PackageFullName":"WarnerBros.Interactive.PHX_1.0.16.0_x64__ktmk1xygcecda"}}
        """;

    private const string Key = "{656D2CCB-8D2B-4E9F-8255-081B45DFB75A}#{C1084505-ABC1-4C27-B3FE-AB7040A5F302}";

    [Fact]
    public void ReadsARealCheckpoint()
    {
        var checkpoint = XboxCheckpointReader.Read(Key, RealCheckpoint)!;

        Assert.Equal("C1084505-ABC1-4C27-B3FE-AB7040A5F302", checkpoint.ContentId);
        Assert.Equal("Running", checkpoint.State);
        Assert.Equal("Streaming", checkpoint.Operation);
        Assert.Equal("Install", checkpoint.Type);
        Assert.Equal(0, checkpoint.QueueOrder);
        Assert.Equal(77_016_702_976, checkpoint.TotalBytes);
        Assert.Equal(563_023_872, checkpoint.StreamedBytes);
        Assert.Equal("9MT5NJ5W7B8Z", checkpoint.StoreId);
        Assert.Equal("WarnerBros.Interactive.PHX_1.0.16.0_x64__ktmk1xygcecda", checkpoint.PackageFullName);
    }

    [Fact]
    public void ARunningStreamMapsToADownload()
    {
        var checkpoint = XboxCheckpointReader.Read(Key, RealCheckpoint)!;
        var flags = checkpoint.ToStateFlags();

        Assert.True((flags & AppStateFlags.Downloading) != 0);
        Assert.True((flags & AppStateFlags.UpdateRunning) != 0);

        var app = Activity(checkpoint);
        Assert.True(app.HasJobFlags);
        Assert.True(app.IsOutstanding);
        Assert.Equal(0.0073, app.DownloadProgress!.Value, 4);
    }

    [Theory]
    [InlineData("Paused")]
    [InlineData("PausedByUser")]
    [InlineData("paused")]
    public void APausedCheckpointIsRecognisedHoweverItIsSpelled(string state)
    {
        var checkpoint = XboxCheckpointReader.Read(Key, Json(state, "None"))!;

        Assert.True(checkpoint.IsPaused);
        Assert.True(Activity(checkpoint).IsPaused);
    }

    [Fact]
    public void AQueuedCheckpointStillCountsAsOutstanding()
    {
        var checkpoint = XboxCheckpointReader.Read(Key, Json("Queued", "None"))!;

        Assert.False(checkpoint.IsRunning);
        Assert.True(Activity(checkpoint).IsOutstanding);
    }

    [Fact]
    public void AnUnrecognisedStateBlocksRatherThanFiringEarly()
    {
        var checkpoint = XboxCheckpointReader.Read(Key, Json("SomethingNewMicrosoftAdded", "None"))!;

        Assert.True(Activity(checkpoint).IsOutstanding);
    }

    [Fact]
    public void AFullyStreamedIdleCheckpointNoLongerBlocks()
    {
        var json = """
            {"State":"Completed","Type":"Install","QueueOrder":0,
             "Status":{"Operation":"None","Progress":{"Package":{"TotalBytes":1000,"StreamedBytes":1000}}},
             "PC":{"PackageFullName":"Some.Game_1.0_x64__abc"}}
            """;

        var checkpoint = XboxCheckpointReader.Read(Key, json)!;

        Assert.True(checkpoint.IsComplete);
        Assert.False(Activity(checkpoint).IsOutstanding);
    }

    [Fact]
    public void PostDownloadWorkIsReportedAsInstalling()
    {
        var checkpoint = XboxCheckpointReader.Read(Key, Json("Running", "Installing"))!;

        Assert.True(checkpoint.IsInstalling);
        Assert.True((checkpoint.ToStateFlags() & AppStateFlags.Staging) != 0);
    }

    [Fact]
    public void SyntheticIdsAreStableAndCannotCollideWithSteam()
    {
        var id = XboxCheckpointReader.AppIdFor("C1084505-ABC1-4C27-B3FE-AB7040A5F302");

        Assert.Equal(id, XboxCheckpointReader.AppIdFor("c1084505-abc1-4c27-b3fe-ab7040a5f302"));
        Assert.NotEqual(id, XboxCheckpointReader.AppIdFor("D1084505-ABC1-4C27-B3FE-AB7040A5F302"));

        // Steam app ids are ordinary small numbers; Xbox keys live in the top half of the range.
        Assert.True(id >= 0x8000_0000u);
    }

    [Fact]
    public void TheFallbackNameIsTheLastSegmentOfThePackageIdentity()
    {
        Assert.Equal("PHX", XboxCheckpointReader.Read(Key, RealCheckpoint)!.FallbackName);
    }

    [Fact]
    public void GarbageIsRejectedRatherThanThrown()
    {
        Assert.Null(XboxCheckpointReader.Read(Key, "not json at all"));
        Assert.Null(XboxCheckpointReader.Read(Key, "[1,2,3]"));
        Assert.Null(XboxCheckpointReader.Read(string.Empty, RealCheckpoint));
    }

    private static AppActivity Activity(XboxCheckpoint checkpoint) => new()
    {
        AppId = XboxCheckpointReader.AppIdFor(checkpoint.ContentId),
        Name = checkpoint.FallbackName,
        Platform = GamePlatform.Xbox,
        State = checkpoint.ToStateFlags(),
        BytesDownloaded = checkpoint.StreamedBytes,
        BytesToDownload = checkpoint.TotalBytes,
    };

    /// <summary>
    /// Built by substitution rather than interpolation: the JSON has runs of three closing braces,
    /// which fight with raw-string interpolation delimiters.
    /// </summary>
    private static string Json(string state, string operation) => Template
        .Replace("STATE", state, StringComparison.Ordinal)
        .Replace("OPERATION", operation, StringComparison.Ordinal);

    private const string Template = """
        {"State":"STATE","Type":"Install","QueueOrder":0,
         "Status":{"Operation":"OPERATION",
           "Progress":{"Package":{"TotalBytes":77016702976,"StreamedBytes":563023872}}},
         "PC":{"PackageFullName":"WarnerBros.Interactive.PHX_1.0.16.0_x64__ktmk1xygcecda"}}
        """;
}

public class XboxLocatorTests
{
    [Fact]
    public void ReadsTheRealGamingRootFormat()
    {
        // Captured from C:\.GamingRoot — "RGBX", a version word, then a UTF-16 relative path.
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, ".GamingRoot");
        var bytes = new List<byte> { 0x52, 0x47, 0x42, 0x58, 0x01, 0x00, 0x00, 0x00 };
        bytes.AddRange(Encoding.Unicode.GetBytes("XboxGames\0"));
        File.WriteAllBytes(path, [.. bytes]);

        Assert.Equal("XboxGames", XboxLocator.ReadGamingRoot(path));
    }

    [Fact]
    public void RejectsAFileThatIsNotAGamingRoot()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, ".GamingRoot");
        File.WriteAllBytes(path, Encoding.Unicode.GetBytes("just some text"));

        Assert.Null(XboxLocator.ReadGamingRoot(path));
        Assert.Null(XboxLocator.ReadGamingRoot(Path.Combine(temp.Root, "absent")));
    }
}

public class XboxScannerTests
{
    private const string Key = "{656D2CCB-8D2B-4E9F-8255-081B45DFB75A}#{C1084505-ABC1-4C27-B3FE-AB7040A5F302}";
    private const string ContentId = "C1084505-ABC1-4C27-B3FE-AB7040A5F302";

    /// <summary>Gaming Services installed, with nothing downloading.</summary>
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    [Fact]
    public void ReportsGamingServicesMissingWhenTheKeyIsAbsent()
    {
        var result = new XboxScanner(new FakeSource(null)).Scan(DateTimeOffset.Now);

        Assert.False(result.Available);
        Assert.Empty(result.Apps);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void AnInstalledMachineWithNoDownloadsIsAvailableButEmpty()
    {
        var result = new XboxScanner(new FakeSource(Empty)).Scan(DateTimeOffset.Now);

        Assert.True(result.Available);
        Assert.Empty(result.Apps);
    }

    [Fact]
    public void ResolvesTheFriendlyTitleFromTheGameConfig()
    {
        using var temp = new TempFolder();
        var content = Path.Combine(temp.Root, ContentId, "Content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <Identity Name="WarnerBros.Interactive.PHX" Version="1.0.16.0" />
              <ShellVisuals DefaultDisplayName="Hogwarts Legacy" PublisherDisplayName="Warner Bros." />
            </Game>
            """);

        var scanner = new XboxScanner(new FakeSource(new Dictionary<string, string> { [Key] = Checkpoint }))
        {
            GamesRoots = () => [temp.Root],
        };

        var app = Assert.Single(scanner.Scan(DateTimeOffset.Now).Apps);

        Assert.Equal("Hogwarts Legacy", app.Name);
        Assert.Equal(GamePlatform.Xbox, app.Platform);
        Assert.Equal(77_016_702_976, app.BytesToDownload);
        Assert.Equal(563_023_872, app.BytesDownloaded);
        Assert.True(app.IsOutstanding);
    }

    [Fact]
    public void FallsBackToThePackageNameBeforeTheConfigExists()
    {
        using var temp = new TempFolder();
        var scanner = new XboxScanner(new FakeSource(new Dictionary<string, string> { [Key] = Checkpoint }))
        {
            GamesRoots = () => [temp.Root],
        };

        Assert.Equal("PHX", Assert.Single(scanner.Scan(DateTimeOffset.Now).Apps).Name);
    }

    private const string Checkpoint = """
        {"State":"Running","Type":"Install","QueueOrder":0,
         "Status":{"Operation":"Streaming",
           "Progress":{"Package":{"TotalBytes":77016702976,"StreamedBytes":563023872}}},
         "PC":{"PackageFullName":"WarnerBros.Interactive.PHX_1.0.16.0_x64__ktmk1xygcecda"}}
        """;

    private sealed class FakeSource(IReadOnlyDictionary<string, string>? entries) : IXboxCheckpointSource
    {
        public IReadOnlyDictionary<string, string>? Read() => entries;
    }
}

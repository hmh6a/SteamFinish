using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Steam;

namespace SteamFinish.Tests;

/// <summary>
/// Runs the real scanner against a real folder and feeds the results to the real engine, so the
/// whole "download finishes, action fires" path is covered without waiting in real time.
/// </summary>
public class EndToEndTests
{
    private const long UpdateRunningDownloading = 4 | 256 | 1_048_576;
    private const long Installed = 4;

    [Fact]
    public void ADownloadThatFinishesEventuallyTriggersTheAction()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "Test Game", UpdateRunningDownloading, 1_000, 10_000);
        temp.CreateDownloadingFolder(library, 570);

        var scanner = new SteamScanner(new FixedLibrarySource([library]));
        var engine = new MonitorEngine(() => new MonitorOptions
        {
            ConfirmationWindow = TimeSpan.FromSeconds(45),
            Countdown = TimeSpan.FromSeconds(60),
        });

        var fired = false;
        engine.ActionDue += () => fired = true;
        engine.Enable();

        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Downloading.
        Assert.Equal(MonitorPhase.Busy, engine.Update(scanner.Scan(clock), clock));

        // Steam finishes: the manifest settles and the download folder is emptied.
        temp.WriteManifest(library, 570, "Test Game", Installed, 10_000, 10_000);
        Directory.Delete(Path.Combine(library, "steamapps", "downloading", "570"), recursive: true);

        clock = clock.AddSeconds(5);
        Assert.Equal(MonitorPhase.Confirming, engine.Update(scanner.Scan(clock), clock));

        clock = clock.AddSeconds(45);
        Assert.Equal(MonitorPhase.Countdown, engine.Update(scanner.Scan(clock), clock));
        Assert.False(fired);

        clock = clock.AddSeconds(60);
        Assert.Equal(MonitorPhase.Executing, engine.Update(scanner.Scan(clock), clock));
        Assert.True(fired);
    }

    [Fact]
    public void AQueuedSecondGameKeepsTheActionFromFiring()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 570, "First", UpdateRunningDownloading, 9_000, 10_000);
        temp.WriteManifest(library, 730, "Second", 4 | 2, 0, 40_000); // installed, update queued

        var scanner = new SteamScanner(new FixedLibrarySource([library]));
        var engine = new MonitorEngine(() => new MonitorOptions
        {
            ConfirmationWindow = TimeSpan.FromSeconds(45),
            Countdown = TimeSpan.FromSeconds(60),
        });
        engine.Enable();

        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        engine.Update(scanner.Scan(clock), clock);

        // The first game completes but the queued one still has bytes outstanding.
        temp.WriteManifest(library, 570, "First", Installed, 10_000, 10_000);
        clock = clock.AddSeconds(200);

        Assert.Equal(MonitorPhase.Busy, engine.Update(scanner.Scan(clock), clock));

        // Once that one is done as well the countdown may start.
        temp.WriteManifest(library, 730, "Second", Installed, 40_000, 40_000);
        clock = clock.AddSeconds(5);
        Assert.Equal(MonitorPhase.Confirming, engine.Update(scanner.Scan(clock), clock));

        clock = clock.AddSeconds(46);
        Assert.Equal(MonitorPhase.Countdown, engine.Update(scanner.Scan(clock), clock));
    }

    [Fact]
    public void ALostConnectionDoesNotLookLikeAFinishedDownload()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();

        // Steam keeps the update flags set while it retries, and the byte counters stop moving.
        temp.WriteManifest(library, 570, "Test Game", UpdateRunningDownloading, 4_000, 10_000);

        var scanner = new SteamScanner(new FixedLibrarySource([library]));
        var engine = new MonitorEngine(() => new MonitorOptions
        {
            ConfirmationWindow = TimeSpan.FromSeconds(45),
            Countdown = TimeSpan.FromSeconds(60),
        });
        engine.Enable();

        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var minute = 0; minute < 20; minute++)
        {
            clock = clock.AddMinutes(1);
            Assert.Equal(MonitorPhase.Busy, engine.Update(scanner.Scan(clock), clock));
        }
    }

    [Fact]
    public void SwitchingLibrariesAtRuntimeIsPickedUp()
    {
        using var temp = new TempFolder();
        var first = temp.CreateLibrary("Main");
        var second = temp.CreateLibrary("Extra");
        temp.WriteManifest(first, 570, "First", Installed);
        temp.WriteManifest(second, 730, "Second", UpdateRunningDownloading, 1, 100);

        var roots = new List<string> { first };
        var scanner = new SteamScanner(new SwitchableLibrarySource(() => roots));

        Assert.False(scanner.Scan(DateTimeOffset.Now).HasPendingWork());

        roots.Add(second);
        Assert.True(scanner.Scan(DateTimeOffset.Now).HasPendingWork());
    }

    [Fact]
    public void TheAppWhoseBytesMoveIsTheOneReportedAsDownloading()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();

        // Both read 1026 — Steam's flags cannot tell the live download from the queue.
        const long Queued = 4 | 2 | 1024;
        temp.WriteManifest(library, 374_320, "DARK SOULS III", Queued, 269_991_136, 25_459_276_064);
        temp.WriteManifest(library, 2_680_010, "Khazan", Queued, 18_926_085_632, 24_168_810_176);

        var scanner = new SteamScanner(new FixedLibrarySource([library]));
        var meter = new TransferMeter();
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var first = scanner.Scan(clock);
        meter.Observe(first);
        Assert.Null(meter.ActiveAppId);

        // Only Khazan advances.
        temp.WriteManifest(library, 2_680_010, "Khazan", Queued, 19_926_085_632, 24_168_810_176);
        Touch(library, 2_680_010, clock.AddSeconds(10));

        clock = clock.AddSeconds(10);
        var second = scanner.Scan(clock);
        meter.Observe(second);

        Assert.Equal(2_680_010u, meter.ActiveAppId);

        var stamped = second with { ActiveAppId = meter.ActiveAppId };
        Assert.Equal("Khazan", stamped.Headline!.Name);
        Assert.True(stamped.IsLive(stamped.Headline));
        Assert.False(stamped.IsLive(stamped.Pipeline[1]));

        var status = SteamStatusFormatter.Describe(stamped);
        Assert.StartsWith("Downloading Khazan", status.Headline, StringComparison.Ordinal);

        // 1 GB over ten seconds.
        Assert.InRange(meter.NetworkBytesPerSecond, 20_000_000, 100_000_000);
        Assert.NotNull(meter.Eta);
    }

    [Fact]
    public void ADownloadPausedBeforeTheAppStartedIsReportedAsPaused()
    {
        // Steam does not set UpdatePaused, and it stops rewriting the manifest, so a stale manifest
        // on the current download is the only evidence that the user pressed pause earlier.
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 2_680_010, "Khazan", 4 | 2 | 1024, 18_926_085_632, 24_168_810_176);
        Touch(library, 2_680_010, DateTimeOffset.UtcNow.AddHours(-3));

        var snapshot = new SteamScanner(new FixedLibrarySource([library])).Scan(DateTimeOffset.Now);

        Assert.Equal("Khazan", snapshot.Headline!.Name);
        Assert.True(snapshot.IsCurrent(snapshot.Headline));
        Assert.False(snapshot.IsLive(snapshot.Headline));
        Assert.True(snapshot.IsPausedOrStalled(snapshot.Headline));
        Assert.StartsWith("Paused: Khazan", SteamStatusFormatter.Describe(snapshot).Headline, StringComparison.Ordinal);

        // It still counts as unfinished work, so the countdown stays blocked.
        Assert.True(snapshot.HasPendingWork());
    }

    [Fact]
    public void PausingMidSessionFlipsTheStatusToPausedAndBack()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        const long Queued = 4 | 2 | 1024;
        temp.WriteManifest(library, 2_680_010, "Khazan", Queued, 1_000_000_000, 24_168_810_176);

        var scanner = new SteamScanner(new FixedLibrarySource([library]));
        var meter = new TransferMeter();
        var stall = TimeSpan.FromSeconds(60);
        var clock = DateTimeOffset.Now;

        SteamSnapshot Take(DateTimeOffset at)
        {
            var raw = scanner.Scan(at);
            meter.Observe(raw);
            return raw with { ActiveAppId = meter.ActiveAppId, ActiveStalled = meter.IsStalled(at, stall) };
        }

        Take(clock);

        temp.WriteManifest(library, 2_680_010, "Khazan", Queued, 2_000_000_000, 24_168_810_176);
        Touch(library, 2_680_010, clock.AddSeconds(10));
        var downloading = Take(clock.AddSeconds(10));

        Assert.True(downloading.IsLive(downloading.Headline!));
        Assert.StartsWith("Downloading Khazan", SteamStatusFormatter.Describe(downloading).Headline, StringComparison.Ordinal);

        // The user hits pause: the manifest simply stops changing.
        var paused = Take(clock.AddSeconds(120));

        Assert.True(paused.IsPausedOrStalled(paused.Headline!));
        Assert.StartsWith("Paused: Khazan", SteamStatusFormatter.Describe(paused).Headline, StringComparison.Ordinal);

        // Resuming starts the bytes moving again.
        temp.WriteManifest(library, 2_680_010, "Khazan", Queued, 3_000_000_000, 24_168_810_176);
        Touch(library, 2_680_010, clock.AddSeconds(130));
        var resumed = Take(clock.AddSeconds(130));

        Assert.True(resumed.IsLive(resumed.Headline!));
        Assert.StartsWith("Downloading Khazan", SteamStatusFormatter.Describe(resumed).Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshlyWrittenManifestReadsAsLiveBeforeAnyMovementIsMeasured()
    {
        using var temp = new TempFolder();
        var library = temp.CreateLibrary();
        temp.WriteManifest(library, 2_680_010, "Khazan", 4 | 2 | 1024, 18_926_085_632, 24_168_810_176);

        var snapshot = new SteamScanner(new FixedLibrarySource([library])).Scan(DateTimeOffset.Now);

        Assert.True(snapshot.IsLive(snapshot.Headline!));
        Assert.StartsWith("Downloading Khazan", SteamStatusFormatter.Describe(snapshot).Headline, StringComparison.Ordinal);
    }

    private static void Touch(string library, uint appId, DateTimeOffset when) =>
        File.SetLastWriteTimeUtc(
            Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"),
            when.UtcDateTime);

    private sealed class SwitchableLibrarySource(Func<IReadOnlyList<string>> roots) : ILibrarySource
    {
        public IReadOnlyList<string> GetLibraryRoots() => roots();
    }
}

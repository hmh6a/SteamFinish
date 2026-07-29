using SteamFinish.Core.Steam;
using static SteamFinish.Tests.TestData;

namespace SteamFinish.Tests;

public class AppActivityTests
{
    [Fact]
    public void InstalledGameIsNeitherWorkingNorPending()
    {
        var app = App(AppStateFlags.FullyInstalled);

        Assert.False(app.HasJobFlags);
        Assert.False(app.IsQueued);
        Assert.False(app.IsOutstanding);
    }

    [Fact]
    public void RunningGameIsNotTreatedAsDownloadActivity()
    {
        var app = App(AppStateFlags.FullyInstalled | AppStateFlags.AppRunning);

        Assert.False(app.HasJobFlags);
        Assert.False(app.IsOutstanding);
    }

    [Theory]
    [InlineData(AppStateFlags.UpdateRunning | AppStateFlags.Downloading)]
    [InlineData(AppStateFlags.Staging)]
    [InlineData(AppStateFlags.Committing)]
    [InlineData(AppStateFlags.Validating)]
    [InlineData(AppStateFlags.Preallocating)]
    public void NamedJobsCountAsWork(AppStateFlags state)
    {
        Assert.True(App(state).HasJobFlags);
    }

    // These are the exact StateFlags Steam wrote while Khazan was downloading and DARK SOULS III
    // sat stalled behind it. The flags alone cannot tell them apart, and Locked was on the *stalled*
    // one — so both must simply read as outstanding work.
    [Fact]
    public void TheFlagsAloneDoNotSeparateTheLiveDownloadFromTheQueue()
    {
        var live = App(
            AppStateFlags.UpdateStarted | AppStateFlags.UpdateRequired,
            downloaded: 18_926_085_632,
            toDownload: 24_168_810_176,
            appId: 2_680_010,
            name: "The First Berserker: Khazan");

        var stalled = App(
            AppStateFlags.UpdateStarted | AppStateFlags.Locked | AppStateFlags.UpdateRequired,
            downloaded: 269_991_136,
            toDownload: 25_459_276_064,
            appId: 374_320,
            name: "DARK SOULS III");

        Assert.False(live.HasJobFlags);
        Assert.False(stalled.HasJobFlags);
        Assert.True(live.IsQueued);
        Assert.True(stalled.IsQueued);
    }

    [Fact]
    public void PausedUpdateIsPendingButNotWorking()
    {
        var app = App(
            AppStateFlags.UpdateRunning | AppStateFlags.UpdatePaused,
            downloaded: 10,
            toDownload: 100);

        Assert.False(app.HasJobFlags);
        Assert.True(app.IsPaused);
        Assert.True(app.IsPending);
    }

    [Fact]
    public void AvailableUpdateThatHasNotStartedDoesNotBlock()
    {
        // StateFlags 6: installed with an update available. Very common, and it must not
        // keep the countdown from ever starting.
        var app = App(AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired);

        Assert.False(app.HasJobFlags);
        Assert.False(app.IsOutstanding);
    }

    [Fact]
    public void QueuedDownloadWithAssignedBytesIsPending()
    {
        var app = App(AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired, downloaded: 0, toDownload: 5_000);

        Assert.True(app.IsQueued);
    }

    [Fact]
    public void StaleByteCountersOnASettledGameAreIgnored()
    {
        // Steam leaves counters behind after an install; on their own they mean nothing.
        var app = App(AppStateFlags.FullyInstalled, downloaded: 10, toDownload: 5_000);

        Assert.False(app.IsPending);
    }

    [Fact]
    public void DownloadAndInstallSharesAreReportedSeparately()
    {
        // The real Khazan numbers: 9% of the bytes have arrived but only 4% are on disk.
        var app = App(
            AppStateFlags.UpdateStarted,
            downloaded: 2_284_059_904,
            toDownload: 24_168_810_176,
            staged: 2_466_506_354,
            toStage: 56_628_168_942);

        Assert.Equal(0.0945, app.DownloadProgress!.Value, 4);
        Assert.Equal(0.0436, app.InstallProgress!.Value, 4);

        // Steam shows the staged share as the game's percentage, so that is what Progress returns.
        Assert.Equal(app.InstallProgress, app.Progress);
    }

    [Fact]
    public void ProgressFallsBackToTheDownloadShareWhenNothingIsStaged()
    {
        var app = App(AppStateFlags.Downloading, downloaded: 82, toDownload: 100);

        Assert.Equal(0.82, app.Progress!.Value, 3);
        Assert.Equal(18, app.DownloadBytesRemaining);
    }

    [Fact]
    public void InstallingIsReportedOnceEveryByteHasArrived()
    {
        var app = App(AppStateFlags.Staging, downloaded: 100, toDownload: 100, staged: 25, toStage: 100);

        Assert.True(app.IsInstalling);
        Assert.Equal(0, app.DownloadBytesRemaining);
        Assert.Equal(75, app.InstallBytesRemaining);
    }

    [Fact]
    public void ProgressIsUnknownWhenNoByteCountersArePresent()
    {
        Assert.Null(App(AppStateFlags.FullyInstalled).Progress);
    }

    [Fact]
    public void SnapshotReportsPendingWorkForActiveDownloads()
    {
        Assert.True(Downloading().HasPendingWork());
        Assert.False(Idle().HasPendingWork());
    }

    [Fact]
    public void PausedDownloadsBlockUnlessTheUserOptsOut()
    {
        var snapshot = Snapshot(App(AppStateFlags.UpdatePaused, downloaded: 1, toDownload: 100));

        Assert.True(snapshot.HasPendingWork());
        Assert.False(snapshot.HasPendingWork(ignorePaused: true));
    }

    [Fact]
    public void ANonEmptyDownloadFolderCountsAsPendingWork()
    {
        var snapshot = Snapshot(App(AppStateFlags.FullyInstalled)) with { DownloadFolderBusy = true };

        Assert.True(snapshot.HasPendingWork());
    }

    [Fact]
    public void TheHeadlineIsTheAppTheMeterSawMoving()
    {
        // The real bug: the stalled game had far more bytes left, so it won the headline.
        var snapshot = Snapshot(
            App(AppStateFlags.UpdateStarted | AppStateFlags.Locked,
                downloaded: 269_991_136, toDownload: 25_459_276_064, appId: 374_320, name: "DARK SOULS III"),
            App(AppStateFlags.UpdateStarted,
                downloaded: 18_926_085_632, toDownload: 24_168_810_176, appId: 2_680_010, name: "Khazan"))
            with
        { ActiveAppId = 2_680_010 };

        Assert.Equal("Khazan", snapshot.Headline!.Name);
        Assert.Equal(["Khazan", "DARK SOULS III"], snapshot.Pipeline.Select(a => a.Name));
        Assert.True(snapshot.IsLive(snapshot.Headline));
    }

    [Fact]
    public void BeforeAnyMovementIsSeenTheFreshestManifestWins()
    {
        var stale = App(AppStateFlags.UpdateStarted, downloaded: 269_991_136, toDownload: 25_459_276_064,
            appId: 374_320, name: "DARK SOULS III") with
        { ManifestWrittenAt = new DateTimeOffset(2026, 1, 1, 22, 14, 9, TimeSpan.Zero) };

        var fresh = App(AppStateFlags.UpdateStarted, downloaded: 18_926_085_632, toDownload: 24_168_810_176,
            appId: 2_680_010, name: "Khazan") with
        { ManifestWrittenAt = new DateTimeOffset(2026, 1, 1, 23, 35, 52, TimeSpan.Zero) };

        Assert.Equal("Khazan", Snapshot(stale, fresh).Headline!.Name);
    }

    [Fact]
    public void ANamedJobStillWinsWhenNothingHasBeenMeasuredYet()
    {
        var snapshot = Snapshot(
            App(AppStateFlags.UpdateStarted, downloaded: 0, toDownload: 900, appId: 1, name: "Queued"),
            App(AppStateFlags.UpdateRunning | AppStateFlags.Downloading, downloaded: 5, toDownload: 100,
                appId: 2, name: "Running"));

        Assert.Equal("Running", snapshot.Headline!.Name);
    }

    [Fact]
    public void ThePipelineRanksTheRestByWhatIsLeft()
    {
        var snapshot = Snapshot(
            App(AppStateFlags.UpdateStarted, downloaded: 0, toDownload: 10, appId: 1, name: "Live"),
            App(AppStateFlags.UpdateStarted, downloaded: 90, toDownload: 100, appId: 2, name: "Small"),
            App(AppStateFlags.UpdateStarted, downloaded: 10, toDownload: 100, appId: 3, name: "Big"))
            with
        { ActiveAppId = 1 };

        Assert.Equal(["Live", "Big", "Small"], snapshot.Pipeline.Select(a => a.Name));
    }

    [Fact]
    public void PausedGamesSinkToTheBottomOfTheQueue()
    {
        var snapshot = Snapshot(
            App(AppStateFlags.UpdatePaused, downloaded: 0, toDownload: 9_000, appId: 1, name: "Paused"),
            App(AppStateFlags.UpdateStarted, downloaded: 0, toDownload: 100, appId: 2, name: "Queued"))
            with
        { ActiveAppId = 2 };

        Assert.Equal(["Queued", "Paused"], snapshot.Pipeline.Select(a => a.Name));
    }

    [Fact]
    public void TheWaitingListLeavesOutTheLiveDownload()
    {
        var snapshot = Snapshot(
            App(AppStateFlags.UpdateStarted, downloaded: 0, toDownload: 10, appId: 1, name: "Live"),
            App(AppStateFlags.UpdateStarted, downloaded: 0, toDownload: 100, appId: 2, name: "Next"),
            App(AppStateFlags.UpdateStarted, downloaded: 0, toDownload: 50, appId: 3, name: "Later"))
            with
        { ActiveAppId = 1 };

        Assert.Equal(["Next", "Later"], snapshot.Waiting.Select(a => a.Name));
        Assert.DoesNotContain(snapshot.Waiting, a => snapshot.IsLive(a));
    }

    [Fact]
    public void AStalledLiveDownloadReadsAsPaused()
    {
        // Steam leaves the flags alone when you press pause, so the stall verdict is what tells us.
        var snapshot = Snapshot(
            App(AppStateFlags.UpdateStarted | AppStateFlags.UpdateRequired,
                downloaded: 18_926_085_632, toDownload: 24_168_810_176, appId: 1, name: "Khazan"))
            with
        { ActiveAppId = 1, ActiveStalled = true };

        var app = snapshot.Headline!;
        Assert.True(snapshot.IsPausedOrStalled(app));
        Assert.StartsWith("Paused: Khazan", SteamStatusFormatter.Describe(snapshot).Headline, StringComparison.Ordinal);

        // Still unfinished work, so the countdown stays blocked.
        Assert.True(snapshot.HasPendingWork());
    }

    [Fact]
    public void AMovingDownloadIsNotReportedAsPaused()
    {
        var snapshot = Snapshot(
            App(AppStateFlags.UpdateStarted, downloaded: 50, toDownload: 100, appId: 1, name: "Khazan"))
            with
        { ActiveAppId = 1, ActiveStalled = false };

        Assert.False(snapshot.IsPausedOrStalled(snapshot.Headline!));
        Assert.StartsWith("Downloading Khazan", SteamStatusFormatter.Describe(snapshot).Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueTotalsCoverEverythingStillOutstanding()
    {
        var snapshot = Snapshot(
            App(AppStateFlags.UpdateStarted, downloaded: 20, toDownload: 100, appId: 1),
            App(AppStateFlags.UpdateStarted, downloaded: 0, toDownload: 400, appId: 2));

        Assert.Equal(480, snapshot.TotalDownloadBytesRemaining);
        Assert.Equal(500, snapshot.TotalBytesToDownload);
        Assert.Equal(0.04, snapshot.QueueProgress!.Value, 3);
    }

    [Fact]
    public void SnapshotWithoutLibrariesIsNotReliable()
    {
        Assert.False(Unavailable().IsReliable);
        Assert.True(Idle().IsReliable);
    }
}

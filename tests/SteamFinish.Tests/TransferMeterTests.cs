using SteamFinish.Core.Formatting;
using SteamFinish.Core.Steam;
using static SteamFinish.Tests.TestData;

namespace SteamFinish.Tests;

public class TransferMeterTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SteamSnapshot At(DateTimeOffset when, long downloaded, long staged = 0, long toDownload = 1_000_000) =>
        Snapshot(App(
            AppStateFlags.UpdateStarted | AppStateFlags.Locked,
            downloaded: downloaded,
            toDownload: toDownload,
            staged: staged,
            toStage: toDownload * 2)) with
        {
            TakenAt = when,
        };

    [Fact]
    public void TheFirstSnapshotOnlyAnchorsTheWindow()
    {
        var meter = new TransferMeter();

        meter.Observe(At(Start, downloaded: 1_000));

        Assert.Equal(0, meter.NetworkBytesPerSecond);
        Assert.Null(meter.Eta);
    }

    [Fact]
    public void RateIsMeasuredAcrossTheGapSinceTheCountersLastMoved()
    {
        var meter = new TransferMeter();
        meter.Observe(At(Start, downloaded: 0));

        // Steam rewrites the manifest every few seconds; four idle polls then a 4 MB jump.
        meter.Observe(At(Start.AddSeconds(1), downloaded: 0));
        meter.Observe(At(Start.AddSeconds(2), downloaded: 0));
        meter.Observe(At(Start.AddSeconds(3), downloaded: 0));
        meter.Observe(At(Start.AddSeconds(4), downloaded: 4_000_000));

        // 4 MB over the full four seconds, damped by the smoothing factor — not 4 MB in one second.
        Assert.InRange(meter.NetworkBytesPerSecond, 100_000, 1_000_000);
    }

    [Fact]
    public void SustainedTransferConvergesOnTheRealRate()
    {
        var meter = new TransferMeter();
        long downloaded = 0;
        meter.Observe(At(Start, downloaded));

        // One megabyte every second for a minute.
        for (var second = 1; second <= 60; second++)
        {
            downloaded += 1_000_000;
            meter.Observe(At(Start.AddSeconds(second), downloaded, toDownload: 500_000_000));
        }

        Assert.InRange(meter.NetworkBytesPerSecond, 950_000, 1_050_000);
        Assert.Equal(meter.PeakNetworkBytesPerSecond, meter.NetworkBytesPerSecond, 0);
    }

    [Fact]
    public void PeakKeepsTheBestRateAfterTheTransferSlowsDown()
    {
        var meter = new TransferMeter();
        long downloaded = 0;
        meter.Observe(At(Start, downloaded));

        for (var second = 1; second <= 30; second++)
        {
            downloaded += 5_000_000;
            meter.Observe(At(Start.AddSeconds(second), downloaded, toDownload: 900_000_000));
        }

        var peak = meter.PeakNetworkBytesPerSecond;

        for (var second = 31; second <= 60; second++)
        {
            downloaded += 100_000;
            meter.Observe(At(Start.AddSeconds(second), downloaded, toDownload: 900_000_000));
        }

        Assert.True(meter.NetworkBytesPerSecond < peak);
        Assert.Equal(peak, meter.PeakNetworkBytesPerSecond);
    }

    [Fact]
    public void AStalledTransferFallsBackToZero()
    {
        var meter = new TransferMeter();
        meter.Observe(At(Start, downloaded: 0));
        meter.Observe(At(Start.AddSeconds(1), downloaded: 2_000_000));
        Assert.True(meter.NetworkBytesPerSecond > 0);

        // The connection drops: the counters stop moving entirely.
        for (var second = 2; second <= 40; second++)
        {
            meter.Observe(At(Start.AddSeconds(second), downloaded: 2_000_000));
        }

        Assert.Equal(0, meter.NetworkBytesPerSecond);
    }

    [Fact]
    public void DiskRateTracksStagedBytesSeparately()
    {
        var meter = new TransferMeter();
        meter.Observe(At(Start, downloaded: 0, staged: 0));

        for (var second = 1; second <= 30; second++)
        {
            meter.Observe(At(Start.AddSeconds(second), downloaded: second * 1_000_000L, staged: second * 3_000_000L, toDownload: 500_000_000));
        }

        Assert.InRange(meter.DiskBytesPerSecond, 2_800_000, 3_200_000);
        Assert.InRange(meter.NetworkBytesPerSecond, 900_000, 1_100_000);
    }

    [Fact]
    public void EtaFollowsWhatIsLeftAtTheCurrentRate()
    {
        var meter = new TransferMeter();
        long downloaded = 0;
        meter.Observe(At(Start, downloaded, toDownload: 100_000_000));

        for (var second = 1; second <= 30; second++)
        {
            downloaded += 1_000_000;
            meter.Observe(At(Start.AddSeconds(second), downloaded, toDownload: 100_000_000));
        }

        // 70 MB left at roughly 1 MB/s.
        Assert.NotNull(meter.Eta);
        Assert.InRange(meter.Eta!.Value.TotalSeconds, 60, 80);
    }

    [Fact]
    public void CountersDroppingWhenAGameLeavesTheQueueDoesNotProduceANegativeRate()
    {
        var meter = new TransferMeter();
        meter.Observe(At(Start, downloaded: 10_000_000));
        meter.Observe(At(Start.AddSeconds(1), downloaded: 0));

        Assert.True(meter.NetworkBytesPerSecond >= 0);
    }

    [Fact]
    public void APauseIsSpottedOnceTheCountersSitStillLongEnough()
    {
        var meter = new TransferMeter();
        var threshold = TimeSpan.FromSeconds(60);

        meter.Observe(At(Start, downloaded: 0));
        meter.Observe(At(Start.AddSeconds(5), downloaded: 5_000_000));
        Assert.False(meter.IsStalled(Start.AddSeconds(5), threshold));

        // The user presses pause: Steam leaves the flags alone and the counters simply stop.
        for (var second = 6; second <= 90; second++)
        {
            meter.Observe(At(Start.AddSeconds(second), downloaded: 5_000_000));
        }

        Assert.False(meter.IsStalled(Start.AddSeconds(60), threshold));
        Assert.True(meter.IsStalled(Start.AddSeconds(90), threshold));
    }

    [Fact]
    public void ResumingClearsTheStall()
    {
        var meter = new TransferMeter();
        var threshold = TimeSpan.FromSeconds(30);

        meter.Observe(At(Start, downloaded: 0));
        for (var second = 1; second <= 60; second++)
        {
            meter.Observe(At(Start.AddSeconds(second), downloaded: 1_000));
        }

        Assert.True(meter.IsStalled(Start.AddSeconds(60), threshold));

        meter.Observe(At(Start.AddSeconds(61), downloaded: 9_000_000));
        Assert.False(meter.IsStalled(Start.AddSeconds(61), threshold));
    }

    [Fact]
    public void ADownloadAlreadyPausedAtStartupIsStillSpotted()
    {
        // Nothing ever moves, so the anchor is the only reference point available.
        var meter = new TransferMeter();
        for (var second = 0; second <= 120; second++)
        {
            meter.Observe(At(Start.AddSeconds(second), downloaded: 5_000_000));
        }

        Assert.True(meter.IsStalled(Start.AddSeconds(120), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var meter = new TransferMeter();
        meter.Observe(At(Start, downloaded: 0));
        meter.Observe(At(Start.AddSeconds(1), downloaded: 5_000_000));

        meter.Reset();

        Assert.Equal(0, meter.NetworkBytesPerSecond);
        Assert.Equal(0, meter.PeakNetworkBytesPerSecond);
        Assert.Null(meter.Eta);
        Assert.Null(meter.LastMovementAt);
        Assert.False(meter.HasReading);
    }

    [Theory]
    [InlineData(0, "01:30")]
    [InlineData(1, "01:30 +1d")]
    [InlineData(3, "01:30 +3d")]
    public void TheFinishTimeMarksHowManyDaysAhead(int daysAhead, string expected)
    {
        var when = DateTime.Today.AddDays(daysAhead).AddHours(1).AddMinutes(30);

        Assert.Equal(expected, Humanize.FinishTime(new DateTimeOffset(when)));
    }

    [Fact]
    public void UnreliableSnapshotsAreIgnored()
    {
        var meter = new TransferMeter();
        meter.Observe(Unavailable());

        Assert.False(meter.HasReading);
    }
}

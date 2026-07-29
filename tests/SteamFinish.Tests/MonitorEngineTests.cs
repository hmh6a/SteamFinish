using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Steam;
using static SteamFinish.Tests.TestData;

namespace SteamFinish.Tests;

public class MonitorEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private MonitorOptions _options = new()
    {
        ConfirmationWindow = TimeSpan.FromSeconds(45),
        Countdown = TimeSpan.FromSeconds(60),
        RequireDownloadFirst = true,
    };

    private MonitorEngine NewEngine() => new(() => _options);

    [Fact]
    public void StartsDisabledAndIgnoresSnapshots()
    {
        var engine = NewEngine();

        engine.Update(Idle(), Start);

        Assert.Equal(MonitorPhase.Disabled, engine.Phase);
        Assert.False(engine.IsEnabled);
    }

    [Fact]
    public void AnIdleMachineNeverFiresUntilADownloadHasBeenSeen()
    {
        var engine = NewEngine();
        var fired = false;
        engine.ActionDue += () => fired = true;

        engine.Enable();
        for (var minute = 0; minute < 30; minute++)
        {
            engine.Update(Idle(), Start.AddMinutes(minute));
        }

        Assert.Equal(MonitorPhase.WaitingForDownload, engine.Phase);
        Assert.False(fired);
    }

    [Fact]
    public void WithoutTheSafetyGateAnIdleMachineArmsImmediately()
    {
        _options = _options with { RequireDownloadFirst = false };
        var engine = NewEngine();

        engine.Enable();
        engine.Update(Idle(), Start);
        Assert.Equal(MonitorPhase.Confirming, engine.Phase);

        engine.Update(Idle(), Start.AddSeconds(45));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
    }

    [Fact]
    public void RunsTheActionAfterTheQuietPeriodAndTheCountdown()
    {
        var engine = NewEngine();
        var fired = 0;
        engine.ActionDue += () => fired++;

        engine.Enable();
        engine.Update(Downloading(), Start);
        Assert.Equal(MonitorPhase.Busy, engine.Phase);
        Assert.True(engine.IsArmed);

        engine.Update(Idle(), Start.AddSeconds(10));
        Assert.Equal(MonitorPhase.Confirming, engine.Phase);
        Assert.Equal(TimeSpan.FromSeconds(45), engine.ConfirmationRemaining(Start.AddSeconds(10)));

        engine.Update(Idle(), Start.AddSeconds(54));
        Assert.Equal(MonitorPhase.Confirming, engine.Phase);

        engine.Update(Idle(), Start.AddSeconds(55));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
        Assert.Equal(TimeSpan.FromSeconds(60), engine.CountdownRemaining(Start.AddSeconds(55)));

        engine.Update(Idle(), Start.AddSeconds(114));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
        Assert.Equal(0, fired);

        engine.Update(Idle(), Start.AddSeconds(115));
        Assert.Equal(MonitorPhase.Executing, engine.Phase);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void TheQuietPeriodRestartsWheneverSteamGetsBusyAgain()
    {
        var engine = NewEngine();
        engine.Enable();
        engine.Update(Downloading(), Start);

        engine.Update(Idle(), Start.AddSeconds(10));
        engine.Update(Downloading(), Start.AddSeconds(20));
        engine.Update(Idle(), Start.AddSeconds(30));

        // The clock restarted at t+30, so t+70 is still inside the window.
        engine.Update(Idle(), Start.AddSeconds(70));
        Assert.Equal(MonitorPhase.Confirming, engine.Phase);

        engine.Update(Idle(), Start.AddSeconds(76));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
    }

    [Fact]
    public void ANewDownloadDuringTheCountdownCancelsIt()
    {
        var engine = NewEngine();
        CountdownCancelReason? reason = null;
        engine.CountdownCancelled += r => reason = r;

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Idle(), Start.AddSeconds(10));
        engine.Update(Idle(), Start.AddSeconds(60));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);

        engine.Update(Downloading(), Start.AddSeconds(70));

        Assert.Equal(MonitorPhase.Busy, engine.Phase);
        Assert.Equal(CountdownCancelReason.NewActivity, reason);
        Assert.Equal(TimeSpan.Zero, engine.CountdownRemaining(Start.AddSeconds(70)));
    }

    [Fact]
    public void CancellingKeepsMonitoringButWaitsForAFreshDownload()
    {
        var engine = NewEngine();
        var fired = false;
        engine.ActionDue += () => fired = true;

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Idle(), Start.AddSeconds(10));
        engine.Update(Idle(), Start.AddSeconds(60));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);

        Assert.True(engine.CancelCountdown());

        Assert.Equal(MonitorPhase.WaitingForDownload, engine.Phase);
        Assert.True(engine.IsEnabled);
        Assert.False(engine.IsArmed);

        // Staying idle must not start a second countdown on its own.
        for (var minute = 2; minute < 20; minute++)
        {
            engine.Update(Idle(), Start.AddMinutes(minute));
        }

        Assert.Equal(MonitorPhase.WaitingForDownload, engine.Phase);
        Assert.False(fired);

        // A new download re-arms it.
        engine.Update(Downloading(), Start.AddMinutes(20));
        engine.Update(Idle(), Start.AddMinutes(21));
        engine.Update(Idle(), Start.AddMinutes(22));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
    }

    [Fact]
    public void CancelDoesNothingOutsideACountdown()
    {
        var engine = NewEngine();
        engine.Enable();
        engine.Update(Downloading(), Start);

        Assert.False(engine.CancelCountdown());
        Assert.Equal(MonitorPhase.Busy, engine.Phase);
    }

    [Fact]
    public void AnUnreadableSteamStateBlocksAndCancelsTheCountdown()
    {
        var engine = NewEngine();
        CountdownCancelReason? reason = null;
        engine.CountdownCancelled += r => reason = r;

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Idle(), Start.AddSeconds(10));
        engine.Update(Idle(), Start.AddSeconds(60));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);

        engine.Update(Unavailable(), Start.AddSeconds(65));

        Assert.Equal(MonitorPhase.Blocked, engine.Phase);
        Assert.Equal(CountdownCancelReason.StateUnavailable, reason);
    }

    [Fact]
    public void MonitoringResumesOnceSteamCanBeReadAgain()
    {
        var engine = NewEngine();
        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Unavailable(), Start.AddSeconds(5));
        Assert.Equal(MonitorPhase.Blocked, engine.Phase);

        engine.Update(Idle(), Start.AddSeconds(10));
        Assert.Equal(MonitorPhase.Confirming, engine.Phase);

        // The quiet period restarts from the moment the state became readable again.
        engine.Update(Idle(), Start.AddSeconds(56));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
    }

    [Fact]
    public void APausedDownloadHoldsTheCountdownBack()
    {
        var engine = NewEngine();
        var paused = Snapshot(App(AppStateFlags.UpdatePaused, downloaded: 5, toDownload: 100));

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(paused, Start.AddSeconds(10));
        engine.Update(paused, Start.AddSeconds(300));

        Assert.Equal(MonitorPhase.Busy, engine.Phase);
    }

    [Fact]
    public void PausedDownloadsCanBeIgnoredOnRequest()
    {
        _options = _options with { IgnorePaused = true };
        var engine = NewEngine();
        var paused = Snapshot(App(AppStateFlags.UpdatePaused, downloaded: 5, toDownload: 100));

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(paused, Start.AddSeconds(10));
        engine.Update(paused, Start.AddSeconds(60));

        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
    }

    [Fact]
    public void DisablingClearsEverything()
    {
        var engine = NewEngine();
        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Idle(), Start.AddSeconds(10));
        engine.Update(Idle(), Start.AddSeconds(60));

        engine.Disable();

        Assert.Equal(MonitorPhase.Disabled, engine.Phase);
        Assert.False(engine.IsArmed);
        Assert.Null(engine.CountdownEndsAt);

        engine.Update(Idle(), Start.AddSeconds(600));
        Assert.Equal(MonitorPhase.Disabled, engine.Phase);
    }

    [Fact]
    public void TheActionFiresOnlyOnce()
    {
        var engine = NewEngine();
        var fired = 0;
        engine.ActionDue += () => fired++;

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Idle(), Start.AddSeconds(10));
        engine.Update(Idle(), Start.AddSeconds(60));
        engine.Update(Idle(), Start.AddSeconds(130));
        engine.Update(Idle(), Start.AddSeconds(200));
        engine.Update(Idle(), Start.AddSeconds(300));

        Assert.Equal(1, fired);
        Assert.Equal(MonitorPhase.Executing, engine.Phase);
    }

    [Fact]
    public void PhaseChangesAreReportedWithTheirPreviousPhase()
    {
        var engine = NewEngine();
        var transitions = new List<(MonitorPhase From, MonitorPhase To)>();
        engine.PhaseChanged += (from, to) => transitions.Add((from, to));

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Idle(), Start.AddSeconds(10));

        Assert.Equal(
            [
                (MonitorPhase.Disabled, MonitorPhase.WaitingForDownload),
                (MonitorPhase.WaitingForDownload, MonitorPhase.Busy),
                (MonitorPhase.Busy, MonitorPhase.Confirming),
            ],
            transitions);
    }
}

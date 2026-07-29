using SteamFinish.Core.Steam;

namespace SteamFinish.Core.Monitoring;

/// <summary>
/// The decision logic: it consumes snapshots plus the current time and decides when the configured
/// power action should run. It owns no timers and touches no I/O, which keeps it fully testable.
/// </summary>
public sealed class MonitorEngine(Func<MonitorOptions> optionsProvider)
{
    private bool _armed;
    private DateTimeOffset? _idleSince;
    private DateTimeOffset? _countdownEndsAt;

    public MonitorEngine()
        : this(static () => new MonitorOptions())
    {
    }

    /// <summary>Raised whenever <see cref="Phase"/> changes, with the previous phase first.</summary>
    public event Action<MonitorPhase, MonitorPhase>? PhaseChanged;

    /// <summary>Raised once when the countdown reaches zero.</summary>
    public event Action? ActionDue;

    public event Action? CountdownStarted;

    public event Action<CountdownCancelReason>? CountdownCancelled;

    public MonitorPhase Phase { get; private set; } = MonitorPhase.Disabled;

    public bool IsEnabled => Phase != MonitorPhase.Disabled;

    /// <summary>True once a download has been observed, so the action is allowed to fire.</summary>
    public bool IsArmed => _armed;

    public DateTimeOffset? CountdownEndsAt => _countdownEndsAt;

    public TimeSpan CountdownRemaining(DateTimeOffset now) =>
        _countdownEndsAt is { } end && end > now ? end - now : TimeSpan.Zero;

    public TimeSpan ConfirmationRemaining(DateTimeOffset now)
    {
        if (_idleSince is not { } since)
        {
            return optionsProvider().ConfirmationWindow;
        }

        var remaining = optionsProvider().ConfirmationWindow - (now - since);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void Enable()
    {
        if (Phase != MonitorPhase.Disabled)
        {
            return;
        }

        _armed = false;
        _idleSince = null;
        _countdownEndsAt = null;
        SetPhase(optionsProvider().RequireDownloadFirst ? MonitorPhase.WaitingForDownload : MonitorPhase.Confirming);
    }

    public void Disable()
    {
        var wasCountingDown = Phase == MonitorPhase.Countdown;
        _armed = false;
        _idleSince = null;
        _countdownEndsAt = null;
        SetPhase(MonitorPhase.Disabled);

        if (wasCountingDown)
        {
            CountdownCancelled?.Invoke(CountdownCancelReason.User);
        }
    }

    /// <summary>
    /// Stops a running countdown and keeps monitoring. The action is disarmed again, so a fresh
    /// download must appear before another countdown can start — otherwise cancelling would be
    /// pointless while Steam stays idle.
    /// </summary>
    public bool CancelCountdown()
    {
        if (Phase != MonitorPhase.Countdown)
        {
            return false;
        }

        _armed = false;
        _idleSince = null;
        _countdownEndsAt = null;
        SetPhase(MonitorPhase.WaitingForDownload);
        CountdownCancelled?.Invoke(CountdownCancelReason.User);
        return true;
    }

    /// <summary>Feeds a snapshot in and advances the state machine. Safe to call every second.</summary>
    public MonitorPhase Update(SteamSnapshot snapshot, DateTimeOffset now)
    {
        if (Phase is MonitorPhase.Disabled or MonitorPhase.Executing)
        {
            return Phase;
        }

        if (!snapshot.IsReliable)
        {
            AbortCountdown(CountdownCancelReason.StateUnavailable);
            _idleSince = null;
            SetPhase(MonitorPhase.Blocked);
            return Phase;
        }

        var options = optionsProvider();

        if (snapshot.HasPendingWork(options.IgnorePaused))
        {
            _armed = true;
            _idleSince = null;
            AbortCountdown(CountdownCancelReason.NewActivity);
            SetPhase(MonitorPhase.Busy);
            return Phase;
        }

        if (!_armed && options.RequireDownloadFirst)
        {
            _idleSince = null;
            SetPhase(MonitorPhase.WaitingForDownload);
            return Phase;
        }

        _idleSince ??= now;

        if (Phase != MonitorPhase.Countdown)
        {
            if (now - _idleSince.Value < options.ConfirmationWindow)
            {
                SetPhase(MonitorPhase.Confirming);
                return Phase;
            }

            _countdownEndsAt = now + options.Countdown;
            SetPhase(MonitorPhase.Countdown);
            CountdownStarted?.Invoke();
        }

        if (now >= _countdownEndsAt)
        {
            SetPhase(MonitorPhase.Executing);
            ActionDue?.Invoke();
        }

        return Phase;
    }

    private void AbortCountdown(CountdownCancelReason reason)
    {
        if (Phase != MonitorPhase.Countdown)
        {
            return;
        }

        _countdownEndsAt = null;
        CountdownCancelled?.Invoke(reason);
    }

    private void SetPhase(MonitorPhase phase)
    {
        if (Phase == phase)
        {
            return;
        }

        var previous = Phase;
        Phase = phase;
        PhaseChanged?.Invoke(previous, phase);
    }
}

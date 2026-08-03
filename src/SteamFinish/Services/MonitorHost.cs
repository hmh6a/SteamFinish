using System.IO;
using System.Windows.Threading;
using SteamFinish.Core.Logging;
using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Notifications;
using SteamFinish.Core.Power;
using SteamFinish.Core.Settings;
using SteamFinish.Core.Steam;

namespace SteamFinish.Services;

/// <summary>
/// Drives <see cref="MonitorEngine"/> from a one-second dispatcher timer: it rescans Steam on a
/// background thread at the configured interval, feeds snapshots to the engine, the transfer meter,
/// the session recorder and the Telegram notifier, and runs the power action when the countdown ends.
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly DownloadScanner _scanner;
    private readonly IPowerController _power;
    private readonly Func<AppSettings> _settings;
    private readonly ILog _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly List<FileSystemWatcher> _watchers = [];

    private string[] _watchedRoots = [];
    private volatile bool _dirty = true;
    private bool _scanning;
    private bool _keepLive;
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;
    private bool _disposed;

    public MonitorHost(
        DownloadScanner scanner,
        MonitorEngine engine,
        IPowerController power,
        TelegramNotifier telegram,
        Func<AppSettings> settings,
        ILog log)
    {
        _scanner = scanner;
        Engine = engine;
        _power = power;
        Telegram = telegram;
        _settings = settings;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;

        Engine.ActionDue += OnActionDue;
        Engine.CountdownStarted += OnCountdownStarted;
        Engine.CountdownCancelled += OnCountdownCancelled;

        // Button presses arrive on a background thread; everything else here is dispatcher-bound.
        Telegram.RemoteDecisionMade += decision => _dispatcher.BeginInvoke(() => ApplyRemoteDecision(decision));
    }

    public MonitorEngine Engine { get; }

    public TelegramNotifier Telegram { get; }

    /// <summary>Live network and disk rates derived from consecutive snapshots.</summary>
    public TransferMeter Meter { get; } = new();

    /// <summary>What has been downloaded since the current batch began.</summary>
    public DownloadSession Session { get; } = new();

    /// <summary>The most recent scan, or <c>null</c> before the first one completes.</summary>
    public DownloadSnapshot? LastSnapshot { get; private set; }

    /// <summary>
    /// Keeps scanning while the window is on screen so the progress read-out stays live even with
    /// monitoring off. Nothing is read from disk once this is false and monitoring is off.
    /// </summary>
    public bool KeepLive
    {
        get => _keepLive;
        set
        {
            if (_keepLive == value)
            {
                return;
            }

            _keepLive = value;
            SyncTimer();

            if (value)
            {
                RefreshNow();
            }
        }
    }

    public event Action<DownloadSnapshot>? SnapshotUpdated;

    /// <summary>Fires once per second while scanning, so the countdown display can refresh.</summary>
    public event Action? Tick;

    public event Action<PowerAction>? ActionStarting;

    public event Action<string>? ActionFailed;

    public void Enable()
    {
        if (Engine.IsEnabled)
        {
            return;
        }

        _log.Info("Monitoring enabled.");
        Meter.Reset();
        Session.Reset();
        Telegram.Reset();
        Engine.Enable();
        _dirty = true;
        SyncTimer();
        RefreshNow();
    }

    public void Disable()
    {
        if (!Engine.IsEnabled)
        {
            return;
        }

        _log.Info("Monitoring disabled.");
        Engine.Disable();
        Session.Reset();
        SyncTimer();
    }

    /// <summary>
    /// Stops the countdown but keeps watching. Also asks Windows to abort a system shutdown that may
    /// already be pending, which is a no-op in the normal case.
    /// </summary>
    public void CancelCountdown()
    {
        if (Engine.CancelCountdown())
        {
            _log.Info("Countdown cancelled by the user.");
            _power.AbortPendingShutdown();
        }
    }

    /// <summary>Runs a single scan on demand, even while monitoring is off.</summary>
    public void RefreshNow()
    {
        if (!_scanning)
        {
            BeginScan();
        }
    }

    /// <summary>
    /// Re-evaluates whether scanning should be running. Call this after the Telegram settings change,
    /// since progress messages keep the scanner alive on their own.
    /// </summary>
    public void RefreshSchedule()
    {
        SyncTimer();

        if (_timer.IsEnabled)
        {
            RefreshNow();
        }
    }

    /// <summary>
    /// True when Telegram is configured to report on downloads. Those messages describe the download
    /// itself rather than the monitoring session, so they must not depend on monitoring being on.
    /// </summary>
    private bool TelegramWantsUpdates
    {
        get
        {
            var telegram = _settings().Telegram;
            return telegram.IsUsable && (telegram.NotifyOnProgress || telegram.NotifyOnStart);
        }
    }

    private void SyncTimer()
    {
        var shouldRun = Engine.IsEnabled || _keepLive || TelegramWantsUpdates;

        if (shouldRun && !_timer.IsEnabled)
        {
            _timer.Start();
        }
        else if (!shouldRun && _timer.IsEnabled)
        {
            _timer.Stop();
            StopWatchers();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        var interval = TimeSpan.FromSeconds(_settings().PollIntervalSeconds);

        if (!_scanning && (_dirty || now - _lastScan >= interval))
        {
            BeginScan();
        }

        if (LastSnapshot is { } snapshot)
        {
            Engine.Update(snapshot, now);
        }

        Tick?.Invoke();
    }

    private void BeginScan()
    {
        _scanning = true;
        _dirty = false;
        _lastScan = DateTimeOffset.Now;

        // File access happens off the UI thread; results are marshalled back before use.
        Task.Run(() =>
        {
            try
            {
                return _scanner.Scan(DateTimeOffset.Now);
            }
            catch (Exception e)
            {
                _log.Error("Steam scan failed.", e);
                return DownloadSnapshot.Unavailable(DateTimeOffset.Now, e.Message);
            }
        }).ContinueWith(
            task =>
            {
                try
                {
                    _dispatcher.BeginInvoke(() => CompleteScan(task.Result));
                }
                catch (TaskCanceledException)
                {
                    // The dispatcher shut down while the scan was running; the app is closing.
                    _scanning = false;
                }
            },
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private void CompleteScan(DownloadSnapshot snapshot)
    {
        _scanning = false;
        if (_disposed)
        {
            return;
        }

        Meter.Observe(snapshot);

        // The meter is the only thing that knows which app is really moving — and whether it has
        // stopped — so stamp its verdict onto the snapshot before anyone else looks at it.
        snapshot = snapshot with
        {
            ActiveAppId = Meter.ActiveAppId,
            ActiveStalled = Meter.IsStalled(
                snapshot.TakenAt,
                TimeSpan.FromSeconds(_settings().StalledAfterSeconds)),
        };
        LastSnapshot = snapshot;

        if (Engine.IsEnabled)
        {
            Session.Observe(snapshot);
            SyncWatchers(snapshot.LibraryRoots);
        }

        // Download progress is reported whether or not monitoring is armed — the messages are about
        // the download, not about the shutdown.
        Telegram.OnSnapshot(snapshot, Meter);

        SnapshotUpdated?.Invoke(snapshot);
    }

    private void OnCountdownStarted()
    {
        var settings = _settings();
        Telegram.OnCountdownStarted(
            Session.Summarize(DateTimeOffset.Now),
            settings.Action,
            settings.CountdownSeconds);
    }

    private void OnCountdownCancelled(CountdownCancelReason reason)
    {
        Telegram.OnCountdownCancelled(_settings().Action, reason);
    }

    /// <summary>Applies a decision taken from the Telegram buttons.</summary>
    private void ApplyRemoteDecision(RemoteDecision decision)
    {
        if (decision == RemoteDecision.Now)
        {
            // Not a cancel: this jumps straight to the action, so nothing is announced as cancelled.
            if (!Engine.RunNow())
            {
                _log.Warn("A remote 'run now' arrived after the countdown had already ended.");
            }

            return;
        }

        _log.Info("Countdown cancelled from Telegram.");
        Engine.CancelCountdown();
        _power.AbortPendingShutdown();
    }

    private void OnActionDue()
    {
        var settings = _settings();
        var action = settings.Action;
        _log.Info($"Countdown finished. Running {action}.");
        ActionStarting?.Invoke(action);

        var executed = true;
        try
        {
            _power.Execute(action, settings.ForceCloseApps);
        }
        catch (PowerActionException e)
        {
            executed = false;
            _log.Error("The power action could not be started.", e);
            ActionFailed?.Invoke(e.Message);
        }

        // Leaves the chat showing the outcome and takes the buttons away.
        Telegram.OnResolvedLocally(action, executed);

        // Sleep and hibernate return control once the PC wakes up again; leave monitoring off then.
        Engine.Disable();
        Session.Reset();
        SyncTimer();
    }

    /// <summary>
    /// Watches the manifest folder and the download folder of every library so completion is noticed
    /// immediately instead of on the next poll. Events only set a flag; the poll does the real work.
    /// </summary>
    private void SyncWatchers(IReadOnlyList<string> roots)
    {
        if (_watchedRoots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        StopWatchers();
        _watchedRoots = roots.ToArray();

        foreach (var root in roots)
        {
            var steamApps = Path.Combine(root, "steamapps");
            AddWatcher(steamApps, "appmanifest_*.acf", NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size);
            AddWatcher(Path.Combine(steamApps, "downloading"), "*", NotifyFilters.FileName | NotifyFilters.DirectoryName);
        }
    }

    private void AddWatcher(string path, string filter, NotifyFilters notifyFilters)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(path, filter)
            {
                NotifyFilter = notifyFilters,
                IncludeSubdirectories = false,
            };

            void MarkDirty(object? sender, FileSystemEventArgs e) => _dirty = true;

            watcher.Changed += MarkDirty;
            watcher.Created += MarkDirty;
            watcher.Deleted += MarkDirty;
            watcher.Renamed += (_, _) => _dirty = true;
            watcher.Error += (_, _) => _dirty = true;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Losing a watcher only costs responsiveness; polling still detects the change.
            _log.Warn($"Cannot watch '{path}': {e.Message}");
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _watchedRoots = [];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Engine.ActionDue -= OnActionDue;
        Engine.CountdownStarted -= OnCountdownStarted;
        Engine.CountdownCancelled -= OnCountdownCancelled;
        StopWatchers();
    }
}

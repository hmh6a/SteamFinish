using System.ComponentModel;
using System.Media;
using Microsoft.Win32;
using System.Reflection;
using System.Windows;
using SteamFinish.Core;
using SteamFinish.Core.Control;
using SteamFinish.Core.Localization;
using SteamFinish.Core.Logging;
using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Notifications;
using SteamFinish.Core.Power;
using SteamFinish.Core.Settings;
using SteamFinish.Core.Startup;
using SteamFinish.Core.Steam;
using SteamFinish.Core.Updates;
using SteamFinish.Core.Xbox;
using SteamFinish.Services;
using SteamFinish.ViewModels;

namespace SteamFinish;

/// <summary>Composition root: builds the services, the tray icon and the window, and wires them up.</summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private FileLog? _log;
    private AppSettings? _settings;
    private TelegramClient? _telegramClient;
    private SteamDownloadController? _downloads;
    private UpdateService? _updates;
    private MonitorHost? _host;
    private MainViewModel? _viewModel;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private bool _hideHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.Acquire();
        if (_instance is null)
        {
            // Another copy is already running and has been asked to show itself.
            Shutdown();
            return;
        }

        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(ShowMainWindow));

        AppPaths.EnsureDataFolder();
        _log = new FileLog(AppPaths.LogFile);
        var store = new SettingsStore(AppPaths.SettingsFile, _log);
        _settings = store.Load();
        _log.Enabled = _settings.EnableLogging;
        _log.Info($"SteamFinish {Assembly.GetExecutingAssembly().GetName().Version} started.");

        // Applied before any window is built, so the first paint is already right.
        Loc.Use(_settings.Language);
        ThemeManager.Apply(_settings.Theme);

        // Windows can flip to dark while the app is open; only matters on the "System" setting.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                Dispatcher.BeginInvoke(ThemeManager.RefreshIfFollowingSystem);
            }
        };

        var librarySource = new AutoLibrarySource(
            () => _settings!.AutoDetectLibraries,
            () => _settings!.ManualLibraries);
        var steamScanner = new SteamScanner(librarySource, _log);
        var xboxScanner = new XboxScanner(log: _log);
        var scanner = new DownloadScanner(
            steamScanner,
            xboxScanner,
            () => _settings!.WatchSteam,
            () => _settings!.WatchXbox);
        var engine = new MonitorEngine(() => _settings!.ToMonitorOptions());
        var power = new WindowsPowerController(_log);

        _telegramClient = new TelegramClient(_log);
        var telegram = new TelegramNotifier(() => _settings!.Telegram, _telegramClient, _log, _telegramClient);

        // Pausing a download is a Steam-only trick, and one Steam allows only through its own
        // JavaScript — see SteamCefBridge for why there is no simpler route.
        _downloads = new SteamDownloadController(_log);

        _host = new MonitorHost(
            scanner, engine, power, telegram, () => _settings!, _log, _downloads, _telegramClient);
        _updates = new UpdateService(_settings.UpdateRepository, _log);
        _viewModel = new MainViewModel(
            store, _settings, _host, librarySource, _telegramClient, _updates, _downloads, _log);

        _tray = new TrayIcon();
        _tray.ShowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;
        _tray.ToggleRequested += () => _viewModel.ToggleMonitoring();
        _tray.CancelRequested += () => _viewModel.CancelCountdown();

        _viewModel.StateChanged += OnStateChanged;
        _viewModel.Notification += Notify;
        engine.CountdownStarted += OnCountdownStarted;
        engine.CountdownCancelled += OnCountdownCancelled;
        _host.ActionStarting += OnActionStarting;

        _window = new MainWindow(_viewModel);
        _window.Closing += OnWindowClosing;

        // The progress read-out stays live while the window is on screen; hiding it to the tray
        // stops the scanning again unless monitoring is on.
        _window.IsVisibleChanged += (_, _) =>
            _host.KeepLive = _window.IsVisible && _settings.LiveStatusWhileOpen;

        var startHidden = _settings.StartMinimized
                          || e.Args.Contains(StartupRegistrar.StartMinimizedArgument, StringComparer.OrdinalIgnoreCase);
        if (!startHidden)
        {
            ShowMainWindow();
        }

        OnStateChanged();

        // Starts the poll timer and the Telegram command listener when the settings call for them.
        // Doing it here rather than leaving it to the window means starting minimized still answers
        // /pause from the phone.
        _host.RefreshSchedule();

        // One scan up front so the window and the library list are populated straight away.
        _host.RefreshNow();
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _settings is null || !_settings.CloseToTray)
        {
            ExitApplication();
            return;
        }

        e.Cancel = true;
        _window?.Hide();

        if (!_hideHintShown)
        {
            _hideHintShown = true;
            Notify(Loc.Get("Tray.StillRunning"), Loc.Get("Tray.StillRunningBody"), NotificationKind.Info);
        }
    }

    private void OnStateChanged()
    {
        if (_viewModel is null || _tray is null)
        {
            return;
        }

        _tray.SetTooltip(_viewModel.TrayTooltip);
        _tray.UpdateMenu(_viewModel.IsMonitoring, _viewModel.IsCountingDown, _viewModel.CancelButtonText);
    }

    private void OnCountdownStarted()
    {
        if (_settings is null || _viewModel is null)
        {
            return;
        }

        Notify(
            Loc.Get("Tray.DownloadsFinished"),
            Loc.F("Tray.CountdownBody", _viewModel.ActionName, _settings.CountdownSeconds),
            NotificationKind.Warning);

        if (_settings.SoundNotification)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    private void OnCountdownCancelled(CountdownCancelReason reason)
    {
        switch (reason)
        {
            case CountdownCancelReason.NewActivity:
                Notify(Loc.Get("Tray.CountdownCancelled"), Loc.Get("Tray.CancelledActivity"), NotificationKind.Info);
                break;

            case CountdownCancelReason.StateUnavailable:
                Notify(Loc.Get("Tray.CountdownCancelled"), Loc.Get("Tray.CancelledUnavailable"), NotificationKind.Warning);
                break;

            default:
                // The user cancelled it themselves; no need to tell them.
                break;
        }
    }

    private void OnActionStarting(PowerAction action)
    {
        if (_settings?.SoundNotification == true)
        {
            SystemSounds.Asterisk.Play();
        }
    }

    private void Notify(string title, string message, NotificationKind kind)
    {
        // Errors are always surfaced; routine updates respect the notification setting.
        if (_tray is null || (kind != NotificationKind.Error && _settings?.TrayNotifications != true))
        {
            return;
        }

        _tray.ShowBalloon(title, message, kind);
    }

    private void ExitApplication()
    {
        _exiting = true;
        _viewModel?.SaveNow();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("SteamFinish exiting.");
        _viewModel?.Dispose();
        _host?.Dispose();
        _telegramClient?.Dispose();
        _downloads?.Dispose();
        _updates?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

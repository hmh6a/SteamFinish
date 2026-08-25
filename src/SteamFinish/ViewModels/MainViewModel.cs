using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SteamFinish.Core;
using SteamFinish.Core.Control;
using SteamFinish.Core.Formatting;
using SteamFinish.Core.Localization;
using SteamFinish.Core.Logging;
using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Notifications;
using SteamFinish.Core.Power;
using SteamFinish.Core.Settings;
using SteamFinish.Core.Startup;
using SteamFinish.Core.Steam;
using SteamFinish.Core.Updates;
using SteamFinish.Services;

namespace SteamFinish.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly MonitorHost _host;
    private readonly AutoLibrarySource _librarySource;
    private readonly FileLog? _log;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dispatcher _dispatcher;

    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _removeLibraryCommand;
    private readonly RelayCommand _checkUpdateCommand;
    private readonly RelayCommand _installUpdateCommand;
    private readonly RelayCommand _findChatCommand;
    private readonly RelayCommand _confirmChatCommand;
    private readonly RelayCommand _cancelPairingCommand;
    private readonly RelayCommand _addChatIdCommand;
    private readonly RelayCommand _removeChatIdCommand;
    private readonly RelayCommand _testTelegramCommand;
    private readonly RelayCommand _enableDownloadControlCommand;
    private readonly RelayCommand _restartSteamCommand;

    private string _statusHeadline = "Steam has not been checked yet";
    private string _statusDetail = "Enable monitoring to start watching downloads.";
    private string _phaseText = "Monitoring is off";
    private Brush _phaseBrush = Brushes.Gray;
    private bool _isMonitoring;
    private bool _isCountingDown;
    private int _countdownDisplay;
    private string _toggleButtonText = "Enable Monitoring";
    private string _libraryMessage = string.Empty;
    private string? _selectedManualLibrary;

    private bool _hasActiveDownload;
    private string _downloadingName = "—";
    private string _downloadingState = string.Empty;
    private double _downloadPercent;
    private double _installPercent;
    private string _downloadBytesText = string.Empty;
    private string _installBytesText = string.Empty;
    private string _networkText = "—";
    private string _peakText = "—";
    private string _diskText = "—";
    private string _etaText = "—";
    private string _finishesAtText = "—";
    private string _totalLeftText = "—";
    private string _queueSummary = string.Empty;
    private bool _isPaused;
    private string _platformText = string.Empty;
    private bool _hasPlatform;
    private Brush _platformBrush = Brushes.Gray;
    private Brush _platformSoftBrush = Brushes.Transparent;

    private string _chatIdInput = string.Empty;
    private ChatEntryViewModel? _selectedChatId;
    private string _telegramStatus = string.Empty;
    private bool _telegramBusy;
    private bool _showBotToken;

    private readonly ITelegramChatFinder _chatFinder;
    private readonly IDownloadController? _downloads;
    private string _downloadControlStatus = string.Empty;
    private readonly UpdateService? _updates;
    private UpdateInfo? _pendingUpdate;
    private string _updateStatus = string.Empty;
    private bool _updateAvailable;
    private bool _updateBusy;
    private CancellationTokenSource? _pairing;
    private IDisposable? _commandsHeldForPairing;
    private DiscoveredChat? _pairedChat;
    private long? _pairedMessageId;
    private PairingStage _pairingStage = PairingStage.Idle;
    private string _pairingStatus = string.Empty;
    private string _pairingCode = string.Empty;

    public MainViewModel(
        SettingsStore store,
        AppSettings settings,
        MonitorHost host,
        AutoLibrarySource librarySource,
        ITelegramChatFinder chatFinder,
        UpdateService? updates,
        IDownloadController? downloads,
        FileLog? log)
    {
        _updates = updates;
        _store = store;
        _settings = settings;
        _host = host;
        _librarySource = librarySource;
        _chatFinder = chatFinder;
        _downloads = downloads;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _countdownDisplay = settings.CountdownSeconds;
        ManualLibraries = new ObservableCollection<string>(settings.ManualLibraries);
        TelegramChatIds = new ObservableCollection<ChatEntryViewModel>(
            settings.Telegram.ChatIds.Select(id => new ChatEntryViewModel(
                id,
                settings.Telegram.ChatLabels.GetValueOrDefault(id))));

        // Nothing to hide before a token is entered, so start revealed only when the box is empty.
        _showBotToken = !TelegramOptions.LooksLikeToken(settings.Telegram.BotToken);

        _cancelCommand = new RelayCommand(CancelCountdown, () => _isCountingDown);
        _removeLibraryCommand = new RelayCommand(RemoveLibrary, () => _selectedManualLibrary is not null);
        _addChatIdCommand = new RelayCommand(AddChatId, () => !string.IsNullOrWhiteSpace(_chatIdInput));
        _removeChatIdCommand = new RelayCommand(RemoveChatId, () => _selectedChatId is not null);
        _testTelegramCommand = new RelayCommand(TestTelegram, () => !_telegramBusy);
        _enableDownloadControlCommand = new RelayCommand(EnableDownloadControl, () => !_telegramBusy);
        _restartSteamCommand = new RelayCommand(RestartSteam, () => !_telegramBusy);
        _checkUpdateCommand = new RelayCommand(() => _ = CheckUpdatesAsync(announceUpToDate: true), () => !_updateBusy);
        _installUpdateCommand = new RelayCommand(InstallUpdate, () => !_updateBusy && _pendingUpdate is not null);
        _findChatCommand = new RelayCommand(FindChat, () => _pairingStage != PairingStage.Listening);
        _confirmChatCommand = new RelayCommand(ConfirmChat, () => _pairingStage == PairingStage.AwaitingConfirmation);
        _cancelPairingCommand = new RelayCommand(CancelPairing, () => _pairingStage != PairingStage.Idle);
        OpenBotFatherCommand = new RelayCommand(() => OpenLink("https://t.me/BotFather"));
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
        RefreshCommand = new RelayCommand(() => _host.RefreshNow());
        AddLibraryCommand = new RelayCommand(AddLibrary);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        // Settings are written a moment after the last edit rather than on every keystroke.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _saveTimer.Tick += (_, _) => SaveNow();

        _host.SnapshotUpdated += OnSnapshotUpdated;
        _host.Tick += RefreshPhase;
        _host.ActionFailed += OnActionFailed;
        _host.Engine.PhaseChanged += (_, _) => RefreshPhase();
        _host.Telegram.SendFailed += OnTelegramSendFailed;

        // Keep the registry entry and the saved setting in step, in case one was changed elsewhere.
        _settings.StartWithWindows = StartupRegistrar.IsEnabled();

        DescribeKnownChats();

        if (_settings.CheckForUpdates)
        {
            // Quiet on start-up: it only speaks up when there is actually something newer.
            _ = CheckUpdatesAsync(announceUpToDate: false);
        }

        // Brushes resolved in code (the phase dot, the queue accents) hold a reference to the old
        // palette, so they are re-fetched after a swap.
        ThemeManager.ThemeChanged += OnThemeChanged;

        RefreshPhase();
    }

    /// <summary>Raised for messages worth surfacing in the tray (title, message, kind).</summary>
    public event Action<string, string, NotificationKind>? Notification;

    /// <summary>Raised whenever the monitoring state changes, so the tray menu can follow.</summary>
    public event Action? StateChanged;

    // ---------------------------------------------------------------- Status

    public string StatusHeadline
    {
        get => _statusHeadline;
        private set => SetProperty(ref _statusHeadline, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string PhaseText
    {
        get => _phaseText;
        private set => SetProperty(ref _phaseText, value);
    }

    public Brush PhaseBrush
    {
        get => _phaseBrush;
        private set => SetProperty(ref _phaseBrush, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set => SetProperty(ref _isMonitoring, value);
    }

    public bool IsCountingDown
    {
        get => _isCountingDown;
        private set
        {
            if (SetProperty(ref _isCountingDown, value))
            {
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Seconds left in the countdown, or the configured length while it is not running.</summary>
    public int CountdownDisplay
    {
        get => _countdownDisplay;
        private set => SetProperty(ref _countdownDisplay, value);
    }

    public string ToggleButtonText
    {
        get => _toggleButtonText;
        private set => SetProperty(ref _toggleButtonText, value);
    }

    public string ActionName => DescribeAction(_settings.Action);

    public string CountdownCaption =>
        IsCountingDown ? Loc.F("Countdown.ActionIn", ActionName) : Loc.Get("Countdown.Title");

    public string CancelButtonText => Loc.F("Button.CancelAction", ActionName);

    public string TrayTooltip =>
        IsMonitoring ? $"SteamFinish · {PhaseText}" : Loc.Get("Tray.MonitoringOff");

    // ---------------------------------------------------------------- Live transfer

    /// <summary>True while Steam is actually moving bytes for something.</summary>
    public bool HasActiveDownload
    {
        get => _hasActiveDownload;
        private set => SetProperty(ref _hasActiveDownload, value);
    }

    public string DownloadingName
    {
        get => _downloadingName;
        private set => SetProperty(ref _downloadingName, value);
    }

    public string DownloadingState
    {
        get => _downloadingState;
        private set => SetProperty(ref _downloadingState, value);
    }

    /// <summary>Network transfer share — Steam's "Downloading data" bar, on its own scale.</summary>
    public double DownloadPercent
    {
        get => _downloadPercent;
        private set
        {
            if (SetProperty(ref _downloadPercent, value))
            {
                OnPropertyChanged(nameof(DownloadPercentText));
            }
        }
    }

    public string DownloadPercentText => $"{_downloadPercent:0}%";

    /// <summary>Disk write share — Steam's "Installing files" bar, separate from the download bar.</summary>
    public double InstallPercent
    {
        get => _installPercent;
        private set
        {
            if (SetProperty(ref _installPercent, value))
            {
                OnPropertyChanged(nameof(InstallPercentText));
            }
        }
    }

    public string InstallPercentText => $"{_installPercent:0}%";

    public string DownloadBytesText
    {
        get => _downloadBytesText;
        private set => SetProperty(ref _downloadBytesText, value);
    }

    public string InstallBytesText
    {
        get => _installBytesText;
        private set => SetProperty(ref _installBytesText, value);
    }

    public string NetworkText
    {
        get => _networkText;
        private set => SetProperty(ref _networkText, value);
    }

    public string PeakText
    {
        get => _peakText;
        private set => SetProperty(ref _peakText, value);
    }

    public string DiskText
    {
        get => _diskText;
        private set => SetProperty(ref _diskText, value);
    }

    /// <summary>How much longer everything outstanding will take.</summary>
    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    /// <summary>The clock time the queue is expected to be finished at.</summary>
    public string FinishesAtText
    {
        get => _finishesAtText;
        private set => SetProperty(ref _finishesAtText, value);
    }

    public string TotalLeftText
    {
        get => _totalLeftText;
        private set => SetProperty(ref _totalLeftText, value);
    }

    /// <summary>Which launcher the headline download belongs to — "Steam" or "Xbox".</summary>
    public string PlatformText
    {
        get => _platformText;
        private set => SetProperty(ref _platformText, value);
    }

    /// <summary>Steam blue or Xbox green, for the badge beside the status line.</summary>
    public Brush PlatformBrush
    {
        get => _platformBrush;
        private set => SetProperty(ref _platformBrush, value);
    }

    public Brush PlatformSoftBrush
    {
        get => _platformSoftBrush;
        private set => SetProperty(ref _platformSoftBrush, value);
    }

    /// <summary>False when nothing is outstanding, so the badge is not shown over an empty status.</summary>
    public bool HasPlatform
    {
        get => _hasPlatform;
        private set => SetProperty(ref _hasPlatform, value);
    }

    /// <summary>True when the live download is not moving, whether paused by hand or stalled.</summary>
    public bool IsDownloadPaused
    {
        get => _isPaused;
        private set => SetProperty(ref _isPaused, value);
    }

    public string QueueSummary
    {
        get => _queueSummary;
        private set => SetProperty(ref _queueSummary, value);
    }

    public bool HasQueue => Queue.Count > 0;

    /// <summary>Everything waiting behind the current download; the live one is not listed here.</summary>
    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];

    // ---------------------------------------------------------------- Commands

    public RelayCommand ToggleMonitoringCommand { get; }

    public RelayCommand CancelCommand => _cancelCommand;

    public RelayCommand RefreshCommand { get; }

    public RelayCommand AddLibraryCommand { get; }

    public RelayCommand RemoveLibraryCommand => _removeLibraryCommand;

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand AddChatIdCommand => _addChatIdCommand;

    public RelayCommand RemoveChatIdCommand => _removeChatIdCommand;

    public RelayCommand TestTelegramCommand => _testTelegramCommand;

    public RelayCommand FindChatCommand => _findChatCommand;

    public RelayCommand ConfirmChatCommand => _confirmChatCommand;

    public RelayCommand CancelPairingCommand => _cancelPairingCommand;

    public RelayCommand OpenBotFatherCommand { get; }

    // ---------------------------------------------------------------- Settings

    public PowerAction SelectedAction
    {
        get => _settings.Action;
        set
        {
            if (_settings.Action == value)
            {
                return;
            }

            _settings.Action = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionName), nameof(CountdownCaption), nameof(CancelButtonText));
            RefreshPhase();
            ScheduleSave();
        }
    }

    public int CountdownSeconds
    {
        get => _settings.CountdownSeconds;
        set
        {
            _settings.CountdownSeconds = Math.Clamp(value, AppSettings.MinCountdownSeconds, AppSettings.MaxCountdownSeconds);
            OnPropertyChanged();
            RefreshPhase();
            ScheduleSave();
        }
    }

    public int ConfirmationSeconds
    {
        get => _settings.ConfirmationSeconds;
        set
        {
            _settings.ConfirmationSeconds = Math.Clamp(value, AppSettings.MinConfirmationSeconds, AppSettings.MaxConfirmationSeconds);
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            if (!StartupRegistrar.SetEnabled(value))
            {
                Notification?.Invoke(
                    "Start with Windows",
                    "Windows would not let SteamFinish change the startup entry.",
                    NotificationKind.Warning);
                OnPropertyChanged();
                return;
            }

            _settings.StartWithWindows = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set => SetSetting(_settings.StartMinimized == value, () => _settings.StartMinimized = value);
    }

    public bool TrayNotifications
    {
        get => _settings.TrayNotifications;
        set => SetSetting(_settings.TrayNotifications == value, () => _settings.TrayNotifications = value);
    }

    public bool SoundNotification
    {
        get => _settings.SoundNotification;
        set => SetSetting(_settings.SoundNotification == value, () => _settings.SoundNotification = value);
    }

    /// <summary>The app's own interface language; applies immediately, without a restart.</summary>
    public UiLanguage UiLanguage
    {
        get => _settings.Language;
        set
        {
            if (SetSetting(_settings.Language == value, () => _settings.Language = value))
            {
                Loc.Use(value);
                OnPropertyChanged(nameof(FlowDirection));
                RefreshLocalizedText();
            }
        }
    }

    /// <summary>Arabic lays the whole window out right-to-left.</summary>
    public FlowDirection FlowDirection =>
        Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    // ---------------------------------------------------------------- Updates

    /// <summary>"You are running 1.2.0" — the build actually executing.</summary>
    public string CurrentVersionText => Loc.F("Update.Current", UpdateService.CurrentVersion);

    /// <summary>Short form for the header strip.</summary>
    public string VersionBadge => "v" + UpdateService.CurrentVersion;

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    /// <summary>True once a newer release has been found, which reveals the install button.</summary>
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetProperty(ref _updateAvailable, value);
    }

    public bool UpdateBusy
    {
        get => _updateBusy;
        private set
        {
            if (SetProperty(ref _updateBusy, value))
            {
                _checkUpdateCommand.RaiseCanExecuteChanged();
                _installUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand CheckUpdateCommand => _checkUpdateCommand;

    public RelayCommand InstallUpdateCommand => _installUpdateCommand;

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set => SetSetting(_settings.CheckForUpdates == value, () => _settings.CheckForUpdates = value);
    }

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (SetSetting(_settings.Theme == value, () => _settings.Theme = value))
            {
                ThemeManager.Apply(value);
            }
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set => SetSetting(_settings.CloseToTray == value, () => _settings.CloseToTray = value);
    }

    public bool LiveStatusWhileOpen
    {
        get => _settings.LiveStatusWhileOpen;
        set
        {
            if (SetSetting(_settings.LiveStatusWhileOpen == value, () => _settings.LiveStatusWhileOpen = value))
            {
                _host.KeepLive = value;
            }
        }
    }

    public bool RequireDownloadBeforeAction
    {
        get => _settings.RequireDownloadBeforeAction;
        set => SetSetting(_settings.RequireDownloadBeforeAction == value, () => _settings.RequireDownloadBeforeAction = value);
    }

    public bool IgnorePausedDownloads
    {
        get => _settings.IgnorePausedDownloads;
        set => SetSetting(_settings.IgnorePausedDownloads == value, () => _settings.IgnorePausedDownloads = value);
    }

    public bool ForceCloseApps
    {
        get => _settings.ForceCloseApps;
        set => SetSetting(_settings.ForceCloseApps == value, () => _settings.ForceCloseApps = value);
    }

    public bool EnableLogging
    {
        get => _settings.EnableLogging;
        set
        {
            if (SetSetting(_settings.EnableLogging == value, () => _settings.EnableLogging = value) && _log is not null)
            {
                _log.Enabled = value;
            }
        }
    }

    public bool WatchSteam
    {
        get => _settings.WatchSteam;
        set
        {
            if (SetSetting(_settings.WatchSteam == value, () => _settings.WatchSteam = value))
            {
                _host.RefreshNow();
            }
        }
    }

    public bool WatchXbox
    {
        get => _settings.WatchXbox;
        set
        {
            if (SetSetting(_settings.WatchXbox == value, () => _settings.WatchXbox = value))
            {
                _host.RefreshNow();
            }
        }
    }

    public bool AutoDetectLibraries
    {
        get => _settings.AutoDetectLibraries;
        set
        {
            if (SetSetting(_settings.AutoDetectLibraries == value, () => _settings.AutoDetectLibraries = value))
            {
                _librarySource.Invalidate();
                _host.RefreshNow();
            }
        }
    }

    // ---------------------------------------------------------------- Telegram

    public bool TelegramEnabled
    {
        get => _settings.Telegram.Enabled;
        set
        {
            if (SetSetting(_settings.Telegram.Enabled == value, () => _settings.Telegram.Enabled = value))
            {
                // Progress messages keep the scanner running on their own, so the schedule changes.
                _host.RefreshSchedule();
            }
        }
    }

    public string TelegramBotToken
    {
        get => _settings.Telegram.BotToken;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (SetSetting(_settings.Telegram.BotToken == trimmed, () => _settings.Telegram.BotToken = trimmed))
            {
                TelegramStatus = string.Empty;
                OnPropertyChanged(nameof(MaskedBotToken));
                DescribeKnownChats();
                _host.RefreshSchedule();
            }
        }
    }

    /// <summary>
    /// The token is a full credential for the bot, so it is hidden by default — it should not be
    /// readable over a shoulder or captured in a screenshot of the settings page.
    /// </summary>
    public bool ShowBotToken
    {
        get => _showBotToken;
        set
        {
            if (SetProperty(ref _showBotToken, value))
            {
                OnPropertyChanged(nameof(MaskedBotToken));
            }
        }
    }

    /// <summary>The bot id stays legible; everything after the colon is the secret half.</summary>
    public string MaskedBotToken
    {
        get
        {
            var token = _settings.Telegram.BotToken;
            if (string.IsNullOrEmpty(token))
            {
                return string.Empty;
            }

            var colon = token.IndexOf(':', StringComparison.Ordinal);
            return colon <= 0 ? new string('•', token.Length) : token[..(colon + 1)] + new string('•', 12);
        }
    }

    public ObservableCollection<ChatEntryViewModel> TelegramChatIds { get; }

    public string ChatIdInput
    {
        get => _chatIdInput;
        set
        {
            if (SetProperty(ref _chatIdInput, value))
            {
                _addChatIdCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ChatEntryViewModel? SelectedChatId
    {
        get => _selectedChatId;
        set
        {
            if (SetProperty(ref _selectedChatId, value))
            {
                _removeChatIdCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool TelegramNotifyOnStart
    {
        get => _settings.Telegram.NotifyOnStart;
        set
        {
            if (SetSetting(_settings.Telegram.NotifyOnStart == value, () => _settings.Telegram.NotifyOnStart = value))
            {
                _host.RefreshSchedule();
            }
        }
    }

    public bool TelegramRemoteButtons
    {
        get => _settings.Telegram.RemoteButtons;
        set => SetSetting(_settings.Telegram.RemoteButtons == value, () => _settings.Telegram.RemoteButtons = value);
    }

    public bool TelegramRemoteCommands
    {
        get => _settings.Telegram.RemoteCommands;
        set
        {
            if (SetSetting(_settings.Telegram.RemoteCommands == value, () => _settings.Telegram.RemoteCommands = value))
            {
                // Starts or stops the listener, and keeps the scanner alive so /status can answer.
                _host.RefreshSchedule();
            }
        }
    }

    /// <summary>
    /// What this PC calls itself in the Telegram messages. Empty falls back to the Windows computer
    /// name rather than to nothing.
    /// </summary>
    public string DeviceName
    {
        get => _settings.DeviceName;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length > AppSettings.MaxDeviceNameLength)
            {
                trimmed = trimmed[..AppSettings.MaxDeviceNameLength].TrimEnd();
            }

            if (trimmed.Length == 0)
            {
                trimmed = Environment.MachineName;
            }

            SetSetting(_settings.DeviceName == trimmed, () => _settings.DeviceName = trimmed);
        }
    }

    /// <summary>
    /// True once the marker file is in place. It does not promise the channel is open — Steam only
    /// reads the marker when it starts — which is what the status line beside the button is for.
    /// </summary>
    public bool DownloadControlEnabled => _downloads?.BridgeMarkerPresent ?? false;

    public RelayCommand RestartSteamCommand => _restartSteamCommand;

    public string DownloadControlStatus
    {
        get => _downloadControlStatus;
        private set => SetProperty(ref _downloadControlStatus, value);
    }

    public RelayCommand EnableDownloadControlCommand => _enableDownloadControlCommand;

    public bool TelegramNotifyOnProgress
    {
        get => _settings.Telegram.NotifyOnProgress;
        set
        {
            if (SetSetting(_settings.Telegram.NotifyOnProgress == value, () => _settings.Telegram.NotifyOnProgress = value))
            {
                _host.RefreshSchedule();
            }
        }
    }

    public int TelegramProgressStep
    {
        get => _settings.Telegram.ProgressStepPercent;
        set
        {
            _settings.Telegram.ProgressStepPercent =
                Math.Clamp(value, TelegramOptions.MinProgressStep, TelegramOptions.MaxProgressStep);
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool TelegramNotifyOnFinish
    {
        get => _settings.Telegram.NotifyOnFinish;
        set => SetSetting(_settings.Telegram.NotifyOnFinish == value, () => _settings.Telegram.NotifyOnFinish = value);
    }

    public bool TelegramNotifyOnCancel
    {
        get => _settings.Telegram.NotifyOnCancel;
        set => SetSetting(_settings.Telegram.NotifyOnCancel == value, () => _settings.Telegram.NotifyOnCancel = value);
    }

    public MessageLanguage TelegramLanguage
    {
        get => _settings.Telegram.Language;
        set => SetSetting(_settings.Telegram.Language == value, () => _settings.Telegram.Language = value);
    }

    public string TelegramStatus
    {
        get => _telegramStatus;
        private set => SetProperty(ref _telegramStatus, value);
    }

    // ---------------------------------------------------------------- Chat pairing

    /// <summary>Progress line for the automatic chat lookup.</summary>
    public string PairingStatus
    {
        get => _pairingStatus;
        private set => SetProperty(ref _pairingStatus, value);
    }

    /// <summary>The six digits also delivered to Telegram, for the user to compare.</summary>
    public string PairingCode
    {
        get => _pairingCode;
        private set => SetProperty(ref _pairingCode, value);
    }

    public bool IsListeningForChat => _pairingStage == PairingStage.Listening;

    /// <summary>True once a chat has been found and the code is waiting to be confirmed.</summary>
    public bool IsAwaitingChatConfirmation => _pairingStage == PairingStage.AwaitingConfirmation;

    public string FindChatButtonText => Loc.Get(IsListeningForChat ? "Telegram.Listening" : "Telegram.FindButton");

    public bool TelegramBusy
    {
        get => _telegramBusy;
        private set
        {
            if (SetProperty(ref _telegramBusy, value))
            {
                _testTelegramCommand.RaiseCanExecuteChanged();
                _enableDownloadControlCommand.RaiseCanExecuteChanged();
                _restartSteamCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ---------------------------------------------------------------- Libraries

    public ObservableCollection<string> DetectedLibraries { get; } = [];

    public ObservableCollection<string> ManualLibraries { get; }

    public string? SelectedManualLibrary
    {
        get => _selectedManualLibrary;
        set
        {
            if (SetProperty(ref _selectedManualLibrary, value))
            {
                _removeLibraryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LibraryMessage
    {
        get => _libraryMessage;
        private set => SetProperty(ref _libraryMessage, value);
    }

    // ---------------------------------------------------------------- Behaviour

    public void ToggleMonitoring()
    {
        if (_host.Engine.IsEnabled)
        {
            _host.Disable();
        }
        else
        {
            _host.Enable();
        }

        RefreshPhase();
    }

    public void CancelCountdown()
    {
        _host.CancelCountdown();
        RefreshPhase();
    }

    /// <summary>Writes any pending settings changes immediately.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        _settings.ManualLibraries = [.. ManualLibraries];
        SyncChatSettings();
        _store.Save(_settings);
    }

    private void OnSnapshotUpdated(DownloadSnapshot snapshot)
    {
        var status = SteamStatusFormatter.Describe(snapshot);
        StatusHeadline = status.Headline;
        StatusDetail = status.Detail;

        UpdateTransfer(snapshot);
        SyncQueue(snapshot);
        SyncDetectedLibraries(snapshot.LibraryRoots);
        RefreshPhase();
    }

    private void UpdateTransfer(DownloadSnapshot snapshot)
    {
        var app = snapshot.Headline;
        var meter = _host.Meter;

        HasActiveDownload = app is not null;

        if (app is null)
        {
            DownloadingName = "—";
            DownloadingState = string.Empty;
            IsDownloadPaused = false;
            HasPlatform = false;
            PlatformText = string.Empty;
            DownloadPercent = 0;
            InstallPercent = 0;
            DownloadBytesText = string.Empty;
            InstallBytesText = string.Empty;
            NetworkText = "—";
            PeakText = "—";
            DiskText = "—";
            EtaText = "—";
            FinishesAtText = "—";
            TotalLeftText = "—";
            QueueSummary = string.Empty;
            return;
        }

        var isLive = snapshot.IsLive(app);
        IsDownloadPaused = snapshot.IsPausedOrStalled(app);
        PlatformText = Loc.Get($"Platform.{app.Platform}");
        PlatformBrush = PlatformBrushes.Strong(app.Platform);
        PlatformSoftBrush = PlatformBrushes.Soft(app.Platform);
        HasPlatform = true;

        DownloadingName = app.Name;
        DownloadingState = Loc.Get((app, isLive, IsDownloadPaused) switch
        {
            (_, _, true) => "Queue.StatePaused",
            ({ IsValidating: true }, true, _) => "Queue.StateValidating",
            ({ IsInstalling: true }, true, _) => "Queue.StateInstalling",
            (_, true, _) => "Queue.StateDownloading",
            _ => "Queue.StateWaiting",
        });

        // The two bars are deliberately independent: bytes off the network versus bytes on disk.
        DownloadPercent = (app.DownloadProgress ?? 0) * 100;
        InstallPercent = (app.InstallProgress ?? 0) * 100;
        DownloadBytesText = $"{Humanize.Bytes(app.BytesDownloaded)} / {Humanize.Bytes(app.BytesToDownload)}";
        InstallBytesText = $"{Humanize.Bytes(app.BytesStaged)} / {Humanize.Bytes(app.BytesToStage)}";

        // Once a rate has been measured, a standstill reads "0 bps" as it does in Steam, rather than
        // the em dash that means "not measured yet".
        var idle = meter.HasReading ? "0 bps" : "—";
        NetworkText = Humanize.Rate(meter.NetworkBytesPerSecond, idle);
        PeakText = Humanize.Rate(meter.PeakNetworkBytesPerSecond);
        DiskText = Humanize.Rate(meter.DiskBytesPerSecond, idle);

        if (meter.Eta is { } eta && !IsDownloadPaused)
        {
            EtaText = Humanize.Clock(eta);
            FinishesAtText = Humanize.FinishTime(DateTimeOffset.Now + eta);
        }
        else
        {
            EtaText = "—";
            FinishesAtText = "—";
        }

        TotalLeftText = Humanize.Bytes(snapshot.TotalDownloadBytesRemaining);

        var waiting = snapshot.Waiting;
        var waitingBytes = Loc.Ltr(Humanize.Bytes(waiting.Sum(a => a.DownloadBytesRemaining)));
        QueueSummary = waiting.Count switch
        {
            0 => string.Empty,
            1 => Loc.F("Queue.OneItemLeft", waitingBytes),
            _ => Loc.F("Queue.ItemsLeft", waiting.Count, waitingBytes),
        };
    }

    /// <summary>
    /// Updates the queue in place. Rebuilding it every second would make the list flicker and drop
    /// the scroll position, so rows are only replaced when the order actually changes.
    /// </summary>
    private void SyncQueue(DownloadSnapshot snapshot)
    {
        // The live download has its own card at the top, so only what is waiting is listed here.
        var pipeline = snapshot.Waiting;
        var sameOrder = Queue.Count == pipeline.Count;
        if (sameOrder)
        {
            for (var i = 0; i < pipeline.Count; i++)
            {
                if (Queue[i].AppId != pipeline[i].AppId)
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (sameOrder)
        {
            for (var i = 0; i < pipeline.Count; i++)
            {
                Queue[i].Update(pipeline[i], isLive: false);
            }

            return;
        }

        var existing = Queue.ToDictionary(item => item.AppId);
        Queue.Clear();
        foreach (var app in pipeline)
        {
            if (existing.TryGetValue(app.AppId, out var item))
            {
                item.Update(app, isLive: false);
                Queue.Add(item);
            }
            else
            {
                Queue.Add(new QueueItemViewModel(app, isLive: false));
            }
        }

        OnPropertyChanged(nameof(HasQueue));
    }

    private void OnActionFailed(string message)
    {
        StatusHeadline = "The action could not be started";
        StatusDetail = message;
        Notification?.Invoke("SteamFinish", message, NotificationKind.Error);
        RefreshPhase();
    }

    private void OnTelegramSendFailed(string message) =>
        _dispatcher.BeginInvoke(() => TelegramStatus = $"Telegram: {message}");

    private void OnThemeChanged()
    {
        if (_host.LastSnapshot is { } snapshot)
        {
            // Re-projects the snapshot so every brush resolved in code comes from the new palette.
            OnSnapshotUpdated(snapshot);
        }
        else
        {
            RefreshPhase();
        }
    }

    private void RefreshPhase()
    {
        var engine = _host.Engine;
        var now = DateTimeOffset.Now;

        IsMonitoring = engine.IsEnabled;
        IsCountingDown = engine.Phase == MonitorPhase.Countdown;
        CountdownDisplay = IsCountingDown
            ? (int)Math.Ceiling(engine.CountdownRemaining(now).TotalSeconds)
            : _settings.CountdownSeconds;
        ToggleButtonText = Loc.Get(IsMonitoring ? "Button.Disable" : "Button.Enable");
        PhaseText = DescribePhase(engine, now);
        PhaseBrush = BrushFor(engine.Phase);

        OnPropertyChanged(nameof(CountdownCaption), nameof(CancelButtonText), nameof(TrayTooltip));
        StateChanged?.Invoke();
    }

    private string DescribePhase(MonitorEngine engine, DateTimeOffset now) => engine.Phase switch
    {
        MonitorPhase.Disabled => Loc.Get("Phase.Off"),
        MonitorPhase.WaitingForDownload => Loc.Get("Phase.Waiting"),
        MonitorPhase.Busy => Loc.Get("Phase.Busy"),
        MonitorPhase.Confirming =>
            Loc.F("Phase.Confirming", (int)Math.Ceiling(engine.ConfirmationRemaining(now).TotalSeconds)),
        MonitorPhase.Countdown => Loc.F("Phase.Countdown", ActionName, CountdownDisplay),
        MonitorPhase.Executing => Loc.F("Phase.Executing", ActionName),
        MonitorPhase.Blocked => Loc.Get("Phase.Blocked"),
        _ => string.Empty,
    };

    /// <summary>
    /// Re-runs everything that produced a string in the old language. XAML labels update themselves
    /// through the Loc indexer; these are the ones built in code.
    /// </summary>
    private void RefreshLocalizedText()
    {
        OnPropertyChanged(
            nameof(ActionName),
            nameof(CountdownCaption),
            nameof(CancelButtonText),
            nameof(FindChatButtonText),
            nameof(TrayTooltip));

        foreach (var entry in TelegramChatIds)
        {
            entry.RefreshLabel();
        }

        if (_host.LastSnapshot is { } snapshot)
        {
            OnSnapshotUpdated(snapshot);
        }
        else
        {
            RefreshPhase();
        }
    }

    private static Brush BrushFor(MonitorPhase phase)
    {
        var key = phase switch
        {
            MonitorPhase.Disabled => "DisabledBrush",
            MonitorPhase.Busy or MonitorPhase.WaitingForDownload => "AccentBrush",
            MonitorPhase.Confirming or MonitorPhase.Blocked => "WarningBrush",
            MonitorPhase.Countdown or MonitorPhase.Executing => "DangerBrush",
            _ => "TextSecondaryBrush",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private static string DescribeAction(PowerAction action) => Loc.Get($"Action.{action}");

    private void SyncDetectedLibraries(IReadOnlyList<string> roots)
    {
        if (DetectedLibraries.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        DetectedLibraries.Clear();
        foreach (var root in roots)
        {
            DetectedLibraries.Add(root);
        }
    }

    private void AddLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a Steam library folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var folder = dialog.FolderName.TrimEnd('\\');

        // Accept the library root or the steamapps folder inside it; both are easy to pick by mistake.
        if (!Directory.Exists(Path.Combine(folder, "steamapps")))
        {
            if (string.Equals(Path.GetFileName(folder), "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.GetDirectoryName(folder) ?? folder;
            }
            else
            {
                LibraryMessage = $"'{folder}' does not contain a steamapps folder.";
                return;
            }
        }

        if (ManualLibraries.Any(existing => string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase)))
        {
            LibraryMessage = "That library is already in the list.";
            return;
        }

        ManualLibraries.Add(folder);
        LibraryMessage = string.Empty;
        ApplyLibraryChange();
    }

    private void RemoveLibrary()
    {
        if (_selectedManualLibrary is null)
        {
            return;
        }

        ManualLibraries.Remove(_selectedManualLibrary);
        SelectedManualLibrary = null;
        LibraryMessage = string.Empty;
        ApplyLibraryChange();
    }

    private void ApplyLibraryChange()
    {
        _settings.ManualLibraries = [.. ManualLibraries];
        ScheduleSave();
        _librarySource.Invalidate();
        _host.RefreshNow();
    }

    private void AddChatId()
    {
        if (!AddChatIdCore(_chatIdInput))
        {
            TelegramStatus = "That chat ID is already in the list.";
            return;
        }

        ChatIdInput = string.Empty;
        TelegramStatus = string.Empty;
    }

    /// <summary>Shared by the manual box and the automatic lookup; false when it was already there.</summary>
    private bool AddChatIdCore(string chatId, string? label = null)
    {
        var id = chatId.Trim();
        if (id.Length == 0
            || TelegramChatIds.Any(existing => string.Equals(existing.ChatId, id, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var entry = new ChatEntryViewModel(id, label);
        TelegramChatIds.Add(entry);
        SyncChatSettings();
        ScheduleSave();
        _host.RefreshSchedule();

        if (label is null)
        {
            _ = DescribeChatAsync(entry);
        }

        return true;
    }

    private void RemoveChatId()
    {
        if (_selectedChatId is null)
        {
            return;
        }

        TelegramChatIds.Remove(_selectedChatId);
        SelectedChatId = null;
        SyncChatSettings();
        ScheduleSave();
        _host.RefreshSchedule();
    }

    private void SyncChatSettings()
    {
        _settings.Telegram.ChatIds = [.. TelegramChatIds.Select(entry => entry.ChatId)];
        _settings.Telegram.ChatLabels = TelegramChatIds
            .Where(entry => entry.Label is not null)
            .ToDictionary(entry => entry.ChatId, entry => entry.Label!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Asks Telegram what a stored id actually is, so the list can name it.</summary>
    private async Task DescribeChatAsync(ChatEntryViewModel entry)
    {
        var token = _settings.Telegram.BotToken;
        if (!TelegramOptions.LooksLikeToken(token))
        {
            return;
        }

        var chat = await _chatFinder.DescribeChatAsync(token, entry.ChatId).ConfigureAwait(true);
        if (chat is null)
        {
            return;
        }

        entry.Describe(chat);
        SyncChatSettings();
        ScheduleSave();
    }

    /// <summary>Fills in any names still missing, once at startup and after the token changes.</summary>
    private async void DescribeKnownChats()
    {
        foreach (var entry in TelegramChatIds.Where(e => e.Label is null).ToList())
        {
            await DescribeChatAsync(entry).ConfigureAwait(true);
        }
    }

    private async void TestTelegram()
    {
        // Persist first so the test uses exactly what is on screen.
        SaveNow();
        TelegramBusy = true;
        TelegramStatus = "Sending a test message…";

        try
        {
            var result = await _host.Telegram.TestAsync().ConfigureAwait(true);
            TelegramStatus = result.Success ? $"✓ {result.Message}" : $"✕ {result.Message}";
        }
        catch (Exception e)
        {
            TelegramStatus = $"✕ {e.Message}";
        }
        finally
        {
            TelegramBusy = false;
        }
    }

    /// <summary>
    /// Writes the marker file Steam needs, then reports whether the channel is actually open —
    /// which it is not until Steam has been restarted at least once afterwards.
    /// </summary>
    private async void EnableDownloadControl()
    {
        if (_downloads is null)
        {
            return;
        }

        TelegramBusy = true;
        DownloadControlStatus = Loc.Get("Telegram.ControlChecking");

        try
        {
            var setup = _downloads.EnableBridge();
            OnPropertyChanged(nameof(DownloadControlEnabled));

            switch (setup.Outcome)
            {
                case BridgeSetupOutcome.SteamNotFound:
                    DownloadControlStatus = $"✕ {Loc.Get("Telegram.ControlSteamNotFound")}";
                    return;

                case BridgeSetupOutcome.Failed:
                    DownloadControlStatus = $"✕ {Loc.F("Telegram.ControlFailed", setup.Detail ?? string.Empty)}";
                    return;
            }

            await ReportChannelAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            DownloadControlStatus = $"✕ {e.Message}";
        }
        finally
        {
            TelegramBusy = false;
        }
    }

    /// <summary>
    /// Closes Steam and starts it again with its control channel on a port nothing else is using.
    /// The way out when the default port was taken at the moment Steam started — Steam chooses the
    /// port once and never revisits it.
    /// </summary>
    private async void RestartSteam()
    {
        if (_downloads is null)
        {
            return;
        }

        TelegramBusy = true;
        DownloadControlStatus = Loc.Get("Telegram.ControlRestarting");

        try
        {
            var restart = await _downloads.RestartSteamAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(DownloadControlEnabled));

            switch (restart.Outcome)
            {
                case RelaunchOutcome.SteamNotFound:
                    DownloadControlStatus = $"✕ {Loc.Get("Telegram.ControlSteamNotFound")}";
                    return;

                case RelaunchOutcome.WouldNotClose:
                    DownloadControlStatus = $"✕ {Loc.Get("Telegram.ControlWouldNotClose")}";
                    return;

                case RelaunchOutcome.WouldNotStart:
                    DownloadControlStatus =
                        $"✕ {Loc.F("Telegram.ControlFailed", restart.Detail ?? string.Empty)}";
                    return;
            }

            // Steam takes a few seconds to bring its windows up, and the channel is not there until
            // the shared context exists.
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(true);
            await ReportChannelAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            DownloadControlStatus = $"✕ {e.Message}";
        }
        finally
        {
            TelegramBusy = false;
        }
    }

    /// <summary>Probes the channel and puts the outcome, in the user's words, under the buttons.</summary>
    private async Task ReportChannelAsync()
    {
        if (_downloads is null)
        {
            return;
        }

        var probe = await _downloads.ProbeAsync().ConfigureAwait(true);
        DownloadControlStatus = probe.Outcome switch
        {
            ControlOutcome.Done => $"✓ {Loc.F("Telegram.ControlReady", _downloads.ActivePort)}",
            ControlOutcome.RestartSteam => Loc.Get("Telegram.ControlRestartSteam"),
            ControlOutcome.SteamNotRunning => Loc.Get("Telegram.ControlSteamNotRunning"),
            ControlOutcome.PortBusy => $"✕ {Loc.Get("Telegram.ControlPortBusy")}",
            _ => $"✕ {Loc.F("Telegram.ControlFailed", probe.Detail ?? probe.Outcome.ToString())}",
        };
    }

    /// <summary>
    /// Listens for the user's message, then holds the six-digit code on screen so it can be checked
    /// against the one Telegram received before the chat is trusted.
    /// </summary>
    private async void FindChat()
    {
        CancelPairing();
        SaveNow();

        // The command loop is listening on the same bot; two pollers on one token take each other's
        // updates, so it stands aside until the pairing is finished or cancelled.
        _commandsHeldForPairing ??= _host.SuspendRemoteCommands();

        _pairing = new CancellationTokenSource();
        SetPairingStage(PairingStage.Listening);
        PairingCode = string.Empty;
        _pairedChat = null;

        var progress = new Progress<string>(message => PairingStatus = message);

        try
        {
            var result = await _chatFinder
                .FindChatAsync(_settings.Telegram.BotToken, progress, _pairing.Token)
                .ConfigureAwait(true);

            if (result is { Success: true, Chat: not null, Code: not null })
            {
                _pairedChat = result.Chat;
                _pairedMessageId = result.CodeMessageId;
                PairingCode = result.Code;
                PairingStatus = $"Found {result.Chat.Describe()}. Check the code in Telegram.";
                SetPairingStage(PairingStage.AwaitingConfirmation);
            }
            else
            {
                PairingStatus = $"✕ {result.Message}";
                SetPairingStage(PairingStage.Idle);
            }
        }
        catch (Exception e)
        {
            PairingStatus = $"✕ {e.Message}";
            SetPairingStage(PairingStage.Idle);
        }
    }

    private void ConfirmChat()
    {
        if (_pairedChat is null)
        {
            return;
        }

        // The pairing already learned the name and kind, so no lookup is needed.
        var chat = _pairedChat;
        var added = AddChatIdCore(chat.ChatId, chat.Describe());

        // Leave the Telegram chat showing that it worked, rather than a code that means nothing now.
        _ = _chatFinder.ConfirmPairingAsync(_settings.Telegram.BotToken, chat.ChatId, _pairedMessageId);
        PairingStatus = added
            ? $"✓ Added {_pairedChat.Describe()}."
            : $"{_pairedChat.Describe()} was already in the list.";

        PairingCode = string.Empty;
        _pairedChat = null;
        SetPairingStage(PairingStage.Idle);
    }

    private void CancelPairing()
    {
        _pairing?.Cancel();
        _pairing?.Dispose();
        _pairing = null;

        if (_pairingStage != PairingStage.Idle)
        {
            PairingCode = string.Empty;
            _pairedChat = null;
            SetPairingStage(PairingStage.Idle);
            PairingStatus = string.Empty;
        }
    }

    private void SetPairingStage(PairingStage stage)
    {
        _pairingStage = stage;

        // Only the listening stage needs the Telegram connection to itself; confirming the code just
        // edits a message, so the command loop can have it back.
        if (stage != PairingStage.Listening)
        {
            var held = _commandsHeldForPairing;
            _commandsHeldForPairing = null;
            held?.Dispose();
        }

        OnPropertyChanged(
            nameof(IsListeningForChat),
            nameof(IsAwaitingChatConfirmation),
            nameof(FindChatButtonText));

        _findChatCommand.RaiseCanExecuteChanged();
        _confirmChatCommand.RaiseCanExecuteChanged();
        _cancelPairingCommand.RaiseCanExecuteChanged();
    }

    private void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            PairingStatus = $"✕ Could not open {url}: {e.Message}";
        }
    }

    /// <summary>
    /// Asks GitHub whether there is a newer release. Quiet unless something is found, except when
    /// the user pressed the button themselves and deserves an answer either way.
    /// </summary>
    public async Task CheckUpdatesAsync(bool announceUpToDate)
    {
        if (_updates is null || _updateBusy)
        {
            return;
        }

        UpdateBusy = true;
        UpdateStatus = Loc.Get("Update.Checking");

        try
        {
            var result = await _updates.CheckAsync().ConfigureAwait(true);

            if (result.Available is { } update)
            {
                _pendingUpdate = update;
                UpdateAvailable = true;
                UpdateStatus = Loc.F("Update.Available", update.Version);
                Notification?.Invoke(
                    "SteamFinish",
                    Loc.F("Update.Available", update.Version),
                    NotificationKind.Info);
            }
            else
            {
                _pendingUpdate = null;
                UpdateAvailable = false;
                UpdateStatus = result.Checked
                    ? announceUpToDate ? Loc.Get("Update.UpToDate") : string.Empty
                    : announceUpToDate ? Loc.F("Update.Failed", result.Message) : string.Empty;
            }
        }
        catch (Exception e)
        {
            UpdateStatus = Loc.F("Update.Failed", e.Message);
        }
        finally
        {
            UpdateBusy = false;
            _installUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Downloads the release and hands over to the swap script, which restarts the app. The app
    /// closes itself so the running executable can be replaced.
    /// </summary>
    private async void InstallUpdate()
    {
        if (_updates is null || _pendingUpdate is not { } update)
        {
            return;
        }

        UpdateBusy = true;
        var progress = new Progress<double>(fraction =>
            UpdateStatus = Loc.F("Update.Downloading", (int)(fraction * 100)));

        try
        {
            var failure = await _updates.InstallAsync(update, progress).ConfigureAwait(true);
            if (failure is not null)
            {
                UpdateStatus = Loc.F("Update.Failed", failure);
                UpdateBusy = false;
                return;
            }

            UpdateStatus = Loc.Get("Update.Restarting");
            SaveNow();

            // The swap script is already waiting for this process to go away.
            Application.Current?.Shutdown();
        }
        catch (Exception e)
        {
            UpdateStatus = Loc.F("Update.Failed", e.Message);
            UpdateBusy = false;
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureDataFolder();
            Process.Start(new ProcessStartInfo(AppPaths.DataFolder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            LibraryMessage = $"Could not open '{AppPaths.DataFolder}': {e.Message}";
        }
    }

    /// <summary>Applies a simple settings change and returns whether anything actually changed.</summary>
    private bool SetSetting(bool unchanged, Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (unchanged)
        {
            return false;
        }

        apply();
        OnPropertyChanged(propertyName);
        ScheduleSave();
        return true;
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Stages of the automatic chat lookup.</summary>
    private enum PairingStage
    {
        Idle,
        Listening,
        AwaitingConfirmation,
    }

    public void Dispose()
    {
        CancelPairing();
        _commandsHeldForPairing?.Dispose();
        _commandsHeldForPairing = null;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _host.SnapshotUpdated -= OnSnapshotUpdated;
        _host.Tick -= RefreshPhase;
        _host.ActionFailed -= OnActionFailed;
        _host.Telegram.SendFailed -= OnTelegramSendFailed;
        SaveNow();
    }
}

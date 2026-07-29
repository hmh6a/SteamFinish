using System.Windows;
using System.Windows.Media;
using SteamFinish.Core.Formatting;
using SteamFinish.Core.Localization;
using SteamFinish.Core.Steam;

namespace SteamFinish.ViewModels;

/// <summary>One row of the download queue, mirroring Steam's "Up Next" list.</summary>
public sealed class QueueItemViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _stateText = string.Empty;
    private string _sizeText = string.Empty;
    private double _percent;
    private bool _isWorking;
    private Brush _accent = Brushes.Gray;

    public QueueItemViewModel(AppActivity app, bool isLive) => Update(app, isLive);

    public uint AppId { get; private set; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    /// <summary>Downloading / Installing / Validating / Next / Queued / Paused.</summary>
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string SizeText
    {
        get => _sizeText;
        private set => SetProperty(ref _sizeText, value);
    }

    /// <summary>Steam's own per-game figure, which is the staged share rather than the downloaded one.</summary>
    public double Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    public bool IsWorking
    {
        get => _isWorking;
        private set => SetProperty(ref _isWorking, value);
    }

    public Brush Accent
    {
        get => _accent;
        private set => SetProperty(ref _accent, value);
    }

    public void Update(AppActivity app, bool isLive)
    {
        AppId = app.AppId;
        Name = app.Name;
        IsWorking = isLive && !app.IsPaused;
        Percent = (app.Progress ?? 0) * 100;

        StateText = Loc.Get((app, isLive) switch
        {
            ({ IsPaused: true }, _) => "Queue.StatePaused",
            ({ IsValidating: true }, true) => "Queue.StateValidating",
            ({ IsInstalling: true }, true) => "Queue.StateInstalling",
            (_, true) => "Queue.StateDownloading",
            _ => "Queue.StateQueued",
        });

        SizeText = app.BytesToDownload > 0
            ? $"{Humanize.Bytes(app.BytesDownloaded)} / {Humanize.Bytes(app.BytesToDownload)}"
            : Humanize.Bytes(app.BytesToStage);

        Accent = Resource(app switch
        {
            { IsPaused: true } => "WarningBrush",
            _ when isLive => "AccentBrush",
            _ => "DisabledBrush",
        });
    }

    private static Brush Resource(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

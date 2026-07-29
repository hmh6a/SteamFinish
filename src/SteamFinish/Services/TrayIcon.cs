using System.Windows;
using SteamFinish.Core.Localization;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SteamFinish.Services;

public enum NotificationKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The system tray presence: icon, tooltip, context menu and balloon notifications.
/// Backed by Windows Forms' NotifyIcon, the only supported tray API for desktop apps.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Shell limit for the tooltip string; longer text is rejected outright.</summary>
    private const int MaxTooltipLength = 63;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Forms.ToolStripMenuItem _cancelItem;

    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _exitItem;

    private bool _monitoring;
    private string _cancelText = string.Empty;

    public TrayIcon()
    {
        _toggleItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => ToggleRequested?.Invoke());
        _cancelItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => CancelRequested?.Invoke())
        {
            Enabled = false,
        };

        var menu = new Forms.ContextMenuStrip();
        _openItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => ShowRequested?.Invoke())
        {
            // The default action of the menu, so it gets the bold treatment Explorer uses.
            Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold),
        };
        _exitItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => ExitRequested?.Invoke());

        menu.Items.Add(_openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_cancelItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_exitItem);

        // The menu is built once and relabelled whenever the language changes.
        _openItem.Text = Loc.Get("Tray.Open");
        _exitItem.Text = Loc.Get("Tray.Exit");
        _toggleItem.Text = Loc.Get("Button.Enable");
        _cancelItem.Text = Loc.F("Button.CancelAction", Loc.Get("Action.Shutdown"));
        Loc.LanguageChanged += OnLanguageChanged;

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "SteamFinish",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public event Action? ShowRequested;

    public event Action? ExitRequested;

    public event Action? ToggleRequested;

    public event Action? CancelRequested;

    public void SetTooltip(string text)
    {
        var trimmed = text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
        _icon.Text = trimmed;
    }

    public void UpdateMenu(bool isMonitoring, bool canCancel, string cancelText)
    {
        _monitoring = isMonitoring;
        _cancelText = cancelText;
        _toggleItem.Text = Loc.Get(isMonitoring ? "Button.Disable" : "Button.Enable");
        _cancelItem.Text = cancelText;
        _cancelItem.Enabled = canCancel;
    }

    private void OnLanguageChanged()
    {
        _openItem.Text = Loc.Get("Tray.Open");
        _exitItem.Text = Loc.Get("Tray.Exit");
        _toggleItem.Text = Loc.Get(_monitoring ? "Button.Disable" : "Button.Enable");
        _cancelItem.Text = _cancelText;
    }

    public void ShowBalloon(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        _icon.ShowBalloonTip(
            5000,
            title,
            message,
            kind switch
            {
                NotificationKind.Warning => Forms.ToolTipIcon.Warning,
                NotificationKind.Error => Forms.ToolTipIcon.Error,
                _ => Forms.ToolTipIcon.Info,
            });
    }

    private static Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (resource is null)
        {
            return Drawing.SystemIcons.Application;
        }

        using var stream = resource.Stream;
        // Ask for the shell's small icon size so the crisp 16px entry is used rather than a downscale.
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}

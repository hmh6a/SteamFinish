using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using Microsoft.Win32;
using SteamFinish.Core.Settings;

namespace SteamFinish.Services;

/// <summary>
/// Swaps the palette dictionary at runtime. The control styles reference their colours with
/// DynamicResource, so replacing the palette repaints the whole window without rebuilding it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ThemeManager
{
    /// <summary>Position of the palette inside <c>App.xaml</c>'s merged dictionaries.</summary>
    private const int PaletteIndex = 0;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Raised after the palette has been swapped, for brushes resolved in code.</summary>
    public static event Action? ThemeChanged;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>Which palette is actually loaded, after <see cref="AppTheme.System"/> is resolved.</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        var resources = Application.Current?.Resources;
        if (resources is null || resources.MergedDictionaries.Count <= PaletteIndex)
        {
            return;
        }

        // Reload even when the resolved mode is unchanged: the first call has to take effect too.
        IsDark = dark;
        var source = new Uri(
            dark ? "Themes/Palette.Dark.xaml" : "Themes/Palette.Light.xaml",
            UriKind.Relative);

        resources.MergedDictionaries[PaletteIndex] = new ResourceDictionary { Source = source };
        ThemeChanged?.Invoke();
    }

    /// <summary>Re-resolves the palette; only meaningful while following the system setting.</summary>
    public static void RefreshIfFollowingSystem()
    {
        if (Current == AppTheme.System && IsDark != IsSystemDark())
        {
            Apply(AppTheme.System);
        }
    }

    /// <summary>Reads Windows' own app theme. Defaults to light when the value cannot be read.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

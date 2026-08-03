using System.Windows;
using System.Windows.Media;
using SteamFinish.Core.Steam;

namespace SteamFinish.ViewModels;

/// <summary>
/// The launcher badge colours — Steam blue, Xbox green. Looked up from the active palette on each
/// call rather than cached, so a theme switch picks up the new shades.
/// </summary>
public static class PlatformBrushes
{
    public static Brush Strong(GamePlatform platform) => Resource($"Platform{platform}Brush");

    public static Brush Soft(GamePlatform platform) => Resource($"Platform{platform}SoftBrush");

    private static Brush Resource(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

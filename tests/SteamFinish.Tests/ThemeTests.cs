using System.Xml.Linq;
using SteamFinish.Core.Settings;

namespace SteamFinish.Tests;

/// <summary>
/// The palettes are swapped wholesale at runtime. If one defines a key the other does not, the
/// missing brush silently falls back and the window paints wrong in that theme only — exactly the
/// kind of thing nobody notices until they switch.
/// </summary>
public class ThemeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void BothPalettesDefineExactlyTheSameKeys()
    {
        var light = KeysOf("Palette.Light.xaml");
        var dark = KeysOf("Palette.Dark.xaml");

        Assert.NotEmpty(light);
        Assert.Equal(light, dark);
    }

    [Fact]
    public void ControlStylesTakeTheirColoursDynamically()
    {
        // A StaticResource brush reference would freeze whichever palette was loaded at startup.
        var controls = File.ReadAllText(ThemeFile("Controls.xaml"));

        Assert.DoesNotContain("StaticResource", ControlBrushReferences(controls), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultThemeFollowsWindows()
    {
        Assert.Equal(AppTheme.System, new AppSettings().Theme);
    }

    [Fact]
    public void AnUnknownThemeInTheSettingsFileFallsBackToSystem()
    {
        var settings = new AppSettings { Theme = (AppTheme)99 }.Normalize();

        Assert.Equal(AppTheme.System, settings.Theme);
    }

    /// <summary>Every brush/colour reference in the control styles, joined for a single assertion.</summary>
    private static string ControlBrushReferences(string xaml) =>
        string.Join(
            "\n",
            System.Text.RegularExpressions.Regex
                .Matches(xaml, @"\{(Static|Dynamic)Resource\s+\w*(Brush|Shadow)\}")
                .Select(match => match.Value));

    private static SortedSet<string> KeysOf(string fileName)
    {
        var document = XDocument.Load(ThemeFile(fileName));

        return
        [
            .. document.Root!
                .Elements()
                .Select(element => element.Attribute(Xaml + "Key")?.Value)
                .Where(key => key is not null)
                .Select(key => key!),
        ];
    }

    private static string ThemeFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SteamFinish.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "SteamFinish", "Themes", fileName);
    }
}

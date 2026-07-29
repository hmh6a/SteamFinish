using System.Text.RegularExpressions;
using SteamFinish.Core.Localization;

namespace SteamFinish.Tests;

/// <summary>
/// Guards the string table itself. A missing or mismatched translation only shows up at runtime in
/// the language nobody happened to test, so it is checked mechanically instead.
/// </summary>
public class LocalizationTests : IDisposable
{
    public void Dispose() => Loc.Use(UiLanguage.English);

    [Fact]
    public void EveryKeyHasTextInBothLanguages()
    {
        foreach (var key in Loc.Keys)
        {
            Loc.Use(UiLanguage.English);
            var english = Loc.Get(key);

            Loc.Use(UiLanguage.Arabic);
            var arabic = Loc.Get(key);

            Assert.False(string.IsNullOrWhiteSpace(english), $"'{key}' has no English text.");
            Assert.False(string.IsNullOrWhiteSpace(arabic), $"'{key}' has no Arabic text.");
            Assert.NotEqual(key, english);
            Assert.NotEqual(key, arabic);
        }
    }

    [Fact]
    public void PlaceholdersMatchAcrossLanguages()
    {
        // A translation that drops or invents a {0} throws at runtime inside string.Format.
        foreach (var key in Loc.Keys)
        {
            Loc.Use(UiLanguage.English);
            var english = Placeholders(Loc.Get(key));

            Loc.Use(UiLanguage.Arabic);
            var arabic = Placeholders(Loc.Get(key));

            Assert.True(
                english.SetEquals(arabic),
                $"'{key}' uses [{string.Join(",", english)}] in English but [{string.Join(",", arabic)}] in Arabic.");
        }
    }

    [Fact]
    public void FormattingASentenceSubstitutesEveryArgument()
    {
        Loc.Use(UiLanguage.English);
        Assert.Equal("Shutdown in 42s", Loc.F("Phase.Countdown", "Shutdown", 42));

        Loc.Use(UiLanguage.Arabic);
        Assert.Equal("إطفاء خلال 42 ثانية", Loc.F("Phase.Countdown", Loc.Get("Action.Shutdown"), 42));
    }

    [Fact]
    public void AnUnknownKeyFallsBackToTheKeyRatherThanThrowing()
    {
        Assert.Equal("Nope.Missing", Loc.Get("Nope.Missing"));
    }

    [Fact]
    public void ArabicIsTheOnlyRightToLeftLanguage()
    {
        Loc.Use(UiLanguage.English);
        Assert.False(Loc.IsRightToLeft);

        Loc.Use(UiLanguage.Arabic);
        Assert.True(Loc.IsRightToLeft);
    }

    [Fact]
    public void SwitchingLanguageNotifiesBindingsAndListeners()
    {
        Loc.Use(UiLanguage.English);

        var indexerRefreshed = false;
        var listenerCalled = false;

        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Item[]")
            {
                indexerRefreshed = true;
            }
        }

        void OnLanguageChanged() => listenerCalled = true;

        Loc.Instance.PropertyChanged += OnPropertyChanged;
        Loc.LanguageChanged += OnLanguageChanged;

        try
        {
            Loc.Use(UiLanguage.Arabic);
        }
        finally
        {
            Loc.Instance.PropertyChanged -= OnPropertyChanged;
            Loc.LanguageChanged -= OnLanguageChanged;
        }

        Assert.True(indexerRefreshed, "Bindings are refreshed through the Item[] notification.");
        Assert.True(listenerCalled);
    }

    [Fact]
    public void TheSteamStatusTextFollowsTheSelectedLanguage()
    {
        var snapshot = TestData.Snapshot(TestData.App(
            SteamFinish.Core.Steam.AppStateFlags.UpdateStarted,
            downloaded: 50,
            toDownload: 100,
            appId: 1,
            name: "Khazan")) with
        { ActiveAppId = 1 };

        Loc.Use(UiLanguage.English);
        var english = SteamFinish.Core.Steam.SteamStatusFormatter.Describe(snapshot).Headline;
        Assert.StartsWith("Downloading Khazan", english, StringComparison.Ordinal);
        Assert.DoesNotContain('‎', english);

        Loc.Use(UiLanguage.Arabic);
        var arabic = SteamFinish.Core.Steam.SteamStatusFormatter.Describe(snapshot).Headline;

        // The game name is fenced with LEFT-TO-RIGHT MARKs; compare the text without them.
        Assert.Contains('‎', arabic);
        Assert.StartsWith("جارٍ تنزيل Khazan", arabic.Replace("‎", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void LeftToRightFencingOnlyAppliesInArabic()
    {
        Loc.Use(UiLanguage.English);
        Assert.Equal("22.5 GB", Loc.Ltr("22.5 GB"));

        Loc.Use(UiLanguage.Arabic);
        Assert.Equal("‎22.5 GB‎", Loc.Ltr("22.5 GB"));
        Assert.Equal(string.Empty, Loc.Ltr(null));
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{(\d+)\}").Select(match => match.Groups[1].Value)];
}

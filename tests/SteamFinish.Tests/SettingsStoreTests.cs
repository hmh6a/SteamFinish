using SteamFinish.Core.Power;
using SteamFinish.Core.Settings;

namespace SteamFinish.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void MissingFileYieldsDefaults()
    {
        using var temp = new TempFolder();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json")).Load();

        Assert.Equal(PowerAction.Shutdown, settings.Action);
        Assert.Equal(60, settings.CountdownSeconds);
        Assert.Equal(45, settings.ConfirmationSeconds);
        Assert.True(settings.RequireDownloadBeforeAction);
    }

    [Fact]
    public void SettingsSurviveARoundTrip()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(Path.Combine(temp.Root, "settings.json"));

        store.Save(new AppSettings
        {
            Action = PowerAction.Hibernate,
            CountdownSeconds = 120,
            ConfirmationSeconds = 30,
            SoundNotification = false,
            AutoDetectLibraries = false,
            ManualLibraries = [@"D:\Games"],
        });

        var loaded = store.Load();

        Assert.Equal(PowerAction.Hibernate, loaded.Action);
        Assert.Equal(120, loaded.CountdownSeconds);
        Assert.Equal(30, loaded.ConfirmationSeconds);
        Assert.False(loaded.SoundNotification);
        Assert.False(loaded.AutoDetectLibraries);
        Assert.Equal([@"D:\Games"], loaded.ManualLibraries);
    }

    [Fact]
    public void CorruptFilesFallBackToDefaults()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Equal(60, new SettingsStore(path).Load().CountdownSeconds);
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        var settings = new AppSettings
        {
            CountdownSeconds = 100_000,
            ConfirmationSeconds = 0,
            PollIntervalSeconds = 0,
        }.Normalize();

        Assert.Equal(AppSettings.MaxCountdownSeconds, settings.CountdownSeconds);
        Assert.Equal(AppSettings.MinConfirmationSeconds, settings.ConfirmationSeconds);
        Assert.Equal(1, settings.PollIntervalSeconds);
    }

    [Fact]
    public void DuplicateAndBlankLibraryPathsAreCleanedUp()
    {
        var settings = new AppSettings
        {
            ManualLibraries = [@"D:\Games\", "   ", @"d:\games", @"E:\Other"],
        }.Normalize();

        Assert.Equal([@"D:\Games", @"E:\Other"], settings.ManualLibraries);
    }

    [Fact]
    public void MonitorOptionsMirrorTheSavedTiming()
    {
        var options = new AppSettings
        {
            CountdownSeconds = 90,
            ConfirmationSeconds = 30,
            RequireDownloadBeforeAction = false,
            IgnorePausedDownloads = true,
        }.ToMonitorOptions();

        Assert.Equal(TimeSpan.FromSeconds(90), options.Countdown);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ConfirmationWindow);
        Assert.False(options.RequireDownloadFirst);
        Assert.True(options.IgnorePaused);
    }
}

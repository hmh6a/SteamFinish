using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Notifications;
using SteamFinish.Core.Power;
using SteamFinish.Core.Steam;
using static SteamFinish.Tests.TestData;

namespace SteamFinish.Tests;

public class DownloadSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NothingIsRecordedUntilAnActualDownloadAppears()
    {
        var session = new DownloadSession();

        session.Observe(Idle() with { TakenAt = Start });

        Assert.False(session.IsRunning);
        Assert.Null(session.Summarize(Start));
    }

    [Fact]
    public void EveryGameInTheBatchIsRemembered()
    {
        var session = new DownloadSession();

        session.Observe(Snapshot(
            App(AppStateFlags.UpdateStarted | AppStateFlags.Locked, toDownload: 20_000_000_000, toStage: 40_000_000_000, appId: 1, name: "First"),
            App(AppStateFlags.UpdateStarted, toDownload: 5_000_000_000, toStage: 9_000_000_000, appId: 2, name: "Second")) with
        {
            TakenAt = Start,
        });

        var summary = session.Summarize(Start.AddHours(2))!;

        Assert.Equal(2, summary.Games.Count);
        Assert.Equal(25_000_000_000, summary.TotalDownloadBytes);
        Assert.Equal(49_000_000_000, summary.TotalInstallBytes);
        Assert.Equal(TimeSpan.FromHours(2), summary.Duration);
        Assert.Equal("First", summary.Games[0].Name);
    }

    [Fact]
    public void SizesGrowToTheLargestValueSteamEventuallyReports()
    {
        var session = new DownloadSession();

        // Steam fills the totals in only once it has fetched the manifest.
        session.Observe(Snapshot(App(AppStateFlags.UpdateStarted | AppStateFlags.Locked, toDownload: 0, appId: 1, name: "Game")) with { TakenAt = Start });
        session.Observe(Snapshot(App(AppStateFlags.UpdateStarted | AppStateFlags.Locked, toDownload: 8_000_000_000, appId: 1, name: "Game")) with { TakenAt = Start.AddSeconds(5) });

        Assert.Equal(8_000_000_000, session.Summarize(Start.AddMinutes(10))!.TotalDownloadBytes);
    }

    [Fact]
    public void DurationRunsFromTheFirstDownloadSeen()
    {
        var session = new DownloadSession();
        session.Observe(Downloading() with { TakenAt = Start });
        session.Observe(Downloading() with { TakenAt = Start.AddMinutes(30) });

        Assert.Equal(TimeSpan.FromMinutes(45), session.Summarize(Start.AddMinutes(45))!.Duration);
    }

    [Fact]
    public void AverageSpeedComesFromTheSizeAndTheDuration()
    {
        var session = new DownloadSession();
        session.Observe(Snapshot(App(AppStateFlags.UpdateStarted | AppStateFlags.Locked, toDownload: 3_600_000_000, appId: 1)) with { TakenAt = Start });

        var summary = session.Summarize(Start.AddHours(1))!;

        Assert.Equal(1_000_000, summary.AverageBytesPerSecond, 0);
    }

    [Fact]
    public void ResetClearsTheBatch()
    {
        var session = new DownloadSession();
        session.Observe(Downloading() with { TakenAt = Start });

        session.Reset();

        Assert.False(session.IsRunning);
        Assert.Null(session.Summarize(Start.AddHours(1)));
    }
}

public class NotificationMessageTests
{
    private static DownloadSummary SampleSummary() => new(
        TimeSpan.FromHours(3) + TimeSpan.FromMinutes(47),
        [new SessionGame(1, "DARK SOULS III", 25_459_276_064, 26_649_084_250)],
        25_459_276_064,
        26_649_084_250);

    [Fact]
    public void GameNamesAreEscapedForTelegramsHtmlMode()
    {
        var app = App(AppStateFlags.Downloading, toDownload: 100, name: "Sam & Max <Season 1>");

        var message = NotificationMessages.DownloadStarted(MessageLanguage.English, app, 0);

        Assert.Contains("Sam &amp; Max &lt;Season 1&gt;", message, StringComparison.Ordinal);
        Assert.DoesNotContain("<Season", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFinishMessageCarriesTheGameSizeDurationAndAction()
    {
        var message = NotificationMessages.Finished(
            MessageLanguage.English, SampleSummary(), PowerAction.Shutdown, 60);

        Assert.Contains("DARK SOULS III", message, StringComparison.Ordinal);
        Assert.Contains("23.7 GB", message, StringComparison.Ordinal);
        Assert.Contains("3 h 47 min", message, StringComparison.Ordinal);
        Assert.Contains("Shutdown</b> in 60 seconds", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArabicFinishMessageIsWrittenInArabic()
    {
        var message = NotificationMessages.Finished(
            MessageLanguage.Arabic, SampleSummary(), PowerAction.Shutdown, 60);

        Assert.Contains("اكتملت جميع التنزيلات", message, StringComparison.Ordinal);
        Assert.Contains("إطفاء الحاسبة", message, StringComparison.Ordinal);
        Assert.Contains("3 ساعات و47 دقيقة", message, StringComparison.Ordinal);
        Assert.Contains("DARK SOULS III", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "ساعة واحدة")]
    [InlineData(2, "ساعتان")]
    [InlineData(5, "5 ساعات")]
    [InlineData(13, "13 ساعة")]
    public void ArabicCountsUseTheRightFormForTheNumber(int hours, string expected)
    {
        var summary = new DownloadSummary(TimeSpan.FromHours(hours), [new SessionGame(1, "G", 1, 1)], 1, 1);

        var message = NotificationMessages.Finished(MessageLanguage.Arabic, summary, PowerAction.Sleep, 30);

        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProgressMessageShowsABarAndTheBytes()
    {
        var app = App(AppStateFlags.Downloading, downloaded: 5_368_709_120, toDownload: 21_474_836_480, name: "Game");

        var message = NotificationMessages.Progress(
            MessageLanguage.English, app, 25, 800_000, TimeSpan.FromHours(1), queueCount: 2);

        Assert.Contains("25%", message, StringComparison.Ordinal);
        Assert.Contains("▰▰▱▱▱▱▱▱▱▱", message, StringComparison.Ordinal);
        Assert.Contains("5.0 GB / 20.0 GB", message, StringComparison.Ordinal);
        Assert.Contains("6.4 Mbps", message, StringComparison.Ordinal);
        Assert.Contains("01:00:00", message, StringComparison.Ordinal);
        Assert.Contains("In queue: 2", message, StringComparison.Ordinal);
    }
}

public class TelegramNotifierTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DownloadSnapshot Downloading(double fraction, string name = "Game", uint appId = 1) =>
        Snapshot(App(
            AppStateFlags.UpdateStarted | AppStateFlags.Locked,
            downloaded: (long)(fraction * 1_000_000),
            toDownload: 1_000_000,
            appId: appId,
            name: name)) with
        {
            TakenAt = Start,
        };

    private static (TelegramNotifier Notifier, List<string> Sent) Build(TelegramOptions options)
    {
        var sent = new List<string>();
        var client = new RecordingTelegramClient(sent);
        return (new TelegramNotifier(() => options, client), sent);
    }

    private static TelegramOptions Options() => new()
    {
        Enabled = true,
        BotToken = "123456789:AAsomethingthatlookslikeatoken",
        ChatIds = ["12345"],
        ProgressStepPercent = 5,
        NotifyOnStart = false,
    };

    [Fact]
    public void TokenValidationRejectsObviousMistakes()
    {
        Assert.False(TelegramOptions.LooksLikeToken(null));
        Assert.False(TelegramOptions.LooksLikeToken("  "));
        Assert.False(TelegramOptions.LooksLikeToken("nocolonhere-but-quite-long"));
        Assert.True(TelegramOptions.LooksLikeToken("123456789:AAsomethingthatlookslikeatoken"));
    }

    [Fact]
    public void OptionsAreOnlyUsableOnceEnabledWithATokenAndAChat()
    {
        var options = Options();
        Assert.True(options.IsUsable);

        var disabled = options.Clone();
        disabled.Enabled = false;
        Assert.False(disabled.IsUsable);

        var noChats = options.Clone();
        noChats.ChatIds = [];
        Assert.False(noChats.IsUsable);

        var noToken = options.Clone();
        noToken.BotToken = string.Empty;
        Assert.False(noToken.IsUsable);
    }

    [Fact]
    public void AMessageArrivesAtEveryStepAcrossAWholeDownload()
    {
        // The reported symptom: nothing at 5%, 10%, 15% … 95%.
        var (notifier, sent) = Build(Options());
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0), meter);
        for (var percent = 1; percent <= 100; percent++)
        {
            notifier.OnSnapshot(Downloading(percent / 100d), meter);
        }

        // 5, 10, 15 … 100 — twenty steps.
        Assert.Equal(20, sent.Count);
        foreach (var expected in new[] { "5%", "50%", "95%", "100%" })
        {
            Assert.Contains(sent, message => message.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ProgressIsTrackedThroughTheInstallPhaseToo()
    {
        // Real manifests carry staging counters, and Progress then follows the staged share.
        var (notifier, sent) = Build(Options());
        var meter = new TransferMeter();

        DownloadSnapshot Staging(double staged) =>
            Snapshot(App(
                AppStateFlags.UpdateStarted,
                downloaded: 24_168_810_176,
                toDownload: 24_168_810_176,
                staged: (long)(staged * 56_600_000_000),
                toStage: 56_600_000_000,
                appId: 1,
                name: "Khazan")) with
            { TakenAt = Start };

        notifier.OnSnapshot(Staging(0.70), meter);
        notifier.OnSnapshot(Staging(0.76), meter);
        notifier.OnSnapshot(Staging(0.81), meter);

        Assert.Equal(2, sent.Count);
        Assert.Contains("75%", sent[0], StringComparison.Ordinal);
        Assert.Contains("80%", sent[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TurningProgressMessagesOffStopsThemButKeepsThePlaceMarked()
    {
        var options = Options();
        var (notifier, sent) = Build(options);
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.10), meter);

        options.NotifyOnProgress = false;
        notifier.OnSnapshot(Downloading(0.50), meter);
        Assert.Empty(sent);

        // Switching back on resumes from where the download actually is, with no catch-up burst.
        options.NotifyOnProgress = true;
        notifier.OnSnapshot(Downloading(0.55), meter);

        var message = Assert.Single(sent);
        Assert.Contains("55%", message, StringComparison.Ordinal);
    }

    [Fact]
    public void JoiningHalfwayThroughDoesNotFireACatchUpBurst()
    {
        var (notifier, sent) = Build(Options());
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.37), meter);

        Assert.Empty(sent);
    }

    [Fact]
    public void OneMessageIsSentForEachStepCrossed()
    {
        var (notifier, sent) = Build(Options());
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.00), meter);
        notifier.OnSnapshot(Downloading(0.03), meter);
        Assert.Empty(sent);

        notifier.OnSnapshot(Downloading(0.05), meter);
        Assert.Single(sent);
        Assert.Contains("5%", sent[0], StringComparison.Ordinal);

        notifier.OnSnapshot(Downloading(0.07), meter);
        Assert.Single(sent);

        notifier.OnSnapshot(Downloading(0.10), meter);
        Assert.Equal(2, sent.Count);
        Assert.Contains("10%", sent[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeJumpStillOnlyProducesOneMessage()
    {
        var (notifier, sent) = Build(Options());
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.00), meter);
        notifier.OnSnapshot(Downloading(0.62), meter);

        Assert.Single(sent);
        Assert.Contains("60%", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepSizeIsConfigurable()
    {
        var options = Options();
        options.ProgressStepPercent = 25;
        var (notifier, sent) = Build(options);
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.00), meter);
        notifier.OnSnapshot(Downloading(0.10), meter);
        notifier.OnSnapshot(Downloading(0.20), meter);
        Assert.Empty(sent);

        notifier.OnSnapshot(Downloading(0.26), meter);
        Assert.Single(sent);
        Assert.Contains("25%", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public void EachGameGetsItsOwnProgressSteps()
    {
        var (notifier, sent) = Build(Options());
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.00, "First", 1), meter);
        notifier.OnSnapshot(Downloading(0.06, "First", 1), meter);
        Assert.Single(sent);

        // A different game starts from scratch and reports its own 5%.
        notifier.OnSnapshot(Downloading(0.00, "Second", 2), meter);
        notifier.OnSnapshot(Downloading(0.06, "Second", 2), meter);

        Assert.Equal(2, sent.Count);
        Assert.Contains("Second", sent[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NewDownloadsAreAnnouncedWhenThatIsTurnedOn()
    {
        var options = Options();
        options.NotifyOnStart = true;
        options.NotifyOnProgress = false;
        var (notifier, sent) = Build(options);
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.00, "First", 1), meter);
        Assert.Empty(sent);

        notifier.OnSnapshot(Downloading(0.10, "Second", 2), meter);

        Assert.Single(sent);
        Assert.Contains("Second", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsSentWhileTelegramIsSwitchedOff()
    {
        var options = Options();
        options.Enabled = false;
        var (notifier, sent) = Build(options);
        var meter = new TransferMeter();

        notifier.OnSnapshot(Downloading(0.00), meter);
        notifier.OnSnapshot(Downloading(0.50), meter);

        Assert.Empty(sent);
    }

    [Fact]
    public void TheFinishMessageIsSentWhenTheCountdownStarts()
    {
        var (notifier, sent) = Build(Options());
        var summary = new DownloadSummary(
            TimeSpan.FromMinutes(90), [new SessionGame(1, "Game", 1_000, 2_000)], 1_000, 2_000);

        notifier.OnCountdownStarted(summary, PowerAction.Shutdown, 60);

        Assert.Single(sent);
        Assert.Contains("اكتملت جميع التنزيلات", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ACancelledCountdownIsReportedWithItsReason()
    {
        var (notifier, sent) = Build(Options());

        notifier.OnCountdownCancelled(PowerAction.Shutdown, CountdownCancelReason.NewActivity);

        Assert.Single(sent);
        Assert.Contains("بدأ تنزيل جديد", sent[0], StringComparison.Ordinal);
    }

    /// <summary>Captures the message text instead of calling Telegram.</summary>
    private sealed class RecordingTelegramClient(List<string> sent) : ITelegramSender
    {
        public Task<TelegramResult> SendAsync(
            TelegramOptions options,
            string html,
            CancellationToken cancellationToken = default)
        {
            sent.Add(html);
            return Task.FromResult(TelegramResult.Ok("recorded"));
        }

        public Task<TelegramResult> TestAsync(
            TelegramOptions options,
            string html,
            CancellationToken cancellationToken = default) =>
            SendAsync(options, html, cancellationToken);
    }
}

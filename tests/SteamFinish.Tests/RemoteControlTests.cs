using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Notifications;
using SteamFinish.Core.Power;
using static SteamFinish.Tests.TestData;

namespace SteamFinish.Tests;

public class RemoteControlTokenTests
{
    [Fact]
    public void EachCountdownGetsItsOwnToken()
    {
        var first = RemoteControl.NewToken();
        var second = RemoteControl.NewToken();

        Assert.NotEqual(first, second);
        Assert.NotEmpty(first);
    }

    [Fact]
    public void BothButtonsRoundTrip()
    {
        var token = RemoteControl.NewToken();

        Assert.Equal(RemoteDecision.Now, RemoteControl.Parse(RemoteControl.DataFor(RemoteDecision.Now, token), token));
        Assert.Equal(RemoteDecision.Skip, RemoteControl.Parse(RemoteControl.DataFor(RemoteDecision.Skip, token), token));
    }

    [Fact]
    public void AButtonFromAnEarlierCountdownIsRejected()
    {
        // The old message stays in the chat forever; pressing it must not power the PC off.
        var stale = RemoteControl.DataFor(RemoteDecision.Now, RemoteControl.NewToken());

        Assert.Null(RemoteControl.Parse(stale, RemoteControl.NewToken()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sf:now")]
    [InlineData("other:now:TOKEN")]
    [InlineData("sf:reboot:TOKEN")]
    [InlineData("TOKEN")]
    public void MalformedPayloadsAreRejected(string data)
    {
        Assert.Null(RemoteControl.Parse(data, "TOKEN"));
    }

    [Fact]
    public void ThePayloadFitsTelegramsSixtyFourByteLimit()
    {
        var data = RemoteControl.DataFor(RemoteDecision.Skip, RemoteControl.NewToken());

        Assert.True(data.Length <= 64, $"'{data}' is {data.Length} characters.");
    }
}

public class CallbackReaderTests
{
    [Fact]
    public void ReadsAButtonPress()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":42,"callback_query":{
              "id":"4382bfdwdsb323b2d9","from":{"id":6186199202,"first_name":"Hussam","username":"hmh6a"},
              "message":{"message_id":77,"chat":{"id":6186199202,"type":"private"}},
              "data":"sf:skip:AABBCCDDEEFF"}}]}
            """);

        var update = Assert.Single(updates.Updates);
        var callback = update.Callback!;

        Assert.Equal(42, update.UpdateId);
        Assert.Equal("4382bfdwdsb323b2d9", callback.Id);
        Assert.Equal("6186199202", callback.ChatId);
        Assert.Equal(77, callback.MessageId);
        Assert.Equal("Hussam", callback.From);
        Assert.Equal(RemoteDecision.Skip, RemoteControl.Parse(callback.Data, "AABBCCDDEEFF"));
    }

    [Fact]
    public void AnOrdinaryMessageCarriesNoCallback()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":1,"message":{"chat":{"id":7,"type":"private"},"text":"/start"}}]}
            """);

        Assert.Null(Assert.Single(updates.Updates).Callback);
    }
}

public class RemoteMessageTests
{
    [Theory]
    [InlineData(MessageLanguage.English)]
    [InlineData(MessageLanguage.Arabic)]
    public void EachDecisionProducesItsOwnReply(MessageLanguage language)
    {
        var now = NotificationMessages.DecisionTaken(language, RemoteDecision.Now, PowerAction.Shutdown, "Hussam");
        var skip = NotificationMessages.DecisionTaken(language, RemoteDecision.Skip, PowerAction.Shutdown, "Hussam");

        Assert.NotEqual(now, skip);
        Assert.Contains("Hussam", now, StringComparison.Ordinal);
        Assert.Contains("Hussam", skip, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepliesSayWhatActuallyHappened()
    {
        var now = NotificationMessages.DecisionTaken(
            MessageLanguage.English, RemoteDecision.Now, PowerAction.Shutdown, "Hussam");
        var skip = NotificationMessages.DecisionTaken(
            MessageLanguage.English, RemoteDecision.Skip, PowerAction.Shutdown, "Hussam");

        Assert.Contains("started now", now, StringComparison.Ordinal);
        Assert.Contains("cancelled", skip, StringComparison.Ordinal);
        Assert.Contains("stays on", skip, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameFromTelegramCannotInjectMarkup()
    {
        var message = NotificationMessages.DecisionTaken(
            MessageLanguage.English, RemoteDecision.Now, PowerAction.Shutdown, "<b>evil</b>");

        Assert.DoesNotContain("<b>evil", message, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;evil", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheButtonLabelsNameTheAction()
    {
        Assert.Contains("Sleep", NotificationMessages.ButtonShutdownNow(MessageLanguage.English, PowerAction.Sleep), StringComparison.Ordinal);
        Assert.NotEmpty(NotificationMessages.ButtonSkip(MessageLanguage.Arabic));
    }
}

/// <summary>Records plain sends, so a prompt that falls back to one can be spotted.</summary>
internal sealed class NullSender(List<string> sent) : ITelegramSender
{
    public Task<TelegramResult> SendAsync(TelegramOptions options, string html, CancellationToken cancellationToken = default)
    {
        sent.Add(html);
        return Task.FromResult(TelegramResult.Ok("recorded"));
    }

    public Task<TelegramResult> TestAsync(TelegramOptions options, string html, CancellationToken cancellationToken = default) =>
        Task.FromResult(TelegramResult.Ok("recorded"));
}

/// <summary>Drives the whole prompt through a stand-in for Telegram.</summary>
public class RemotePromptTests
{
    private static TelegramOptions Options() => new()
    {
        Enabled = true,
        BotToken = "123456789:AAsomethingthatlookslikeatoken",
        ChatIds = ["111", "222"],
        RemoteButtons = true,
        Language = MessageLanguage.English,
    };

    [Fact]
    public async Task PressingRunNowAnswersEditsAndReportsTheDecision()
    {
        var remote = new FakeRemote(RemoteDecision.Now, from: "Hussam");
        var notifier = new TelegramNotifier(Options, new NullSender([]), null, remote);

        RemoteDecision? decision = null;
        notifier.RemoteDecisionMade += d => decision = d;

        notifier.OnCountdownStarted(null, PowerAction.Shutdown, 60);
        await remote.Completed;

        Assert.Equal(RemoteDecision.Now, decision);

        // Both chats got the buttons, and both copies were rewritten afterwards.
        Assert.Equal(2, remote.Sent.Count);
        Assert.Equal(2, remote.Edited.Count);
        Assert.True(remote.Answered);
        Assert.Contains("Hussam", remote.LastEdit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PressingDontLeavesTheChatSayingSo()
    {
        var remote = new FakeRemote(RemoteDecision.Skip, from: "Hussam");
        var notifier = new TelegramNotifier(Options, new NullSender([]), null, remote);

        RemoteDecision? decision = null;
        notifier.RemoteDecisionMade += d => decision = d;

        notifier.OnCountdownStarted(null, PowerAction.Shutdown, 60);
        await remote.Completed;

        Assert.Equal(RemoteDecision.Skip, decision);
        Assert.Contains("cancelled", remote.LastEdit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithButtonsTurnedOffAnOrdinaryMessageIsSent()
    {
        var options = Options();
        options.RemoteButtons = false;

        var sent = new List<string>();
        var remote = new FakeRemote(RemoteDecision.Now);
        var notifier = new TelegramNotifier(() => options, new NullSender(sent), null, remote);

        notifier.OnCountdownStarted(null, PowerAction.Shutdown, 60);

        Assert.Empty(remote.Sent);
        Assert.Single(sent);
    }

    private sealed class FakeRemote(RemoteDecision decision, string from = "") : ITelegramRemoteControl
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Sent { get; } = [];

        public List<string> Edited { get; } = [];

        public string LastEdit { get; private set; } = string.Empty;

        public bool Answered { get; private set; }

        public Task Completed => _completed.Task;

        public Task<IReadOnlyList<PromptTarget>> SendWithButtonsAsync(
            TelegramOptions options,
            string html,
            string nowLabel,
            string skipLabel,
            string token,
            CancellationToken cancellationToken = default)
        {
            Sent.AddRange(options.ChatIds);
            IReadOnlyList<PromptTarget> targets = [.. options.ChatIds.Select((id, i) => new PromptTarget(id, i + 1))];
            return Task.FromResult(targets);
        }

        public Task<(TelegramCallback Callback, RemoteDecision Decision)?> WaitForDecisionAsync(
            string botToken,
            string token,
            CancellationToken cancellationToken = default)
        {
            var callback = new TelegramCallback("cb1", "111", 1, RemoteControl.DataFor(decision, token), from);
            return Task.FromResult<(TelegramCallback, RemoteDecision)?>((callback, decision));
        }

        public Task AnswerCallbackAsync(string botToken, string callbackId, string text, CancellationToken cancellationToken = default)
        {
            Answered = true;
            return Task.CompletedTask;
        }

        public Task EditAllAsync(string botToken, IReadOnlyList<PromptTarget> targets, string html, CancellationToken cancellationToken = default)
        {
            Edited.AddRange(targets.Select(t => t.ChatId));
            LastEdit = html;
            _completed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}

public class RemoteDecisionFlowTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MonitorEngine CountingDown()
    {
        var engine = new MonitorEngine(() => new MonitorOptions
        {
            ConfirmationWindow = TimeSpan.FromSeconds(45),
            Countdown = TimeSpan.FromSeconds(60),
        });

        engine.Enable();
        engine.Update(Downloading(), Start);
        engine.Update(Idle(), Start.AddSeconds(10));
        engine.Update(Idle(), Start.AddSeconds(60));
        Assert.Equal(MonitorPhase.Countdown, engine.Phase);
        return engine;
    }

    [Fact]
    public void RunNowFiresTheActionWithoutWaiting()
    {
        var engine = CountingDown();
        var fired = 0;
        var cancelled = 0;
        engine.ActionDue += () => fired++;
        engine.CountdownCancelled += _ => cancelled++;

        Assert.True(engine.RunNow());

        Assert.Equal(1, fired);
        Assert.Equal(MonitorPhase.Executing, engine.Phase);

        // "Run it now" is not a cancellation, so nothing should announce one.
        Assert.Equal(0, cancelled);
    }

    [Fact]
    public void RunNowIsIgnoredOutsideACountdown()
    {
        var engine = new MonitorEngine();
        var fired = 0;
        engine.ActionDue += () => fired++;

        Assert.False(engine.RunNow());
        Assert.Equal(0, fired);
    }

    [Fact]
    public void RunNowCannotFireTwice()
    {
        var engine = CountingDown();
        var fired = 0;
        engine.ActionDue += () => fired++;

        Assert.True(engine.RunNow());
        Assert.False(engine.RunNow());
        Assert.Equal(1, fired);
    }

    [Fact]
    public void SkippingLeavesMonitoringOnButDisarmed()
    {
        var engine = CountingDown();
        var fired = 0;
        engine.ActionDue += () => fired++;

        Assert.True(engine.CancelCountdown());

        Assert.Equal(MonitorPhase.WaitingForDownload, engine.Phase);
        Assert.True(engine.IsEnabled);
        Assert.False(engine.IsArmed);

        // Staying idle must not start another countdown by itself.
        for (var minute = 2; minute < 20; minute++)
        {
            engine.Update(Idle(), Start.AddMinutes(minute));
        }

        Assert.Equal(0, fired);
    }
}

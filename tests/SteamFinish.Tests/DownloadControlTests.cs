using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Settings;
using SteamFinish.Core.Control;
using SteamFinish.Core.Notifications;
using SteamFinish.Core.Power;
using SteamFinish.Core.Steam;
using static SteamFinish.Tests.TestData;

namespace SteamFinish.Tests;

public class BotCommandTests
{
    [Theory]
    [InlineData("/pause", BotCommand.Pause)]
    [InlineData("/PAUSE", BotCommand.Pause)]
    [InlineData("/stop", BotCommand.Pause)]
    [InlineData("/resume", BotCommand.Resume)]
    [InlineData("/status", BotCommand.Status)]
    [InlineData("/help", BotCommand.Help)]
    [InlineData("/start", BotCommand.Help)]
    public void TheCommandsAreRecognised(string text, BotCommand expected)
    {
        Assert.Equal(expected, BotCommands.Parse(text)!.Command);
    }

    [Fact]
    public void AGroupMentionIsTrimmed()
    {
        // Telegram rewrites commands sent in a group as /pause@thebot.
        Assert.Equal(BotCommand.Pause, BotCommands.Parse("/pause@steamfinish_bot")!.Command);
    }

    [Fact]
    public void ACommandOnItsOwnIsForWhoeverIsReading()
    {
        Assert.Null(BotCommands.Parse("/resume")!.Target);
        Assert.True(BotCommands.IsFor(null, "DESKTOP-4F1QK"));
    }

    [Fact]
    public void WhatFollowsTheCommandNamesAPc()
    {
        var instruction = BotCommands.Parse("  /resume  laptop  ")!;

        Assert.Equal(BotCommand.Resume, instruction.Command);
        Assert.Equal("laptop", instruction.Target);
    }

    [Fact]
    public void APcNameWithSpacesSurvives()
    {
        Assert.Equal("Living Room PC", BotCommands.Parse("/pause Living Room PC")!.Target);
    }

    [Theory]
    [InlineData("DESKTOP-4F1QK", true)]
    [InlineData("desktop-4f1qk", true)]
    [InlineData("desk", true)]
    [InlineData("laptop", false)]
    [InlineData("desktop-9", false)]
    public void ANamedCommandOnlyReachesTheMachineItNames(string target, bool expected)
    {
        Assert.Equal(expected, BotCommands.IsFor(target, "DESKTOP-4F1QK"));
    }

    [Fact]
    public void AMachineWithNoNameAnswersOnlyUnnamedCommands()
    {
        Assert.True(BotCommands.IsFor(null, null));
        Assert.False(BotCommands.IsFor("laptop", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pause")]
    [InlineData("/")]
    [InlineData("/shutdown")]
    [InlineData("hello there")]
    public void AnythingElseIsNotACommand(string text)
    {
        Assert.Null(BotCommands.Parse(text));
    }

    [Fact]
    public void NullIsNotACommand()
    {
        Assert.Null(BotCommands.Parse(null));
    }
}

public class DownloadButtonTests
{
    [Fact]
    public void BothButtonsRoundTrip()
    {
        Assert.Equal(
            DownloadCommand.Pause,
            DownloadButtons.Parse(DownloadButtons.DataFor(DownloadCommand.Pause)));
        Assert.Equal(
            DownloadCommand.Resume,
            DownloadButtons.Parse(DownloadButtons.DataFor(DownloadCommand.Resume)));
    }

    [Fact]
    public void TheCountdownButtonsAreNotMistakenForDownloadButtons()
    {
        // Both live in the same chat; a shutdown must never be reachable through the pause path.
        var countdown = RemoteControl.DataFor(RemoteDecision.Now, RemoteControl.NewToken());

        Assert.Null(DownloadButtons.Parse(countdown));
    }

    [Fact]
    public void ADownloadButtonCannotSettleACountdown()
    {
        var token = RemoteControl.NewToken();

        Assert.Null(RemoteControl.Parse(DownloadButtons.DataFor(DownloadCommand.Pause), token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sfd")]
    [InlineData("sfd:")]
    [InlineData("sfd:pause:extra")]
    [InlineData("other:pause")]
    public void MalformedPayloadsAreRejected(string data)
    {
        Assert.Null(DownloadButtons.Parse(data));
    }

    [Fact]
    public void ThePayloadsFitTelegramsSixtyFourByteLimit()
    {
        Assert.True(DownloadButtons.DataFor(DownloadCommand.Pause).Length <= 64);
        Assert.True(DownloadButtons.DataFor(DownloadCommand.Resume).Length <= 64);
    }

    [Fact]
    public void TheKeyboardCarriesBothButtons()
    {
        var keyboard = TelegramKeyboard.PauseResume(MessageLanguage.English);

        Assert.Contains("inline_keyboard", keyboard, StringComparison.Ordinal);
        Assert.Contains(DownloadButtons.DataFor(DownloadCommand.Pause), keyboard, StringComparison.Ordinal);
        Assert.Contains(DownloadButtons.DataFor(DownloadCommand.Resume), keyboard, StringComparison.Ordinal);
    }
}

public class CommandMessageReaderTests
{
    [Fact]
    public void ReadsATypedCommand()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":9,"message":{"message_id":31,
              "from":{"id":6186199202,"first_name":"Hussam","username":"hmh6a"},
              "chat":{"id":6186199202,"type":"private"},"text":"/pause"}}]}
            """);

        var message = Assert.Single(updates.Updates).Message!;

        Assert.Equal("6186199202", message.ChatId);
        Assert.Equal(31, message.MessageId);
        Assert.Equal("/pause", message.Text);
        Assert.Equal("Hussam", message.From);
    }

    [Fact]
    public void AMessageWithoutTextCarriesNothingToActOn()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":9,"message":{"message_id":31,
              "chat":{"id":7,"type":"private"},"sticker":{"emoji":"🎮"}}}]}
            """);

        Assert.Null(Assert.Single(updates.Updates).Message);
    }

    [Fact]
    public void AButtonPressCarriesNoMessage()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":9,"callback_query":{"id":"cb","data":"sfd:pause",
              "message":{"message_id":1,"chat":{"id":7,"type":"private"}}}}]}
            """);

        var update = Assert.Single(updates.Updates);

        Assert.Null(update.Message);
        Assert.Equal(DownloadCommand.Pause, DownloadButtons.Parse(update.Callback!.Data));
    }

    [Fact]
    public void ChannelPostsCarryCommandsToo()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":9,"channel_post":{"message_id":4,
              "chat":{"id":-100123,"type":"channel","title":"Downloads"},"text":"/status"}}]}
            """);

        Assert.Equal("-100123", Assert.Single(updates.Updates).Message!.ChatId);
    }
}

public class ControlMessageTests
{
    [Theory]
    [InlineData(MessageLanguage.English)]
    [InlineData(MessageLanguage.Arabic)]
    public void PausingAndResumingReadDifferently(MessageLanguage language)
    {
        var paused = NotificationMessages.ControlDone(language, DownloadCommand.Pause);
        var resumed = NotificationMessages.ControlDone(language, DownloadCommand.Resume);

        Assert.NotEqual(paused, resumed);
        Assert.NotEmpty(paused);
    }

    [Theory]
    [InlineData(ControlOutcome.BridgeDisabled)]
    [InlineData(ControlOutcome.RestartSteam)]
    [InlineData(ControlOutcome.PortBusy)]
    [InlineData(ControlOutcome.SteamNotRunning)]
    [InlineData(ControlOutcome.SteamNotFound)]
    [InlineData(ControlOutcome.Refused)]
    [InlineData(ControlOutcome.Unreachable)]
    public void EveryFailureExplainsItself(ControlOutcome outcome)
    {
        foreach (var language in new[] { MessageLanguage.English, MessageLanguage.Arabic })
        {
            var message = NotificationMessages.ControlFailed(language, DownloadCommand.Pause, outcome);

            Assert.NotEmpty(message);
            Assert.DoesNotContain(outcome.ToString(), message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachFailureGivesItsOwnAdvice()
    {
        var reasons = Enum.GetValues<ControlOutcome>()
            .Where(outcome => outcome != ControlOutcome.Done)
            .Select(outcome => NotificationMessages.ControlFailed(
                MessageLanguage.English, DownloadCommand.Pause, outcome))
            .ToList();

        Assert.Equal(reasons.Count, reasons.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheStatusReportNamesTheGameAndTheQueue()
    {
        var message = NotificationMessages.Status(
            MessageLanguage.English, Downloading(), 12_000_000, TimeSpan.FromMinutes(4), true, PowerAction.Shutdown);

        Assert.Contains("Test Game", message, StringComparison.Ordinal);
        Assert.Contains("Monitoring is on", message, StringComparison.Ordinal);
        Assert.Contains("Shutdown", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusReportSaysWhenNothingIsHappening()
    {
        var message = NotificationMessages.Status(
            MessageLanguage.English, Idle(), 0, null, false, PowerAction.Shutdown);

        Assert.Contains("Nothing is downloading", message, StringComparison.Ordinal);
        Assert.Contains("Monitoring is off", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusReportAdmitsWhenItCannotRead()
    {
        var message = NotificationMessages.Status(
            MessageLanguage.English, null, 0, null, false, PowerAction.Shutdown);

        Assert.Contains("could not be read", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGameNameCannotInjectMarkupIntoTheStatusReport()
    {
        var snapshot = Snapshot(App(
            AppStateFlags.UpdateRunning | AppStateFlags.Downloading,
            downloaded: 4_000,
            toDownload: 10_000,
            name: "<b>evil</b>"));

        var message = NotificationMessages.Status(
            MessageLanguage.English, snapshot, 0, null, false, PowerAction.Shutdown);

        Assert.DoesNotContain("<b>evil", message, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;evil", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MessageLanguage.English)]
    [InlineData(MessageLanguage.Arabic)]
    public void TheHelpTextListsEveryCommand(MessageLanguage language)
    {
        var help = NotificationMessages.Help(language);

        Assert.Contains("/pause", help, StringComparison.Ordinal);
        Assert.Contains("/resume", help, StringComparison.Ordinal);
        Assert.Contains("/status", help, StringComparison.Ordinal);
    }
}

public class CommandListenerTests
{
    private const string Token = "123456789:AAsomethingthatlookslikeatoken";

    private static TelegramOptions Paired() => new()
    {
        Enabled = true,
        BotToken = Token,
        ChatIds = ["111"],
        RemoteCommands = true,
        Language = MessageLanguage.English,
    };

    private static TelegramUpdates Batch(params TelegramUpdate[] updates) => new(true, "ok", updates);

    private static TelegramUpdate Typed(long id, string chatId, string text) =>
        new(id, null, null, new TelegramMessage(chatId, id, text, "Hussam"));

    private static TelegramUpdate Pressed(long id, string chatId, string data) =>
        new(id, null, new TelegramCallback($"cb{id}", chatId, id, data, "Hussam"));

    [Fact]
    public async Task APauseCommandRunsThePauseAndSaysSo()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "111", "/pause")));

        var commands = new List<DownloadCommand>();
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            Control = (command, _, _) =>
            {
                commands.Add(command);
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.Equal(DownloadCommand.Pause, Assert.Single(commands));
        var (chatId, html, markup) = Assert.Single(conversation.Replies);
        Assert.Equal("111", chatId);
        Assert.Contains("paused", html, StringComparison.OrdinalIgnoreCase);

        // Every reply carries the buttons, so the next pause is a tap rather than a command.
        Assert.Contains(DownloadButtons.DataFor(DownloadCommand.Resume), markup!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCommandExplainsWhyRatherThanClaimingSuccess()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "111", "/resume")));

        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            Control = (_, _, _) => Task.FromResult(ControlResult.Fail(ControlOutcome.RestartSteam)),
        };

        listener.Sync();
        await conversation.Drained;

        var (_, html, _) = Assert.Single(conversation.Replies);
        Assert.Contains("Restart Steam", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNothingWiredUpTheCommandReportsThatRatherThanThrowing()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "111", "/pause")));

        using var listener = new TelegramCommandListener(Paired, conversation);

        listener.Sync();
        await conversation.Drained;

        Assert.Single(conversation.Replies);
    }

    [Fact]
    public async Task AnUnpairedChatIsIgnoredEntirely()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "999", "/pause")));

        var ran = false;
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            Control = (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.False(ran);

        // Not even a refusal: answering would tell a stranger the bot is live.
        Assert.Empty(conversation.Replies);
    }

    [Fact]
    public async Task AButtonPressIsAcknowledgedAndCarriedOut()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Pressed(1, "111", DownloadButtons.DataFor(DownloadCommand.Resume))));

        var commands = new List<DownloadCommand>();
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            Control = (command, _, _) =>
            {
                commands.Add(command);
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.Equal(DownloadCommand.Resume, Assert.Single(commands));
        Assert.Equal("cb1", Assert.Single(conversation.Answered).CallbackId);
        Assert.Single(conversation.Replies);
    }

    [Fact]
    public async Task ACountdownButtonIsLeftForTheCountdownToHandle()
    {
        var countdown = RemoteControl.DataFor(RemoteDecision.Now, RemoteControl.NewToken());
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Pressed(1, "111", countdown)));

        var ran = false;
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            Control = (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.False(ran);
        Assert.Empty(conversation.Replies);

        // The spinner is still cleared, so the chat does not look stuck.
        Assert.Single(conversation.Answered);
    }

    [Fact]
    public async Task CommandsSentWhileNobodyWasListeningAreNotObeyed()
    {
        // A "/pause" from last night must not stop tonight's download the moment the app starts.
        var conversation = new ScriptedConversation(
            Batch(Typed(7, "111", "/pause")),
            Batch());

        var ran = false;
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            Control = (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.False(ran);

        // The backlog is still acknowledged, so it cannot come back on the next poll.
        Assert.Contains(8L, conversation.Offsets.Where(o => o.HasValue).Select(o => o!.Value));
    }

    [Fact]
    public async Task AListenerThatStartsOfflineStillSkipsTheBacklog()
    {
        // The first poll cannot be reached, so the backlog has not been acknowledged yet. Once the
        // connection comes back, that backlog is still stale and must not be obeyed.
        var conversation = new ScriptedConversation(
            new TelegramUpdates(false, "Could not reach Telegram", []),
            Batch(Typed(7, "111", "/pause")),
            Batch());

        var ran = false;
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Control = (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.False(ran);
    }

    [Fact]
    public async Task ACommandAimedAtAnotherPcIsLeftForThatPc()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "111", "/pause laptop")));

        var ran = false;
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            DeviceName = () => "DESKTOP-4F1QK",
            Control = (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.False(ran);

        // Silence, not a refusal: the chat should end up with one answer, from the right machine.
        Assert.Empty(conversation.Replies);
    }

    [Fact]
    public async Task ACommandAimedAtThisPcIsCarriedOut()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "111", "/pause desktop")));

        var ran = false;
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            DeviceName = () => "DESKTOP-4F1QK",
            Control = (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.True(ran);
        var (_, html, _) = Assert.Single(conversation.Replies);
        Assert.Contains("DESKTOP-4F1QK", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatusCommandAnswersWithWhateverTheHostReports()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "111", "/status")));

        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            Status = () => "<b>all quiet</b>",
        };

        listener.Sync();
        await conversation.Drained;

        var (_, html, _) = Assert.Single(conversation.Replies);
        Assert.Equal("<b>all quiet</b>", html);
    }

    [Fact]
    public async Task TheResumeIsToldWhichDownloadIsAtTheFront()
    {
        var conversation = new ScriptedConversation(
            Batch(),
            Batch(Typed(1, "111", "/resume")));

        uint? seen = null;
        using var listener = new TelegramCommandListener(Paired, conversation)
        {
            CurrentAppId = () => 730u,
            Control = (_, appId, _) =>
            {
                seen = appId;
                return Task.FromResult(ControlResult.Done());
            },
        };

        listener.Sync();
        await conversation.Drained;

        Assert.Equal(730u, seen);
    }

    [Fact]
    public void TurningTheSettingOffStopsTheListener()
    {
        var options = Paired();
        var conversation = new ScriptedConversation(Batch());
        using var listener = new TelegramCommandListener(() => options, conversation);

        listener.Sync();
        Assert.True(listener.IsListening);

        options.RemoteCommands = false;
        listener.Sync();
        Assert.False(listener.IsListening);
    }

    [Fact]
    public void AnUnpairedBotIsNotListenedFor()
    {
        var options = Paired();
        options.ChatIds = [];

        using var listener = new TelegramCommandListener(() => options, new ScriptedConversation(Batch()));
        listener.Sync();

        Assert.False(listener.IsListening);
    }

    [Fact]
    public void SuspendingHandsTheConnectionOverAndGivesItBack()
    {
        // Telegram allows one listener per bot; pairing and the countdown both need it to themselves.
        var conversation = new ScriptedConversation(Batch());
        using var listener = new TelegramCommandListener(Paired, conversation);

        listener.Sync();
        Assert.True(listener.IsListening);

        var hold = listener.Suspend();
        Assert.False(listener.IsListening);

        // A second Sync while suspended must not sneak the listener back on.
        listener.Sync();
        Assert.False(listener.IsListening);

        hold.Dispose();
        Assert.True(listener.IsListening);
    }

    [Fact]
    public void NestedSuspensionsOnlyLiftWhenBothAreReleased()
    {
        var conversation = new ScriptedConversation(Batch());
        using var listener = new TelegramCommandListener(Paired, conversation);
        listener.Sync();

        var outer = listener.Suspend();
        var inner = listener.Suspend();

        inner.Dispose();
        Assert.False(listener.IsListening);

        outer.Dispose();
        Assert.True(listener.IsListening);
    }

    /// <summary>
    /// Stands in for Telegram: hands out scripted rounds of updates, then blocks like a long poll
    /// that never returns. <see cref="Drained"/> completes once the listener has asked for more,
    /// which cannot happen until every scripted update has been handled.
    /// </summary>
    private sealed class ScriptedConversation(params TelegramUpdates[] rounds) : ITelegramConversation
    {
        private readonly Queue<TelegramUpdates> _rounds = new(rounds);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _sync = new();

        public Task Drained => _drained.Task;

        public List<(string ChatId, string Html, string? Markup)> Replies { get; } = [];

        public List<(string CallbackId, string Text)> Answered { get; } = [];

        public List<long?> Offsets { get; } = [];

        public async Task<TelegramUpdates> PollAsync(
            string botToken,
            long? offset,
            int holdSeconds,
            string allowedUpdates,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Offsets.Add(offset);
                if (_rounds.Count > 0)
                {
                    return _rounds.Dequeue();
                }
            }

            _drained.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The listener was stopped, which is how every one of these tests ends.
            }

            return new TelegramUpdates(true, "ok", []);
        }

        public Task<TelegramResult> ReplyAsync(
            string botToken,
            string chatId,
            string html,
            string? replyMarkup = null,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Replies.Add((chatId, html, replyMarkup));
            }

            return Task.FromResult(TelegramResult.Ok("recorded"));
        }

        public Task AnswerCallbackAsync(
            string botToken,
            string callbackId,
            string text,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Answered.Add((callbackId, text));
            }

            return Task.CompletedTask;
        }
    }
}

public class BridgeSetupTests
{
    [Fact]
    public void TheMarkerGoesInsideTheSteamFolder()
    {
        var path = SteamCefBridge.MarkerPath(@"C:\Games\Steam");

        Assert.Equal(@"C:\Games\Steam\.cef-enable-remote-debugging", path);
    }

    [Fact]
    public void WithoutASteamFolderThereIsNowhereToPutIt()
    {
        Assert.Null(SteamCefBridge.MarkerPath(null));
        Assert.Null(SteamCefBridge.MarkerPath("  "));
        Assert.False(SteamCefBridge.MarkerExists(null));
    }

    [Fact]
    public void TheMarkerIsWrittenOnceAndThenLeftAlone()
    {
        var folder = Directory.CreateTempSubdirectory("steamfinish-bridge").FullName;
        try
        {
            Assert.False(SteamCefBridge.MarkerExists(folder));

            Assert.Equal(BridgeSetupOutcome.Enabled, SteamCefBridge.CreateMarker(folder).Outcome);
            Assert.True(SteamCefBridge.MarkerExists(folder));

            // Pressing the button twice must not look like a fresh change that needs a restart.
            Assert.Equal(BridgeSetupOutcome.AlreadyEnabled, SteamCefBridge.CreateMarker(folder).Outcome);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WithNoSteamFolderTheSetupSaysSoRatherThanFailing()
    {
        Assert.Equal(BridgeSetupOutcome.SteamNotFound, SteamCefBridge.CreateMarker(null).Outcome);
    }
}

/// <summary>
/// Drives the real DevTools conversation — page list, WebSocket upgrade, framing and all — against a
/// scripted endpoint, since the only other way to exercise it is a running Steam client.
/// </summary>
public class SteamBridgeWireTests
{
    private static string Evaluated(string expression) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 1,
            result = new { result = new { type = "string", value = expression.Contains("PauseAppUpdate(730)", StringComparison.Ordinal) ? "ok" : "unexpected" } },
        });

    [Fact]
    public async Task PausingReachesSteamAndComesBackWithAnAnswer()
    {
        string? seen = null;
        using var steam = FakeDevToolsEndpoint.LikeSteam(expression =>
        {
            seen = expression;
            return Evaluated(expression);
        });

        using var bridge = new SteamCefBridge(null, steam.Port);
        var reply = await bridge.EvaluateAsync(
            """(function(){SteamClient.Downloads.PauseAppUpdate(730);return "ok";})()""",
            CancellationToken.None);

        Assert.Equal(ControlOutcome.Done, reply.Outcome);
        Assert.Equal("ok", reply.Value);
        Assert.Contains("PauseAppUpdate(730)", seen!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSharedContextIsPickedOutOfThePageList()
    {
        // Steam lists several pages; only the offscreen one has the SteamClient object on it.
        using var steam = FakeDevToolsEndpoint.LikeSteam(_ => Evaluated("PauseAppUpdate(730)"));
        using var bridge = new SteamCefBridge(null, steam.Port);

        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(ControlOutcome.Done, reply.Outcome);
    }

    [Fact]
    public async Task AClientTooOldToHaveTheApiIsReportedAsARefusal()
    {
        using var steam = FakeDevToolsEndpoint.LikeSteam(_ => System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 1,
            result = new
            {
                result = new { type = "object" },
                exceptionDetails = new { text = "SteamClient is not defined" },
            },
        }));

        using var bridge = new SteamCefBridge(null, steam.Port);
        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(ControlOutcome.Refused, reply.Outcome);
        Assert.Contains("SteamClient", reply.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADevToolsEndpointThatIsNotSteamsIsNotMistakenForIt()
    {
        using var other = FakeDevToolsEndpoint.WithoutSharedContext();
        using var bridge = new SteamCefBridge(null, other.Port);

        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(ControlOutcome.Refused, reply.Outcome);
    }

    [Fact]
    public async Task AnUnrelatedServerOnThePortIsCalledOutAsSuch()
    {
        // This is not hypothetical: Docker and WSL both like port 8080.
        using var squatter = FakeDevToolsEndpoint.NotDevToolsAtAll();
        using var bridge = new SteamCefBridge(null, squatter.Port);

        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(ControlOutcome.PortBusy, reply.Outcome);
    }

    [Fact]
    public async Task NothingListeningMeansSteamHasNotBeenRestartedYet()
    {
        // Take a port and give it straight back, so nothing is listening on it.
        int free;
        using (var probe = FakeDevToolsEndpoint.NotDevToolsAtAll())
        {
            free = probe.Port;
        }

        using var bridge = new SteamCefBridge(null, free);
        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Contains(reply.Outcome, new[] { ControlOutcome.RestartSteam, ControlOutcome.SteamNotRunning });
    }

    [Fact]
    public async Task TheWholeControllerPausesThroughTheRealProtocol()
    {
        var folder = Directory.CreateTempSubdirectory("steamfinish-wire").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, SteamCefBridge.MarkerFileName), string.Empty);

            string? seen = null;
            using var steam = FakeDevToolsEndpoint.LikeSteam(expression =>
            {
                seen = expression;
                return Evaluated(expression);
            });

            using var controller = new SteamDownloadController(null, () => folder, steam.Port);
            var result = await controller.ApplyAsync(DownloadCommand.Pause, appId: 730);

            Assert.True(result.Success, $"Pausing failed: {result.Outcome} {result.Detail}");
            Assert.Contains("PauseAppUpdate(730)", seen!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AResumeNamesTheDownloadAtTheFrontOfTheQueue()
    {
        var folder = Directory.CreateTempSubdirectory("steamfinish-wire-resume").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, SteamCefBridge.MarkerFileName), string.Empty);

            string? seen = null;
            using var steam = FakeDevToolsEndpoint.LikeSteam(expression =>
            {
                seen = expression;
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = 1,
                    result = new { result = new { type = "string", value = "ok" } },
                });
            });

            using var controller = new SteamDownloadController(null, () => folder, steam.Port);
            var result = await controller.ApplyAsync(DownloadCommand.Resume, appId: 730);

            Assert.True(result.Success, $"Resuming failed: {result.Outcome} {result.Detail}");
            // The per-game pair, not the global switch: only these two undo each other.
            Assert.Contains("ResumeAppUpdate(730)", seen!, StringComparison.Ordinal);
            Assert.DoesNotContain("EnableAllDownloads", seen!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class SteamPortTests
{
    [Fact]
    public void AFreePortIsReallyFree()
    {
        var port = SteamPorts.FindFreePort();

        Assert.InRange(port, 1024, 65535);
        Assert.True(SteamPorts.IsPortFree(port));
    }

    [Fact]
    public void APortInUseIsNotOfferedAsFree()
    {
        using var held = FakeDevToolsEndpoint.NotDevToolsAtAll();

        Assert.False(SteamPorts.IsPortFree(held.Port));
        Assert.NotEqual(held.Port, SteamPorts.FindFreePort());
    }

    [Fact]
    public void TheListenerTableIsReadWithoutBlowingUp()
    {
        // Whether Steam is running here is not knowable, but the table has to parse either way —
        // a misread row would hand back nonsense port numbers rather than nothing.
        foreach (var port in SteamPorts.ListeningPorts())
        {
            Assert.InRange(port, 1, 65535);
        }
    }
}

public class PortDiscoveryTests
{
    [Fact]
    public async Task SteamIsFoundOnAPortThatIsNotTheDefault()
    {
        // The whole point: a Steam started with -devtools-port is found without being told.
        using var steam = FakeDevToolsEndpoint.LikeSteam(_ => Answered("ok"));
        using var bridge = new SteamCefBridge(null, SteamPorts.FindFreePort(), steam.Port);

        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(ControlOutcome.Done, reply.Outcome);
        Assert.Equal(steam.Port, bridge.LastGoodPort);
    }

    [Fact]
    public async Task SteamIsFoundEvenWithSomethingElseHoldingTheFirstPort()
    {
        using var squatter = FakeDevToolsEndpoint.NotDevToolsAtAll();
        using var steam = FakeDevToolsEndpoint.LikeSteam(_ => Answered("ok"));
        using var bridge = new SteamCefBridge(null, squatter.Port, steam.Port);

        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(ControlOutcome.Done, reply.Outcome);
        Assert.Equal(steam.Port, bridge.LastGoodPort);
    }

    [Fact]
    public async Task ThePortThatWorkedIsRememberedAndTriedFirst()
    {
        using var steam = FakeDevToolsEndpoint.LikeSteam(_ => Answered("ok"));
        using var bridge = new SteamCefBridge(null, steam.Port);

        await bridge.EvaluateAsync("1", CancellationToken.None);
        Assert.Equal(steam.Port, bridge.LastGoodPort);

        await bridge.EvaluateAsync("1", CancellationToken.None);
        Assert.Equal(steam.Port, bridge.LastGoodPort);
    }

    [Fact]
    public async Task APortThatStopsWorkingIsForgotten()
    {
        int port;
        using (var steam = FakeDevToolsEndpoint.LikeSteam(_ => Answered("ok")))
        {
            port = steam.Port;
            using var live = new SteamCefBridge(null, port);
            await live.EvaluateAsync("1", CancellationToken.None);
            Assert.Equal(port, live.LastGoodPort);
        }

        using var afterwards = new SteamCefBridge(null, port);
        await afterwards.EvaluateAsync("1", CancellationToken.None);

        Assert.Null(afterwards.LastGoodPort);
    }

    [Fact]
    public async Task SteamsOwnServicePortsAreNotMistakenForAPortClash()
    {
        // Steam listens on several ports that have nothing to do with DevTools and answer with
        // whatever they answer with. Calling that "another program holds the port" would send the
        // user hunting for a program that does not exist; the real answer is to restart Steam.
        // The first pinned port stands in for 8080: nothing there, and the clash is on a later one.
        using var friendsService = FakeDevToolsEndpoint.NotDevToolsAtAll();
        using var bridge = new SteamCefBridge(null, SteamPorts.FindFreePort(), friendsService.Port);

        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.NotEqual(ControlOutcome.PortBusy, reply.Outcome);
    }

    [Fact]
    public async Task AnotherProgramOnEveryCandidateIsReportedAsAPortClash()
    {
        // Nothing found, but something answered — which is a different problem from silence, and
        // the one the restart button fixes.
        using var first = FakeDevToolsEndpoint.NotDevToolsAtAll();
        using var second = FakeDevToolsEndpoint.NotDevToolsAtAll();
        using var bridge = new SteamCefBridge(null, first.Port, second.Port);

        var reply = await bridge.EvaluateAsync("1", CancellationToken.None);

        Assert.Equal(ControlOutcome.PortBusy, reply.Outcome);
    }

    private static string Answered(string value) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 1,
            result = new { result = new { type = "string", value } },
        });
}

public class PauseMechanismTests
{
    /// <summary>Records the script without needing a Steam on the other end.</summary>
    private static async Task<string> ScriptFor(DownloadCommand command, uint? appId)
    {
        var folder = Directory.CreateTempSubdirectory("steamfinish-script").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, SteamCefBridge.MarkerFileName), string.Empty);

            var seen = string.Empty;
            using var steam = FakeDevToolsEndpoint.LikeSteam(expression =>
            {
                seen = expression;
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = 1,
                    result = new { result = new { type = "string", value = "ok" } },
                });
            });

            using var controller = new SteamDownloadController(null, () => folder, steam.Port);
            await controller.ApplyAsync(command, appId);
            return seen;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(DownloadCommand.Pause)]
    [InlineData(DownloadCommand.Resume)]
    public async Task TheGlobalPauseSwitchIsNeverTouched(DownloadCommand command)
    {
        // EnableAllDownloads(false) does pause a download, and nothing puts it back: not
        // EnableAllDownloads(true), not ResumeAppUpdate, not QueueAppUpdate. Verified against a live
        // Steam, where it stayed paused through all three and only a restart cleared it. A pause the
        // phone cannot undo is worse than no pause, so only the per-game calls are used.
        var script = await ScriptFor(command, 730);

        Assert.DoesNotContain("EnableAllDownloads", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseAndResumeAreTheSamePairInBothDirections()
    {
        Assert.Contains("PauseAppUpdate(730)", await ScriptFor(DownloadCommand.Pause, 730), StringComparison.Ordinal);
        Assert.Contains("ResumeAppUpdate(730)", await ScriptFor(DownloadCommand.Resume, 730), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNothingDownloadingThereIsNothingToName()
    {
        // Both calls take an app id. Falling back to the global switch here would be the one-way
        // trap all over again, so it reports instead.
        var folder = Directory.CreateTempSubdirectory("steamfinish-noapp").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, SteamCefBridge.MarkerFileName), string.Empty);
            using var controller = new SteamDownloadController(null, () => folder);

            var result = await controller.ApplyAsync(DownloadCommand.Pause);

            Assert.Equal(ControlOutcome.NothingDownloading, result.Outcome);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class ControlSettingsTests
{
    [Fact]
    public void TheCommandSettingSurvivesACopy()
    {
        var options = new TelegramOptions { RemoteCommands = false };

        Assert.False(options.Clone().RemoteCommands);
        Assert.True(new TelegramOptions().Clone().RemoteCommands);
    }
}

public class DeviceNameTests
{
    [Fact]
    public void AnUnsetNameBecomesTheWindowsComputerName()
    {
        // Upgrading from a build without this setting starts naming the PC straight away.
        Assert.Equal(Environment.MachineName, new AppSettings().Normalize().DeviceName);
    }

    [Fact]
    public void AChosenNameIsKept()
    {
        var settings = new AppSettings { DeviceName = "  Living Room PC  " }.Normalize();

        Assert.Equal("Living Room PC", settings.DeviceName);
    }

    [Fact]
    public void ARidiculousNameIsCutDown()
    {
        var settings = new AppSettings { DeviceName = new string('x', 200) }.Normalize();

        Assert.Equal(AppSettings.MaxDeviceNameLength, settings.DeviceName.Length);
    }

    [Fact]
    public void TheNameSurvivesACopy()
    {
        var settings = new AppSettings { DeviceName = "Attic" };

        Assert.Equal("Attic", settings.Clone().DeviceName);
    }

    [Theory]
    [InlineData(MessageLanguage.English)]
    [InlineData(MessageLanguage.Arabic)]
    public void EveryMessageSaysWhichPcItCameFrom(MessageLanguage language)
    {
        const string Device = "Living Room PC";

        var messages = new[]
        {
            NotificationMessages.DownloadStarted(language, App(AppStateFlags.Downloading, toDownload: 10), 0, Device),
            NotificationMessages.FinishedWithoutDetails(language, PowerAction.Shutdown, 60, Device),
            NotificationMessages.Cancelled(language, PowerAction.Shutdown, CountdownCancelReason.User, Device),
            NotificationMessages.ControlDone(language, DownloadCommand.Pause, Device),
            NotificationMessages.ControlFailed(language, DownloadCommand.Pause, ControlOutcome.PortBusy, Device),
            NotificationMessages.Status(language, Idle(), 0, null, false, PowerAction.Shutdown, Device),
            NotificationMessages.Help(language, Device),
            NotificationMessages.Test(language, Device),
        };

        foreach (var message in messages)
        {
            Assert.Contains(Device, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithNoNameTheHeaderIsLeftPlain()
    {
        var message = NotificationMessages.ControlDone(MessageLanguage.English, DownloadCommand.Pause);

        Assert.Contains("SteamFinish", message, StringComparison.Ordinal);
        Assert.DoesNotContain("·", message, StringComparison.Ordinal);
    }

    [Fact]
    public void APcNameCannotInjectMarkup()
    {
        var message = NotificationMessages.ControlDone(
            MessageLanguage.English, DownloadCommand.Pause, "<b>evil</b>");

        Assert.DoesNotContain("<b>evil", message, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;evil", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelpTextExplainsHowToReachOnePcOutOfSeveral()
    {
        var help = NotificationMessages.Help(MessageLanguage.English, "laptop");

        Assert.Contains("/pause laptop", help, StringComparison.Ordinal);
    }
}

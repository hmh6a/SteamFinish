using System.Net;
using System.Net.Http;
using SteamFinish.Core.Notifications;

namespace SteamFinish.Tests;

public class TelegramUpdateReaderTests
{
    [Fact]
    public void ReadsAPrivateMessage()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":10,"message":{"message_id":1,
              "chat":{"id":123456789,"type":"private","first_name":"Hussam","last_name":"K"},
              "text":"/start"}}]}
            """);

        var update = Assert.Single(updates.Updates);
        Assert.True(updates.Ok);
        Assert.Equal(10, update.UpdateId);
        Assert.Equal("123456789", update.Chat!.ChatId);
        Assert.Equal("Hussam K", update.Chat.Title);
        Assert.Equal("private chat", update.Chat.Kind);
    }

    [Fact]
    public void ReadsAGroupMessage()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":11,"message":{"message_id":2,
              "chat":{"id":-1001234567890,"type":"supergroup","title":"Gaming Nights"},
              "text":"/start@finishbot"}}]}
            """);

        var chat = Assert.Single(updates.Updates).Chat!;
        Assert.Equal("-1001234567890", chat.ChatId);
        Assert.Equal("Gaming Nights", chat.Title);
        Assert.Equal("group", chat.Kind);
    }

    [Fact]
    public void ReadsAChannelPost()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":12,"channel_post":{"message_id":3,
              "chat":{"id":-1009876543210,"type":"channel","title":"My Downloads"}}}]}
            """);

        var chat = Assert.Single(updates.Updates).Chat!;
        Assert.Equal("-1009876543210", chat.ChatId);
        Assert.Equal("channel", chat.Kind);
    }

    [Fact]
    public void ReadsTheBotBeingAddedToAGroup()
    {
        // my_chat_member arrives even when privacy mode hides ordinary group messages,
        // so adding the bot to a group is enough to pair with it.
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":13,"my_chat_member":{
              "chat":{"id":-100555,"type":"group","title":"Squad"},
              "new_chat_member":{"status":"member"}}}]}
            """);

        Assert.Equal("Squad", Assert.Single(updates.Updates).Chat!.Title);
    }

    [Fact]
    public void FallsBackToTheUsernameThenTheIdForUnnamedChats()
    {
        var byUsername = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":1,"message":{"chat":{"id":42,"type":"private","username":"ghost"}}}]}
            """);
        Assert.Equal("@ghost", Assert.Single(byUsername.Updates).Chat!.Title);

        var byId = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":2,"message":{"chat":{"id":42,"type":"private"}}}]}
            """);
        Assert.Equal("42", Assert.Single(byId.Updates).Chat!.Title);
    }

    [Fact]
    public void KeepsUpdatesThatCarryNoChatSoTheOffsetStillAdvances()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":true,"result":[{"update_id":20,"poll_answer":{"poll_id":"7"}}]}
            """);

        var update = Assert.Single(updates.Updates);
        Assert.Equal(20, update.UpdateId);
        Assert.Null(update.Chat);
    }

    [Fact]
    public void SurfacesTelegramsOwnRefusal()
    {
        var updates = TelegramUpdateReader.Read("""
            {"ok":false,"error_code":401,"description":"Unauthorized"}
            """);

        Assert.False(updates.Ok);
        Assert.Equal("Unauthorized", updates.Description);
        Assert.Empty(updates.Updates);
    }

    [Fact]
    public void MalformedJsonIsReportedRatherThanThrown()
    {
        Assert.False(TelegramUpdateReader.Read("<html>502 Bad Gateway</html>").Ok);
    }

    [Fact]
    public void MyChatMemberIsRequestedExplicitly()
    {
        // It is not in Telegram's default allowed_updates set, so it has to be asked for.
        Assert.Contains("my_chat_member", TelegramUpdateReader.AllowedUpdatesJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePairingCodeIsSixDigits()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var code = PairingCode.Generate();
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }
}

public class TelegramChatFinderTests
{
    private const string Token = "123456789:AAFakeTokenForTestsOnly";

    [Fact]
    public async Task PairsWithTheChatAndDeliversACode()
    {
        using var handler = new StubHandler(
            Backlog(),
            """
            {"ok":true,"result":[{"update_id":5,"message":{
              "chat":{"id":777,"type":"private","first_name":"Hussam"}}}]}
            """,
            """{"ok":true,"result":{"message_id":1}}""");

        using var client = new TelegramClient(handler: handler);
        var result = await client.FindChatAsync(Token);

        Assert.True(result.Success);
        Assert.Equal("777", result.Chat!.ChatId);
        Assert.Equal("Hussam", result.Chat.Title);
        Assert.NotNull(result.Code);

        // The code shown on screen is the one that was actually posted to Telegram.
        var send = handler.Requests.Last();
        Assert.Contains("sendMessage", send.Url, StringComparison.Ordinal);
        Assert.Contains(result.Code!, send.Body, StringComparison.Ordinal);
        Assert.Contains("chat_id=777", send.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessagesAlreadyWaitingBeforeTheUserPressedFindAreSkipped()
    {
        // A stale message from someone else must not be paired with by mistake.
        using var handler = new StubHandler(
            """
            {"ok":true,"result":[{"update_id":40,"message":{
              "chat":{"id":111,"type":"private","first_name":"Stranger"}}}]}
            """,
            """
            {"ok":true,"result":[{"update_id":41,"message":{
              "chat":{"id":222,"type":"private","first_name":"Owner"}}}]}
            """,
            """{"ok":true,"result":{"message_id":1}}""");

        using var client = new TelegramClient(handler: handler);
        var result = await client.FindChatAsync(Token);

        Assert.Equal("222", result.Chat!.ChatId);

        // The backlog was acknowledged with offset 41 so Telegram would not resend it.
        Assert.Contains("offset=41", handler.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedTokenIsExplainedInPlainWords()
    {
        using var handler = new StubHandler("""{"ok":false,"description":"Unauthorized"}""");
        using var client = new TelegramClient(handler: handler);

        var result = await client.FindChatAsync(Token);

        Assert.False(result.Success);
        Assert.Contains("BotFather", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConflictingListenerIsExplained()
    {
        using var handler = new StubHandler(
            """{"ok":false,"description":"Conflict: terminated by other getUpdates request"}""");
        using var client = new TelegramClient(handler: handler);

        var result = await client.FindChatAsync(Token);

        Assert.False(result.Success);
        Assert.Contains("Another program", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedTokenIsRejectedWithoutCallingTelegram()
    {
        using var handler = new StubHandler();
        using var client = new TelegramClient(handler: handler);

        var result = await client.FindChatAsync("not-a-token");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GivingUpAfterTheSearchWindowSaysWhatToDo()
    {
        using var handler = new StubHandler { Fallback = Backlog() };
        using var client = new TelegramClient(handler: handler) { SearchWindow = TimeSpan.FromMilliseconds(50) };

        var result = await client.FindChatAsync(Token);

        Assert.False(result.Success);
        Assert.Contains("/start", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingStopsTheSearch()
    {
        using var handler = new StubHandler { Fallback = Backlog() };
        using var client = new TelegramClient(handler: handler);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync();
        var result = await client.FindChatAsync(Token, progress: null, cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("Cancelled", result.Message, StringComparison.Ordinal);
    }

    private static string Backlog() => """{"ok":true,"result":[]}""";

    /// <summary>Replays canned Telegram responses and records what was asked for.</summary>
    private sealed class StubHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;

        public List<(string Url, string Body)> Requests { get; } = [];

        /// <summary>Returned once the scripted responses run out.</summary>
        public string Fallback { get; set; } = """{"ok":true,"result":[]}""";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add((request.RequestUri!.ToString(), body));

            var payload = _index < responses.Length ? responses[_index++] : Fallback;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }
}

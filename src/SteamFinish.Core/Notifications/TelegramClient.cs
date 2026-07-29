using System.Net.Http;
using System.Text.Json;
using SteamFinish.Core.Logging;

namespace SteamFinish.Core.Notifications;

public sealed record TelegramResult(bool Success, string Message, long? MessageId = null)
{
    public static TelegramResult Ok(string message, long? messageId = null) => new(true, message, messageId);

    public static TelegramResult Fail(string message) => new(false, message);
}

/// <summary>Posts messages to Telegram. Split out from the client so it can be substituted in tests.</summary>
public interface ITelegramSender
{
    Task<TelegramResult> SendAsync(TelegramOptions options, string html, CancellationToken cancellationToken = default);

    Task<TelegramResult> TestAsync(TelegramOptions options, string html, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal Telegram Bot API client — just enough to check a token and post messages.
/// The token is never written to the log.
/// </summary>
public sealed class TelegramClient : ITelegramSender, ITelegramChatFinder, IDisposable
{
    private const string ApiRoot = "https://api.telegram.org";

    /// <summary>How long a single long-poll asks Telegram to hold the connection open.</summary>
    private static readonly TimeSpan PollHold = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;

    /// <summary>Separate client: long polling outlives the ordinary request timeout.</summary>
    private readonly HttpClient _pollHttp;

    private readonly bool _ownsHandler;
    private readonly ILog _log;

    /// <param name="handler">Substituted in tests; the real client creates its own.</param>
    public TelegramClient(ILog? log = null, HttpMessageHandler? handler = null)
    {
        _log = log ?? NullLog.Instance;
        _ownsHandler = handler is null;

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);

        _pollHttp = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _pollHttp.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>How long to keep listening for a message before giving up.</summary>
    public TimeSpan SearchWindow { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Posts the same HTML message to every configured chat.</summary>
    public async Task<TelegramResult> SendAsync(
        TelegramOptions options,
        string html,
        CancellationToken cancellationToken = default)
    {
        if (!TelegramOptions.LooksLikeToken(options.BotToken))
        {
            return TelegramResult.Fail("The bot token is missing or malformed.");
        }

        var chats = options.ChatIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (chats.Length == 0)
        {
            return TelegramResult.Fail("No chat ID has been added.");
        }

        var failures = new List<string>();
        foreach (var chatId in chats)
        {
            var result = await PostMessageAsync(options.BotToken, chatId, html, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                failures.Add($"{chatId}: {result.Message}");
            }
        }

        if (failures.Count == 0)
        {
            return TelegramResult.Ok($"Sent to {chats.Length} chat{(chats.Length == 1 ? string.Empty : "s")}.");
        }

        return TelegramResult.Fail(string.Join(" · ", failures));
    }

    /// <summary>Checks the token with <c>getMe</c>, then posts a test message to every chat.</summary>
    public async Task<TelegramResult> TestAsync(
        TelegramOptions options,
        string html,
        CancellationToken cancellationToken = default)
    {
        if (!TelegramOptions.LooksLikeToken(options.BotToken))
        {
            return TelegramResult.Fail("The bot token is missing or malformed. It looks like 123456789:AA…");
        }

        string botName;
        try
        {
            using var response = await _http
                .GetAsync($"{ApiRoot}/bot{options.BotToken}/getMe", cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (ok, description, username, _) = ReadResponse(body);

            if (!ok)
            {
                return TelegramResult.Fail($"The token was rejected: {description}");
            }

            botName = username is null ? "the bot" : $"@{username}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return TelegramResult.Fail($"Could not reach Telegram: {e.Message}");
        }

        var send = await SendAsync(options, html, cancellationToken).ConfigureAwait(false);
        return send.Success
            ? TelegramResult.Ok($"{botName} is connected. {send.Message}")
            : TelegramResult.Fail($"{botName} is valid, but the message failed — {send.Message}");
    }

    /// <summary>
    /// Listens for the next message the bot receives and sends a one-time code back to that chat.
    /// Anything already waiting on the server is skipped first, so only a message sent after the
    /// user pressed the button can pair.
    /// </summary>
    public async Task<ChatSearchResult> FindChatAsync(
        string botToken,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TelegramOptions.LooksLikeToken(botToken))
        {
            return ChatSearchResult.Fail("Paste the bot token first. It looks like 123456789:AA…");
        }

        progress?.Report("Connecting to Telegram…");

        var baseline = await GetUpdatesAsync(botToken, offset: null, holdSeconds: 0, cancellationToken)
            .ConfigureAwait(false);
        if (!baseline.Ok)
        {
            return ChatSearchResult.Fail(Explain(baseline.Description));
        }

        // Acknowledging the backlog means an old message cannot be mistaken for the user's.
        var offset = baseline.Updates.Count > 0 ? baseline.Updates[^1].UpdateId + 1 : 0;

        progress?.Report("Waiting for your message…");
        var deadline = DateTimeOffset.UtcNow + SearchWindow;

        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var poll = await GetUpdatesAsync(botToken, offset, (int)PollHold.TotalSeconds, cancellationToken)
                .ConfigureAwait(false);
            if (!poll.Ok)
            {
                return ChatSearchResult.Fail(Explain(poll.Description));
            }

            foreach (var update in poll.Updates)
            {
                offset = update.UpdateId + 1;
                if (update.Chat is not { } chat)
                {
                    continue;
                }

                progress?.Report($"Found {chat.Describe()}. Sending a code…");

                var code = PairingCode.Generate();
                var sent = await PostMessageAsync(botToken, chat.ChatId, PairingCode.Message(code), cancellationToken)
                    .ConfigureAwait(false);

                if (!sent.Success)
                {
                    return ChatSearchResult.Fail(
                        $"Found {chat.Describe()}, but the code could not be delivered: {sent.Message}");
                }

                _log.Info($"Telegram pairing: code sent to {chat.Describe()}.");
                return new ChatSearchResult(true, $"Code sent to {chat.Describe()}.", chat, code, sent.MessageId);
            }
        }

        return ChatSearchResult.Fail(cancellationToken.IsCancellationRequested
            ? "Cancelled."
            : "No message arrived. Send /start to your bot and try again.");
    }

    public async Task ConfirmPairingAsync(
        string botToken,
        string chatId,
        long? codeMessageId,
        CancellationToken cancellationToken = default)
    {
        DiscoveredChat? chat = null;
        try
        {
            chat = await DescribeChatAsync(botToken, chatId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The name is only decoration on the confirmation.
        }

        var html = PairingCode.Confirmed(chat?.Title ?? chatId);

        if (codeMessageId is { } messageId)
        {
            var edited = await EditMessageAsync(botToken, chatId, messageId, html, cancellationToken)
                .ConfigureAwait(false);
            if (edited.Success)
            {
                return;
            }

            _log.Warn($"Could not edit the pairing message: {edited.Message}. Sending a new one instead.");
        }

        // Editing fails after 48 hours, and in channels without the right rights; a fresh message
        // still leaves the chat showing that the pairing worked.
        await PostMessageAsync(botToken, chatId, html, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TelegramResult> EditMessageAsync(
        string token,
        string chatId,
        long messageId,
        string html,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["message_id"] = messageId.ToString(),
                ["text"] = html,
                ["parse_mode"] = "HTML",
            });

            using var response = await _http
                .PostAsync($"{ApiRoot}/bot{token}/editMessageText", content, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ReadResponse(body);

            return parsed.Ok ? TelegramResult.Ok("Edited.") : TelegramResult.Fail(parsed.Description);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return TelegramResult.Fail(e.Message);
        }
    }

    public async Task<DiscoveredChat?> DescribeChatAsync(
        string botToken,
        string chatId,
        CancellationToken cancellationToken = default)
    {
        if (!TelegramOptions.LooksLikeToken(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            return null;
        }

        try
        {
            using var response = await _http
                .GetAsync(
                    $"{ApiRoot}/bot{botToken}/getChat?chat_id={Uri.EscapeDataString(chatId.Trim())}",
                    cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return TelegramUpdateReader.ReadChatResult(body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Offline or a removed chat: the caller falls back to showing the raw id.
            return null;
        }
    }

    private async Task<TelegramUpdates> GetUpdatesAsync(
        string token,
        long? offset,
        int holdSeconds,
        CancellationToken cancellationToken)
    {
        var query = $"timeout={holdSeconds}&allowed_updates={Uri.EscapeDataString(TelegramUpdateReader.AllowedUpdatesJson)}";
        if (offset is { } value)
        {
            query += $"&offset={value}";
        }

        try
        {
            var client = holdSeconds > 0 ? _pollHttp : _http;
            using var response = await client
                .GetAsync($"{ApiRoot}/bot{token}/getUpdates?{query}", cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return TelegramUpdateReader.Read(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TelegramUpdates.Failed("Cancelled.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // A long poll that times out client-side is normal; treat it as an empty round.
            return holdSeconds > 0
                ? new TelegramUpdates(true, "ok", [])
                : TelegramUpdates.Failed($"Could not reach Telegram: {e.Message}");
        }
    }

    /// <summary>Turns Telegram's terser refusals into something a user can act on.</summary>
    private static string Explain(string description)
    {
        if (description.Contains("terminated by other getUpdates", StringComparison.OrdinalIgnoreCase))
        {
            return "Another program is already listening with this bot. Close it and try again.";
        }

        if (description.Contains("webhook is active", StringComparison.OrdinalIgnoreCase))
        {
            return "This bot uses a webhook, so it cannot listen here. Remove the webhook or use a new bot.";
        }

        if (description.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "The token was rejected. Copy it again from @BotFather.";
        }

        return description;
    }

    private async Task<TelegramResult> PostMessageAsync(
        string token,
        string chatId,
        string html,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = html,
                ["parse_mode"] = "HTML",
                ["disable_web_page_preview"] = "true",
            });

            using var response = await _http
                .PostAsync($"{ApiRoot}/bot{token}/sendMessage", content, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (ok, description, _, messageId) = ReadResponse(body);

            if (ok)
            {
                return TelegramResult.Ok("Sent.", messageId);
            }

            _log.Warn($"Telegram rejected a message for chat {chatId}: {description}");
            return TelegramResult.Fail(description);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Telegram send to chat {chatId} failed: {e.Message}");
            return TelegramResult.Fail(e.Message);
        }
    }

    private static (bool Ok, string Description, string? Username, long? MessageId) ReadResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();

            var description = root.TryGetProperty("description", out var d)
                ? d.GetString() ?? "unknown error"
                : ok ? "ok" : "unknown error";

            string? username = null;
            long? messageId = null;

            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("username", out var u))
                {
                    username = u.GetString();
                }

                if (result.TryGetProperty("message_id", out var m) && m.TryGetInt64(out var id))
                {
                    messageId = id;
                }
            }

            return (ok, description, username, messageId);
        }
        catch (JsonException)
        {
            return (false, "Telegram returned an unexpected response.", null, null);
        }
    }

    public void Dispose()
    {
        // A handler supplied from outside is the caller's to dispose.
        if (_ownsHandler)
        {
            _http.Dispose();
            _pollHttp.Dispose();
        }
    }
}

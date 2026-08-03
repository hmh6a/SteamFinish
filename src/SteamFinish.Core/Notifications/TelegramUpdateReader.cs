using System.Text.Json;

namespace SteamFinish.Core.Notifications;

/// <summary>A chat the bot has heard from, ready to be added to the notification list.</summary>
public sealed record DiscoveredChat(string ChatId, string Title, string Kind)
{
    /// <summary>"Hussam (private chat)" — what the confirmation prompt shows.</summary>
    public string Describe() => $"{Title} ({Kind})";
}

public sealed record TelegramUpdate(long UpdateId, DiscoveredChat? Chat, TelegramCallback? Callback = null);

public sealed record TelegramUpdates(bool Ok, string Description, IReadOnlyList<TelegramUpdate> Updates)
{
    public static TelegramUpdates Failed(string description) => new(false, description, []);
}

/// <summary>
/// Pulls the chat out of a <c>getUpdates</c> response. Split from the HTTP client so the shapes
/// Telegram sends — direct messages, group messages, channel posts, being added to a group — can be
/// tested without a network.
/// </summary>
public static class TelegramUpdateReader
{
    /// <summary>The update kinds that carry a chat worth pairing with.</summary>
    private static readonly string[] ChatCarriers =
    [
        "message",
        "edited_message",
        "channel_post",
        "edited_channel_post",
        "my_chat_member",
    ];

    /// <summary>The value for <c>allowed_updates</c>; <c>my_chat_member</c> is opt-in.</summary>
    public static string AllowedUpdatesJson =>
        "[" + string.Join(",", ChatCarriers.Select(name => $"\"{name}\"")) + "]";

    public static TelegramUpdates Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
            {
                var description = root.TryGetProperty("description", out var d)
                    ? d.GetString() ?? "unknown error"
                    : "unknown error";
                return TelegramUpdates.Failed(description);
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                return new TelegramUpdates(true, "ok", []);
            }

            var updates = new List<TelegramUpdate>();
            foreach (var element in result.EnumerateArray())
            {
                if (!element.TryGetProperty("update_id", out var idElement)
                    || !idElement.TryGetInt64(out var updateId))
                {
                    continue;
                }

                updates.Add(new TelegramUpdate(updateId, ReadChat(element), ReadCallback(element)));
            }

            return new TelegramUpdates(true, "ok", updates);
        }
        catch (JsonException)
        {
            return TelegramUpdates.Failed("Telegram returned an unexpected response.");
        }
    }

    /// <summary>The value for <c>allowed_updates</c> while waiting for a button press.</summary>
    public const string CallbackUpdatesJson = "[\"callback_query\"]";

    /// <summary>Reads an inline-button press, if this update is one.</summary>
    private static TelegramCallback? ReadCallback(JsonElement update)
    {
        if (!update.TryGetProperty("callback_query", out var query) || query.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Text(query, "id");
        var data = Text(query, "data");
        if (id.Length == 0 || data.Length == 0)
        {
            return null;
        }

        long messageId = 0;
        var chatId = string.Empty;
        if (query.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (message.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var parsed))
            {
                messageId = parsed;
            }

            if (message.TryGetProperty("chat", out var chat)
                && chat.ValueKind == JsonValueKind.Object
                && chat.TryGetProperty("id", out var cid)
                && cid.TryGetInt64(out var chatNumber))
            {
                chatId = chatNumber.ToString();
            }
        }

        var from = string.Empty;
        if (query.TryGetProperty("from", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            from = Text(sender, "first_name");
            if (from.Length == 0)
            {
                from = Text(sender, "username");
            }
        }

        return new TelegramCallback(id, chatId, messageId, data, from);
    }

    /// <summary>Reads the chat out of a <c>getChat</c> response.</summary>
    public static DiscoveredChat? ReadChatResult(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("ok", out var ok)
                || !ok.GetBoolean()
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReadChatObject(result);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DiscoveredChat? ReadChat(JsonElement update)
    {
        foreach (var carrier in ChatCarriers)
        {
            if (update.TryGetProperty(carrier, out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("chat", out var chat)
                && chat.ValueKind == JsonValueKind.Object)
            {
                return ReadChatObject(chat);
            }
        }

        return null;
    }

    private static DiscoveredChat? ReadChatObject(JsonElement chat)
    {
        if (!chat.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
        {
            return null;
        }

        var type = chat.TryGetProperty("type", out var t) ? t.GetString() : null;

        // Groups and channels carry a title; private chats carry a name instead.
        var title = Text(chat, "title");
        if (title is null)
        {
            var first = Text(chat, "first_name");
            var last = Text(chat, "last_name");
            title = string.Join(' ', new[] { first, last }.Where(part => part is not null));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = Text(chat, "username") is { } username ? $"@{username}" : id.ToString();
        }

        return new DiscoveredChat(id.ToString(), title, Describe(type));
    }

    private static string Describe(string? type) => type switch
    {
        "private" => "private chat",
        "group" or "supergroup" => "group",
        "channel" => "channel",
        _ => type ?? "chat",
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

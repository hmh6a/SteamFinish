namespace SteamFinish.Core.Notifications;

public enum MessageLanguage
{
    Arabic,
    English,
}

/// <summary>Telegram bot settings, stored as part of the app settings file.</summary>
public sealed class TelegramOptions
{
    public const int MinProgressStep = 1;
    public const int MaxProgressStep = 50;

    public bool Enabled { get; set; }

    /// <summary>The token from @BotFather, in the form <c>123456789:AA…</c>.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Every chat that should receive the messages; groups and channels work too.</summary>
    public List<string> ChatIds { get; set; } = [];

    /// <summary>
    /// Cached "name (kind)" for each chat id, so the list stays readable offline and before the
    /// lookup finishes. Purely cosmetic — <see cref="ChatIds"/> remains the source of truth.
    /// </summary>
    public Dictionary<string, string> ChatLabels { get; set; } = [];

    public bool NotifyOnStart { get; set; } = true;

    public bool NotifyOnProgress { get; set; } = true;

    /// <summary>Send a progress message every time this many percent are added.</summary>
    public int ProgressStepPercent { get; set; } = 5;

    public bool NotifyOnFinish { get; set; } = true;

    public bool NotifyOnCancel { get; set; } = true;

    /// <summary>
    /// Put "run it now" and "don't" buttons on the finish message, so the countdown can be settled
    /// from the phone instead of walking to the PC.
    /// </summary>
    public bool RemoteButtons { get; set; } = true;

    /// <summary>
    /// Answer /pause, /resume and /status from the paired chats. Only chats in <see cref="ChatIds"/>
    /// are obeyed, so holding the bot token is not on its own enough to stop someone's download.
    /// </summary>
    public bool RemoteCommands { get; set; } = true;

    public MessageLanguage Language { get; set; } = MessageLanguage.Arabic;

    /// <summary>True when there is enough configuration to attempt a send.</summary>
    public bool IsUsable =>
        Enabled && LooksLikeToken(BotToken) && ChatIds.Any(id => !string.IsNullOrWhiteSpace(id));

    public static bool LooksLikeToken(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Contains(':', StringComparison.Ordinal)
        && token.Length >= 20;

    /// <summary>
    /// A detached copy. Sends happen on background threads while the settings object stays bound to
    /// the UI, so the values are captured up front rather than read mid-flight.
    /// </summary>
    public TelegramOptions Clone() => new()
    {
        Enabled = Enabled,
        BotToken = BotToken,
        ChatIds = [.. ChatIds],
        ChatLabels = new Dictionary<string, string>(ChatLabels, StringComparer.OrdinalIgnoreCase),
        NotifyOnStart = NotifyOnStart,
        NotifyOnProgress = NotifyOnProgress,
        ProgressStepPercent = ProgressStepPercent,
        NotifyOnFinish = NotifyOnFinish,
        NotifyOnCancel = NotifyOnCancel,
        RemoteButtons = RemoteButtons,
        RemoteCommands = RemoteCommands,
        Language = Language,
    };

    public TelegramOptions Normalize()
    {
        BotToken = BotToken.Trim();
        ProgressStepPercent = Math.Clamp(ProgressStepPercent, MinProgressStep, MaxProgressStep);
        ChatIds = ChatIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Labels for chats that are no longer configured are just clutter.
        ChatLabels = ChatLabels
            .Where(pair => ChatIds.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (!Enum.IsDefined(Language))
        {
            Language = MessageLanguage.Arabic;
        }

        return this;
    }
}

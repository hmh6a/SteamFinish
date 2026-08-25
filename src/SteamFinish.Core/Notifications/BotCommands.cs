using System.Text.Json;
using SteamFinish.Core.Control;

namespace SteamFinish.Core.Notifications;

/// <summary>Something the user typed into the chat.</summary>
public enum BotCommand
{
    /// <summary>Stop the download where it is.</summary>
    Pause,

    /// <summary>Start it moving again.</summary>
    Resume,

    /// <summary>Report what is downloading right now.</summary>
    Status,

    /// <summary>List what the bot understands.</summary>
    Help,
}

/// <summary>
/// Reads the slash commands the bot answers. Telegram appends <c>@thebot</c> to commands sent in a
/// group, so the mention is trimmed before matching.
/// </summary>
public static class BotCommands
{
    public static BotCommand? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Only the first word matters; anything after it is an argument we have no use for.
        var word = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (word.Length < 2 || word[0] != '/')
        {
            return null;
        }

        var at = word.IndexOf('@', StringComparison.Ordinal);
        var name = (at > 0 ? word[1..at] : word[1..]).ToLowerInvariant();

        return name switch
        {
            "pause" or "stop" => BotCommand.Pause,
            "resume" or "continue" => BotCommand.Resume,
            "status" or "state" => BotCommand.Status,

            // /start is what Telegram sends when someone first opens the bot, so it lands on the
            // list of what the bot can do rather than on nothing at all.
            "help" or "start" or "commands" => BotCommand.Help,
            _ => null,
        };
    }
}

/// <summary>
/// The payloads behind the pause and resume buttons.
///
/// Unlike the countdown buttons these carry no one-time token. A stale countdown button could power
/// a PC off hours after the fact, so those are fenced off; pausing is reversible, costs nothing and
/// is exactly what someone pressing an old button meant to do — so the buttons keep working.
/// </summary>
public static class DownloadButtons
{
    private const string Prefix = "sfd";

    public static string DataFor(DownloadCommand command) =>
        $"{Prefix}:{(command == DownloadCommand.Pause ? "pause" : "resume")}";

    public static DownloadCommand? Parse(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        var parts = data.Split(':');
        if (parts.Length != 2 || parts[0] != Prefix)
        {
            return null;
        }

        return parts[1] switch
        {
            "pause" => DownloadCommand.Pause,
            "resume" => DownloadCommand.Resume,
            _ => null,
        };
    }
}

/// <summary>Builds the <c>reply_markup</c> JSON for a single row of inline buttons.</summary>
public static class TelegramKeyboard
{
    public static string Row(params (string Text, string Data)[] buttons) =>
        JsonSerializer.Serialize(new
        {
            inline_keyboard = new[]
            {
                buttons.Select(button => new { text = button.Text, callback_data = button.Data }).ToArray(),
            },
        });

    /// <summary>The pause and resume pair that rides along with every command reply.</summary>
    public static string PauseResume(MessageLanguage language) => Row(
        (NotificationMessages.ButtonPause(language), DownloadButtons.DataFor(DownloadCommand.Pause)),
        (NotificationMessages.ButtonResume(language), DownloadButtons.DataFor(DownloadCommand.Resume)));
}

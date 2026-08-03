using System.Security.Cryptography;

namespace SteamFinish.Core.Notifications;

/// <summary>Which button was pressed on the countdown message.</summary>
public enum RemoteDecision
{
    /// <summary>Run the power action straight away, without waiting out the countdown.</summary>
    Now,

    /// <summary>Call the whole thing off and leave the PC running.</summary>
    Skip,
}

/// <summary>An inline-button press delivered through <c>getUpdates</c>.</summary>
public sealed record TelegramCallback(string Id, string ChatId, long MessageId, string Data, string From);

/// <summary>One chat that received the countdown message, and the message to edit afterwards.</summary>
public sealed record PromptTarget(string ChatId, long MessageId);

/// <summary>The button half of the Telegram client, kept separate so tests can stand in for it.</summary>
public interface ITelegramRemoteControl
{
    Task<IReadOnlyList<PromptTarget>> SendWithButtonsAsync(
        TelegramOptions options,
        string html,
        string nowLabel,
        string skipLabel,
        string token,
        CancellationToken cancellationToken = default);

    Task<(TelegramCallback Callback, RemoteDecision Decision)?> WaitForDecisionAsync(
        string botToken,
        string token,
        CancellationToken cancellationToken = default);

    Task AnswerCallbackAsync(
        string botToken,
        string callbackId,
        string text,
        CancellationToken cancellationToken = default);

    Task EditAllAsync(
        string botToken,
        IReadOnlyList<PromptTarget> targets,
        string html,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The callback payloads carried by the two countdown buttons. Each countdown gets a fresh token so
/// a button from an earlier run — still sitting in the chat history — cannot power the PC off.
/// </summary>
public static class RemoteControl
{
    private const string Prefix = "sf";

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

    public static string DataFor(RemoteDecision decision, string token) =>
        $"{Prefix}:{(decision == RemoteDecision.Now ? "now" : "skip")}:{token}";

    /// <summary>
    /// Reads a callback payload, rejecting anything that is not one of this countdown's buttons.
    /// </summary>
    public static RemoteDecision? Parse(string? data, string expectedToken)
    {
        if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(expectedToken))
        {
            return null;
        }

        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] != Prefix)
        {
            return null;
        }

        if (!string.Equals(parts[2], expectedToken, StringComparison.Ordinal))
        {
            return null;
        }

        return parts[1] switch
        {
            "now" => RemoteDecision.Now,
            "skip" => RemoteDecision.Skip,
            _ => null,
        };
    }
}

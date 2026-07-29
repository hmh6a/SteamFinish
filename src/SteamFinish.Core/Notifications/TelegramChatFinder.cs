using System.Security.Cryptography;

namespace SteamFinish.Core.Notifications;

/// <summary>
/// The result of listening for a message. On success the chat has been found <em>and</em> a code has
/// been delivered to it, so the user can confirm the app is talking to the right place.
/// </summary>
public sealed record ChatSearchResult(
    bool Success,
    string Message,
    DiscoveredChat? Chat = null,
    string? Code = null,
    long? CodeMessageId = null)
{
    public static ChatSearchResult Fail(string message) => new(false, message);
}

public interface ITelegramChatFinder
{
    /// <summary>
    /// Waits for someone to message the bot, then sends a one-time code to whichever chat that was.
    /// </summary>
    Task<ChatSearchResult> FindChatAsync(
        string botToken,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up what a stored chat id actually is, so the list can show "Gaming Nights (group)"
    /// rather than a bare number. Returns <c>null</c> when it cannot be resolved.
    /// </summary>
    Task<DiscoveredChat?> DescribeChatAsync(
        string botToken,
        string chatId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the code message with a confirmation once the user has accepted the pairing, so the
    /// chat is left showing that it worked rather than a stale code.
    /// </summary>
    Task ConfirmPairingAsync(
        string botToken,
        string chatId,
        long? codeMessageId,
        CancellationToken cancellationToken = default);
}

public static class PairingCode
{
    /// <summary>A six-digit code, short enough to compare at a glance.</summary>
    public static string Generate() =>
        RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();

    /// <summary>
    /// Bilingual on purpose: this is sent before the language setting has necessarily been chosen,
    /// and it has to be readable by whoever is holding the phone.
    /// </summary>
    public static string Message(string code) =>
        $"""
         <b>SteamFinish</b>

         رمز التحقق · Verification code
         <code>{code}</code>

         إذا كان هذا الرمز يطابق ما يظهر في البرنامج، اضغط "إضافة".
         If this matches the code shown in SteamFinish, press Add.
         """;

    /// <summary>Replaces the code once the pairing is accepted, so the chat ends on a clear result.</summary>
    public static string Confirmed(string chatName) =>
        $"""
         <b>SteamFinish</b>

         ✅ <b>تم الربط بنجاح</b>
         هذه المحادثة (<b>{Escape(chatName)}</b>) ستستلم إشعارات التنزيل الآن:
         • عند بدء تنزيل جديد
         • مع تقدّم النسبة
         • عند اكتمال كل شيء، قبل إطفاء الحاسبة

         ✅ <b>Connected</b>
         This chat will now receive download updates, including a message before the PC powers off.
         """;

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}

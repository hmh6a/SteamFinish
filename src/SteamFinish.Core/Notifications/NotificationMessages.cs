using System.Globalization;
using System.Text;
using SteamFinish.Core.Formatting;
using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Power;
using SteamFinish.Core.Steam;

namespace SteamFinish.Core.Notifications;

/// <summary>
/// Builds the Telegram messages. Telegram's HTML parse mode is used, so every value that comes from
/// Steam — game names above all — has to be escaped.
/// </summary>
public static class NotificationMessages
{
    private const string Header = "🎮 <b>SteamFinish</b>";

    /// <summary>Escapes the three characters Telegram's HTML mode treats as markup.</summary>
    public static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    public static string DownloadStarted(MessageLanguage language, AppActivity app, int queueCount)
    {
        var name = Escape(app.Name);
        var size = Humanize.Bytes(app.BytesToDownload);

        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();

        if (language == MessageLanguage.Arabic)
        {
            builder.AppendLine($"⬇️ بدأ تنزيل <b>{name}</b>");
            builder.AppendLine($"💾 الحجم: {size}");
            if (queueCount > 0)
            {
                builder.AppendLine($"📥 في الطابور: {queueCount}");
            }
        }
        else
        {
            builder.AppendLine($"⬇️ Started downloading <b>{name}</b>");
            builder.AppendLine($"💾 Size: {size}");
            if (queueCount > 0)
            {
                builder.AppendLine($"📥 In queue: {queueCount}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string Progress(
        MessageLanguage language,
        AppActivity app,
        int reachedPercent,
        double networkBytesPerSecond,
        TimeSpan? eta,
        int queueCount)
    {
        var name = Escape(app.Name);
        var fraction = reachedPercent / 100d;
        var bar = Humanize.Bar(fraction);
        var downloaded = $"{Humanize.Bytes(app.BytesDownloaded)} / {Humanize.Bytes(app.BytesToDownload)}";
        var rate = Humanize.Rate(networkBytesPerSecond);

        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();

        if (language == MessageLanguage.Arabic)
        {
            builder.AppendLine($"⬇️ <b>{name}</b>");
            builder.AppendLine($"<code>{bar}</code> {reachedPercent}%");
            builder.AppendLine($"📦 {downloaded}");
            builder.AppendLine(eta is { } left
                ? $"🚀 {rate} · ⏳ يتبقى {Humanize.Clock(left)}"
                : $"🚀 {rate}");
            if (queueCount > 0)
            {
                builder.AppendLine($"📥 في الطابور: {queueCount}");
            }
        }
        else
        {
            builder.AppendLine($"⬇️ <b>{name}</b>");
            builder.AppendLine($"<code>{bar}</code> {reachedPercent}%");
            builder.AppendLine($"📦 {downloaded}");
            builder.AppendLine(eta is { } left
                ? $"🚀 {rate} · ⏳ {Humanize.Clock(left)} left"
                : $"🚀 {rate}");
            if (queueCount > 0)
            {
                builder.AppendLine($"📥 In queue: {queueCount}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string Finished(
        MessageLanguage language,
        DownloadSummary summary,
        PowerAction action,
        int countdownSeconds)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();

        var arabic = language == MessageLanguage.Arabic;
        builder.AppendLine(arabic
            ? "✅ <b>اكتملت جميع التنزيلات</b>"
            : "✅ <b>All downloads are finished</b>");
        builder.AppendLine();

        builder.AppendLine(arabic
            ? $"📦 الألعاب ({summary.Games.Count}):"
            : $"📦 Games ({summary.Games.Count}):");

        foreach (var game in summary.Games.Take(10))
        {
            builder.AppendLine($"• {Escape(game.Name)} — {Humanize.Bytes(game.BytesToDownload)}");
        }

        if (summary.Games.Count > 10)
        {
            var rest = summary.Games.Count - 10;
            builder.AppendLine(arabic ? $"• و{rest} غيرها" : $"• and {rest} more");
        }

        builder.AppendLine();

        if (arabic)
        {
            builder.AppendLine($"💾 الحجم الكلي: {Humanize.Bytes(summary.TotalDownloadBytes)}");
            builder.AppendLine($"💽 على القرص: {Humanize.Bytes(summary.TotalInstallBytes)}");
            builder.AppendLine($"⏱ استغرق: {ArabicDuration(summary.Duration)}");
            builder.AppendLine($"🚀 متوسط السرعة: {Humanize.Rate(summary.AverageBytesPerSecond)}");
            builder.AppendLine();
            builder.AppendLine($"🔌 سيتم <b>{ArabicAction(action)}</b> خلال {countdownSeconds} ثانية");
            builder.AppendLine("<i>افتح SteamFinish للإلغاء</i>");
        }
        else
        {
            builder.AppendLine($"💾 Downloaded: {Humanize.Bytes(summary.TotalDownloadBytes)}");
            builder.AppendLine($"💽 On disk: {Humanize.Bytes(summary.TotalInstallBytes)}");
            builder.AppendLine($"⏱ Took: {EnglishDuration(summary.Duration)}");
            builder.AppendLine($"🚀 Average speed: {Humanize.Rate(summary.AverageBytesPerSecond)}");
            builder.AppendLine();
            builder.AppendLine($"🔌 <b>{action}</b> in {countdownSeconds} seconds");
            builder.AppendLine("<i>Open SteamFinish to cancel</i>");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Used when the queue empties but nothing was recorded, so there is no summary to show.</summary>
    public static string FinishedWithoutDetails(
        MessageLanguage language,
        PowerAction action,
        int countdownSeconds)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();

        if (language == MessageLanguage.Arabic)
        {
            builder.AppendLine("✅ <b>اكتملت جميع التنزيلات</b>");
            builder.AppendLine();
            builder.AppendLine($"🔌 سيتم <b>{ArabicAction(action)}</b> خلال {countdownSeconds} ثانية");
            builder.AppendLine("<i>افتح SteamFinish للإلغاء</i>");
        }
        else
        {
            builder.AppendLine("✅ <b>All downloads are finished</b>");
            builder.AppendLine();
            builder.AppendLine($"🔌 <b>{action}</b> in {countdownSeconds} seconds");
            builder.AppendLine("<i>Open SteamFinish to cancel</i>");
        }

        return builder.ToString().TrimEnd();
    }

    public static string Cancelled(MessageLanguage language, PowerAction action, CountdownCancelReason reason)
    {
        var arabic = language == MessageLanguage.Arabic;
        var why = reason switch
        {
            CountdownCancelReason.NewActivity => arabic ? "بدأ تنزيل جديد" : "a new download started",
            CountdownCancelReason.StateUnavailable => arabic ? "تعذّرت قراءة حالة Steam" : "Steam's state could not be read",
            _ => arabic ? "بطلب منك" : "you cancelled it",
        };

        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();
        builder.AppendLine(arabic
            ? $"🛑 تم إلغاء <b>{ArabicAction(action)}</b>"
            : $"🛑 <b>{action}</b> cancelled");
        builder.AppendLine(arabic ? $"السبب: {why}" : $"Reason: {why}");

        return builder.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- Remote buttons

    public static string ButtonShutdownNow(MessageLanguage language, PowerAction action) =>
        language == MessageLanguage.Arabic
            ? $"⚡ {ArabicAction(action)} الآن"
            : $"⚡ {action} now";

    public static string ButtonSkip(MessageLanguage language) =>
        language == MessageLanguage.Arabic ? "🛑 لا تطفئ" : "🛑 Don't";

    /// <summary>Shown as a toast on the phone the moment a button is pressed.</summary>
    public static string Toast(MessageLanguage language, RemoteDecision decision) =>
        (language, decision) switch
        {
            (MessageLanguage.Arabic, RemoteDecision.Now) => "جارٍ التنفيذ الآن…",
            (MessageLanguage.Arabic, _) => "تم الإلغاء",
            (_, RemoteDecision.Now) => "Running it now…",
            _ => "Cancelled",
        };

    /// <summary>Replaces the countdown message once someone presses a button.</summary>
    public static string DecisionTaken(
        MessageLanguage language,
        RemoteDecision decision,
        PowerAction action,
        string who)
    {
        var by = string.IsNullOrWhiteSpace(who) ? string.Empty : $" · {Escape(who)}";
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();

        if (language == MessageLanguage.Arabic)
        {
            builder.AppendLine(decision == RemoteDecision.Now
                ? $"⚡ <b>تم {ArabicAction(action)} الحاسبة الآن</b>{by}"
                : $"🛑 <b>أُلغي {ArabicAction(action)}</b>{by}");
            builder.AppendLine();
            builder.AppendLine(decision == RemoteDecision.Now
                ? "نُفِّذ الأمر فوراً بناءً على طلبك، دون انتظار العد التنازلي."
                : "الحاسبة ستبقى تعمل. المراقبة مستمرة، وسيبدأ عد جديد عند انتهاء تنزيل جديد.");
        }
        else
        {
            builder.AppendLine(decision == RemoteDecision.Now
                ? $"⚡ <b>{action} started now</b>{by}"
                : $"🛑 <b>{action} cancelled</b>{by}");
            builder.AppendLine();
            builder.AppendLine(decision == RemoteDecision.Now
                ? "Carried out straight away at your request, without waiting for the countdown."
                : "The PC stays on. Monitoring continues, and a new countdown starts after the next download.");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Replaces the countdown message when the outcome was decided at the PC instead.</summary>
    public static string DecidedAtThePc(MessageLanguage language, PowerAction action, bool executed)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();

        if (language == MessageLanguage.Arabic)
        {
            builder.AppendLine(executed
                ? $"⚡ <b>تم {ArabicAction(action)} الحاسبة</b>"
                : $"🛑 <b>أُلغي {ArabicAction(action)} من البرنامج</b>");
        }
        else
        {
            builder.AppendLine(executed
                ? $"⚡ <b>{action} started</b>"
                : $"🛑 <b>{action} cancelled from the app</b>");
        }

        return builder.ToString().TrimEnd();
    }

    public static string Test(MessageLanguage language)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        builder.AppendLine();

        if (language == MessageLanguage.Arabic)
        {
            builder.AppendLine("✅ <b>الاتصال يعمل</b>");
            builder.AppendLine("سيصلك إشعار عند تقدّم التحميل وعند اكتماله قبل إطفاء الحاسبة.");
        }
        else
        {
            builder.AppendLine("✅ <b>Connection works</b>");
            builder.AppendLine("You will get progress updates and a summary before the PC powers off.");
        }

        return builder.ToString().TrimEnd();
    }

    private static string ArabicAction(PowerAction action) => action switch
    {
        PowerAction.Shutdown => "إطفاء الحاسبة",
        PowerAction.Sleep => "تحويل الحاسبة إلى وضع السكون",
        PowerAction.Hibernate => "تحويل الحاسبة إلى وضع الإسبات",
        PowerAction.Restart => "إعادة تشغيل الحاسبة",
        _ => action.ToString(),
    };

    private static string ArabicDuration(TimeSpan span)
    {
        var parts = new List<string>(2);
        var hours = (int)span.TotalHours;
        if (hours > 0)
        {
            parts.Add(ArabicCount(hours, "ساعة واحدة", "ساعتان", "ساعات", "ساعة"));
        }

        if (span.Minutes > 0)
        {
            parts.Add(ArabicCount(span.Minutes, "دقيقة واحدة", "دقيقتان", "دقائق", "دقيقة"));
        }

        if (parts.Count == 0)
        {
            parts.Add(ArabicCount(Math.Max(1, span.Seconds), "ثانية واحدة", "ثانيتان", "ثوانٍ", "ثانية"));
        }

        return string.Join(" و", parts);
    }

    /// <summary>Arabic counts take four forms depending on the number, not two.</summary>
    private static string ArabicCount(int count, string one, string two, string few, string many) => count switch
    {
        1 => one,
        2 => two,
        >= 3 and <= 10 => $"{count} {few}",
        _ => $"{count} {many}",
    };

    private static string EnglishDuration(TimeSpan span)
    {
        var parts = new List<string>(2);
        var hours = (int)span.TotalHours;
        if (hours > 0)
        {
            parts.Add($"{hours} h");
        }

        if (span.Minutes > 0)
        {
            parts.Add($"{span.Minutes} min");
        }

        if (parts.Count == 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, span.Seconds)} s"));
        }

        return string.Join(' ', parts);
    }
}

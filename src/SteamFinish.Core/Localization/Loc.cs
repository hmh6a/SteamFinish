using System.ComponentModel;
using System.Globalization;

namespace SteamFinish.Core.Localization;

public enum UiLanguage
{
    English,
    Arabic,
}

/// <summary>
/// The interface strings in both languages. Bindings target the indexer, so changing the language
/// refreshes the whole window in place — no restart, and no reload of the view models.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly Loc Instance = new();

    private static readonly Dictionary<string, (string En, string Ar)> Table = new(StringComparer.Ordinal)
    {
        // ---------------------------------------------------------------- Shell
        ["App.Tagline"] = ("Download. Finish. Power Off.", "حمّل. أنهِ. أطفئ."),
        ["Tab.Monitor"] = ("Monitor", "المراقبة"),
        ["Tab.Settings"] = ("Settings", "الإعدادات"),

        // ---------------------------------------------------------------- Monitor tab
        ["Status.Title"] = ("Steam status", "حالة Steam"),
        ["Button.Refresh"] = ("Refresh", "تحديث"),
        ["Bar.Downloading"] = ("Downloading data", "تنزيل البيانات"),
        ["Bar.Installing"] = ("Installing files", "تثبيت الملفات"),
        ["Metric.Network"] = ("NETWORK", "الشبكة"),
        ["Metric.Peak"] = ("PEAK", "الذروة"),
        ["Metric.Disk"] = ("DISK USAGE", "استهلاك القرص"),
        ["Metric.TimeLeft"] = ("TIME LEFT", "الوقت المتبقي"),
        ["Metric.FinishesAt"] = ("FINISHES AT", "ينتهي عند"),
        ["Metric.LeftToDownload"] = ("LEFT TO DOWNLOAD", "المتبقي للتنزيل"),
        ["Queue.Title"] = ("Up next", "التالي"),
        ["Action.Title"] = ("Action when downloads finish", "الإجراء عند انتهاء التنزيلات"),
        ["Action.Shutdown"] = ("Shutdown", "إطفاء"),
        ["Action.Sleep"] = ("Sleep", "سكون"),
        ["Action.Hibernate"] = ("Hibernate", "إسبات"),
        ["Action.Restart"] = ("Restart", "إعادة تشغيل"),
        ["Countdown.Title"] = ("Countdown", "العد التنازلي"),
        ["Countdown.Seconds"] = ("seconds", "ثانية"),
        ["Countdown.ActionIn"] = ("{0} in", "{0} خلال"),
        ["Button.Enable"] = ("Enable Monitoring", "تفعيل المراقبة"),
        ["Button.Disable"] = ("Disable Monitoring", "إيقاف المراقبة"),
        ["Button.CancelAction"] = ("Cancel {0}", "إلغاء {0}"),

        // ---------------------------------------------------------------- Phase line
        ["Phase.Off"] = ("Monitoring is off", "المراقبة متوقفة"),
        ["Phase.Waiting"] = ("Waiting for a download to start", "بانتظار بدء تنزيل"),
        ["Phase.Busy"] = ("Watching — Steam is still working", "مراقبة — Steam ما زال يعمل"),
        ["Phase.Confirming"] = ("Looks finished — confirming for {0}s", "يبدو أنه انتهى — تأكيد خلال {0} ثانية"),
        ["Phase.Countdown"] = ("{0} in {1}s", "{0} خلال {1} ثانية"),
        ["Phase.Executing"] = ("Running {0}…", "جارٍ تنفيذ {0}…"),
        ["Phase.Blocked"] = ("Steam's state cannot be read", "تعذّرت قراءة حالة Steam"),

        // ---------------------------------------------------------------- Steam status text
        ["Status.Unavailable"] = ("Steam state unavailable", "حالة Steam غير متاحة"),
        ["Status.NoLibraries"] = ("No Steam library folder could be read.", "تعذّر قراءة أي مجلد مكتبة لـ Steam."),
        // One placeholder, not two: the name and its percentage are fenced together as a single
        // left-to-right run, otherwise the brackets drift to the wrong end in Arabic.
        ["Status.Paused"] = ("Paused: {0}", "متوقف مؤقتاً: {0}"),
        ["Status.PausedDetail"] = (
            "Steam is not moving any bytes. A paused download does not count as finished.",
            "Steam لا ينقل أي بيانات. التنزيل المتوقف مؤقتاً لا يعتبر مكتملاً."),
        ["Status.Downloading"] = ("Downloading {0}", "جارٍ تنزيل {0}"),
        ["Status.Installing"] = ("Installing {0}", "جارٍ تثبيت {0}"),
        ["Status.Validating"] = ("Validating {0}", "جارٍ التحقق من {0}"),
        ["Status.Queued"] = ("Queued: {0}", "بالانتظار: {0}"),
        ["Status.WritingFiles"] = ("Steam is still writing files", "Steam ما زال يكتب الملفات"),
        ["Status.WritingDetail"] = ("The download folder is not empty yet.", "مجلد التنزيل لم يفرغ بعد."),
        ["Status.NoDownloads"] = ("No downloads in progress", "لا توجد تنزيلات جارية"),
        ["Status.SteamIdle"] = ("Steam is idle · {0}", "Steam خامل · {0}"),
        ["Status.SteamNotRunning"] = ("Steam is not running · {0}", "Steam غير مشغّل · {0}"),
        ["Status.LibrariesWatched"] = ("{0} libraries watched", "{0} مكتبات مراقَبة"),
        ["Status.OneLibraryWatched"] = ("1 library watched", "مكتبة واحدة مراقَبة"),
        ["Status.OfTotal"] = ("{0} of {1}", "{0} من {1}"),
        ["Status.MoreInQueue"] = ("{0} more in queue", "و{0} بالانتظار"),
        ["Status.FinishingUp"] = ("Finishing up…", "على وشك الانتهاء…"),
        ["Status.NotChecked"] = ("Steam has not been checked yet", "لم يتم فحص Steam بعد"),
        ["Status.EnableHint"] = ("Enable monitoring to start watching downloads.", "فعّل المراقبة لبدء متابعة التنزيلات."),
        ["Status.ActionFailed"] = ("The action could not be started", "تعذّر تنفيذ الإجراء"),

        // ---------------------------------------------------------------- Queue rows
        ["Queue.StateDownloading"] = ("Downloading", "جارٍ التنزيل"),
        ["Queue.StateInstalling"] = ("Installing", "جارٍ التثبيت"),
        ["Queue.StateValidating"] = ("Validating", "جارٍ التحقق"),
        ["Queue.StatePaused"] = ("Paused", "متوقف مؤقتاً"),
        ["Queue.StateQueued"] = ("Queued", "بالانتظار"),
        ["Queue.StateWaiting"] = ("Waiting in queue", "بالانتظار"),
        ["Queue.ItemsLeft"] = ("{0} items · {1} left", "{0} عناصر · {1} متبقٍ"),
        ["Queue.OneItemLeft"] = ("1 item · {0} left", "عنصر واحد · {0} متبقٍ"),

        // ---------------------------------------------------------------- Settings: timing
        ["Settings.Timing"] = ("Timing", "التوقيت"),
        ["Settings.CountdownSeconds"] = ("Countdown seconds", "ثواني العد التنازلي"),
        ["Settings.CountdownHint"] = (
            "How long you get to cancel before the action runs.",
            "المدة المتاحة للإلغاء قبل تنفيذ الإجراء."),
        ["Settings.QuietSeconds"] = ("Quiet period seconds", "ثواني فترة الهدوء"),
        ["Settings.QuietHint"] = (
            "Steam must stay idle this long before the countdown starts.",
            "يجب أن يبقى Steam خاملاً هذه المدة قبل بدء العد التنازلي."),

        // ---------------------------------------------------------------- Settings: behaviour
        ["Settings.Behaviour"] = ("Behaviour", "السلوك"),
        ["Settings.StartWithWindows"] = ("Start with Windows", "التشغيل مع ويندوز"),
        ["Settings.StartMinimized"] = ("Start minimized to the tray", "البدء مصغّراً في شريط المهام"),
        ["Settings.CloseToTray"] = ("Closing the window hides it to the tray", "إغلاق النافذة يخفيها في شريط المهام"),
        ["Settings.LiveStatus"] = ("Keep the status live while the window is open", "إبقاء الحالة محدّثة أثناء فتح النافذة"),
        ["Settings.TrayNotifications"] = ("Tray notifications", "إشعارات شريط المهام"),
        ["Settings.SoundNotification"] = ("Sound notification", "تنبيه صوتي"),
        ["Settings.EnableLogging"] = ("Write a log file", "كتابة ملف سجل"),

        // ---------------------------------------------------------------- Settings: safety
        ["Settings.Safety"] = ("Safety", "الأمان"),
        ["Settings.RequireDownload"] = ("Only act after a download has been seen", "لا تنفّذ إلا بعد رصد تنزيل"),
        ["Settings.RequireDownloadHint"] = (
            "Keeps an idle PC from powering off right after you enable monitoring.",
            "يمنع إطفاء حاسبة خاملة مباشرة بعد تفعيل المراقبة."),
        ["Settings.IgnorePaused"] = ("Treat paused downloads as finished", "اعتبار التنزيلات المتوقفة مؤقتاً منتهية"),
        ["Settings.ForceClose"] = ("Force apps to close on shutdown or restart", "إجبار البرامج على الإغلاق عند الإطفاء أو إعادة التشغيل"),
        ["Settings.ForceCloseHint"] = (
            "Unsaved work in other apps is lost when this is on.",
            "العمل غير المحفوظ في البرامج الأخرى سيُفقد عند تفعيل هذا."),

        // ---------------------------------------------------------------- Settings: launchers
        ["Settings.Launchers"] = ("Launchers to watch", "المنصّات المراقَبة"),
        ["Settings.WatchSteam"] = ("Steam", "Steam"),
        ["Settings.WatchXbox"] = ("Xbox app / Microsoft Store", "تطبيق Xbox / متجر مايكروسوفت"),
        ["Settings.LaunchersHint"] = (
            "Downloads from every ticked launcher have to finish before the action runs.",
            "يجب أن تنتهي تنزيلات كل منصّة مفعّلة قبل تنفيذ الإجراء."),
        ["Platform.Steam"] = ("Steam", "Steam"),
        ["Platform.Xbox"] = ("Xbox", "Xbox"),
        ["Status.XboxUnavailable"] = (
            "The Xbox app was not found.",
            "لم يُعثر على تطبيق Xbox."),

        // ---------------------------------------------------------------- Settings: libraries
        ["Settings.Libraries"] = ("Steam libraries", "مكتبات Steam"),
        ["Settings.AutoDetect"] = ("Detect Steam libraries automatically", "اكتشاف مكتبات Steam تلقائياً"),
        ["Settings.LibrariesWatched"] = ("Libraries being watched", "المكتبات المراقَبة"),
        ["Settings.ExtraLibraries"] = ("Extra libraries added by hand", "مكتبات مضافة يدوياً"),
        ["Settings.AddLibrary"] = ("Add…", "إضافة…"),
        ["Common.Remove"] = ("Remove", "حذف"),
        ["Settings.OpenDataFolder"] = ("Open data folder", "فتح مجلد البيانات"),
        ["Settings.SavedTo"] = (
            "Settings are saved automatically to %AppData%\\SteamFinish.",
            "تُحفظ الإعدادات تلقائياً في ‎%AppData%\\SteamFinish."),

        // ---------------------------------------------------------------- Settings: language
        ["Settings.Language"] = ("Language", "اللغة"),
        ["Settings.LanguageHint"] = (
            "Changes the whole app straight away.",
            "يغيّر واجهة البرنامج بالكامل فوراً."),
        ["Language.English"] = ("English", "English"),
        ["Language.Arabic"] = ("العربية", "العربية"),

        // ---------------------------------------------------------------- Appearance
        ["Settings.Appearance"] = ("Appearance", "المظهر"),
        ["Theme.System"] = ("Match Windows", "حسب ويندوز"),
        ["Theme.Light"] = ("Light", "فاتح"),
        ["Theme.Dark"] = ("Dark", "داكن"),

        // ---------------------------------------------------------------- Telegram
        ["Telegram.Title"] = ("Telegram notifications", "إشعارات تيليجرام"),
        ["Telegram.Enable"] = ("Send Telegram messages", "إرسال رسائل تيليجرام"),
        ["Telegram.BotToken"] = ("Bot token", "توكن البوت"),
        // The Latin runs are fenced with LEFT-TO-RIGHT MARKs so bidi does not move the @ and / around.
        ["Telegram.CreateBot"] = ("Create a bot (@BotFather)", "إنشاء بوت عبر ‎@BotFather‎"),
        ["Telegram.ShowToken"] = ("Show the token", "إظهار التوكن"),
        ["Telegram.TokenHint"] = (
            "Open @BotFather, send /newbot, follow the prompts, then paste the token it gives you here. Anyone who has the token controls the bot — keep it hidden.",
            "افتح ‎@BotFather‎ وأرسل ‎/newbot‎ واتبع الخطوات، ثم الصق التوكن هنا. من يملك التوكن يتحكم بالبوت — أبقِه مخفياً."),
        ["Telegram.ChatIds"] = ("Chat IDs", "معرّفات المحادثات"),
        ["Telegram.Add"] = ("Add", "إضافة"),
        ["Telegram.Remove"] = ("Remove", "حذف"),
        ["Telegram.FindTitle"] = ("Find my chat ID", "اكتشاف معرّف المحادثة"),
        ["Telegram.FindHint"] = (
            "Press the button, then send /start to your bot on Telegram. For a group or channel, add the bot there first and send /start inside it.",
            "اضغط الزر ثم أرسل ‎/start‎ إلى البوت في تيليجرام. للكروب أو القناة، أضف البوت هناك أولاً ثم أرسل ‎/start‎ داخلها."),
        ["Telegram.FindButton"] = ("Find my chat", "ابحث عن محادثتي"),
        ["Telegram.Listening"] = ("Listening…", "جارٍ الاستماع…"),
        ["Telegram.Stop"] = ("Stop", "إيقاف"),
        ["Telegram.CodeHint"] = (
            "SteamFinish sent this code to that chat. Add it only if the same code arrived in Telegram.",
            "أرسل SteamFinish هذا الرمز إلى تلك المحادثة. أضفها فقط إذا وصلك الرمز نفسه في تيليجرام."),
        ["Telegram.CodesMatch"] = ("Codes match — add it", "الرمزان متطابقان — أضفها"),
        ["Telegram.Cancel"] = ("Cancel", "إلغاء"),
        ["Telegram.ManualHint"] = (
            "You can also add an ID by hand — @userinfobot on Telegram reports yours.",
            "يمكنك أيضاً إضافة المعرّف يدوياً — البوت ‎@userinfobot‎ يخبرك بمعرّفك."),
        ["Telegram.SendWhen"] = ("Send a message when…", "أرسل رسالة عند…"),
        ["Telegram.OnStart"] = ("A download starts", "بدء تنزيل"),
        ["Telegram.OnFinish"] = ("Everything finishes, before the action runs", "انتهاء كل شيء، قبل تنفيذ الإجراء"),
        ["Telegram.OnCancel"] = ("The countdown is cancelled", "إلغاء العد التنازلي"),
        ["Telegram.OnProgress"] = ("Progress advances by this many percent", "تقدّم التنزيل بهذه النسبة المئوية"),
        ["Telegram.RemoteButtons"] = (
            "Add \"run it now\" and \"don't\" buttons to the finish message",
            "أضف زرَّي \"نفّذ الآن\" و\"لا تطفئ\" إلى رسالة الانتهاء"),
        ["Telegram.RemoteButtonsHint"] = (
            "Lets you settle the countdown from your phone. Anyone who can use the bot can press them.",
            "يتيح حسم العد التنازلي من الهاتف. أي شخص يستطيع استخدام البوت يستطيع الضغط عليهما."),
        ["Telegram.MessageLanguage"] = ("Message language", "لغة الرسائل"),
        ["Telegram.SendTest"] = ("Send test message", "إرسال رسالة تجريبية"),
        ["Telegram.NotIdentified"] = ("not identified yet", "لم يُتعرّف عليها بعد"),

        // ---------------------------------------------------------------- Tray
        ["Tray.Open"] = ("Open SteamFinish", "فتح SteamFinish"),
        ["Tray.Exit"] = ("Exit", "خروج"),
        ["Tray.MonitoringOff"] = ("SteamFinish · monitoring is off", "SteamFinish · المراقبة متوقفة"),
        ["Tray.StillRunning"] = ("SteamFinish is still running", "SteamFinish ما زال يعمل"),
        ["Tray.StillRunningBody"] = ("Open it again from the tray icon.", "افتحه مجدداً من أيقونة شريط المهام."),
        ["Tray.DownloadsFinished"] = ("Downloads finished", "اكتملت التنزيلات"),
        ["Tray.CountdownBody"] = (
            "{0} in {1} seconds. Open SteamFinish to cancel.",
            "{0} خلال {1} ثانية. افتح SteamFinish للإلغاء."),
        ["Tray.CountdownCancelled"] = ("Countdown cancelled", "أُلغي العد التنازلي"),
        ["Tray.CancelledActivity"] = ("Steam started working again.", "Steam عاد للعمل."),
        ["Tray.CancelledUnavailable"] = ("Steam's state could not be read.", "تعذّرت قراءة حالة Steam."),
        ["Tray.StartupRefused"] = ("Start with Windows", "التشغيل مع ويندوز"),
        ["Tray.StartupRefusedBody"] = (
            "Windows would not let SteamFinish change the startup entry.",
            "لم يسمح ويندوز لـ SteamFinish بتغيير إعداد بدء التشغيل."),
    };

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static UiLanguage Current { get; private set; } = UiLanguage.English;

    public static bool IsRightToLeft => Current == UiLanguage.Arabic;

    /// <summary>Bound from XAML as <c>[Key]</c>, which is what makes live switching work.</summary>
    public string this[string key] => Get(key);

    public static void Use(UiLanguage language)
    {
        if (Current == language)
        {
            return;
        }

        Current = language;

        // "Item[]" is the signal WPF uses to re-evaluate every indexer binding.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    /// <summary>Raised after a switch so code-built strings (tray menu, tooltips) can be rebuilt.</summary>
    public static event Action? LanguageChanged;

    /// <summary>The translation for <paramref name="key"/>, or the key itself when it is missing.</summary>
    public static string Get(string key)
    {
        if (!Table.TryGetValue(key, out var entry))
        {
            return key;
        }

        return Current == UiLanguage.Arabic ? entry.Ar : entry.En;
    }

    /// <summary>A formatted translation, e.g. <c>F("Phase.Countdown", "Shutdown", 42)</c>.</summary>
    public static string F(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    /// <summary>
    /// Fences a left-to-right run — a game name, a size, a percentage — so the bidi algorithm cannot
    /// drag its trailing punctuation to the wrong end inside an Arabic sentence. Without this,
    /// "Khazan (0%)" comes out as "(Khazan (0%".
    /// </summary>
    public static string Ltr(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsRightToLeft)
        {
            return value ?? string.Empty;
        }

        const char Mark = '‎'; // LEFT-TO-RIGHT MARK
        return Mark + value + Mark;
    }

    /// <summary>Exposed for tests: every key resolves in both languages.</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;
}

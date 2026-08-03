# SteamFinish

> Download. Finish. Power Off.

A small Windows tray app that shuts the PC down once Steam has finished downloading —
but only while you have monitoring switched on.

<p align="center">
  <em>Monitor tab · Action tiles · Cancellable countdown · Settings</em>
</p>

---

## What it does

1. You press **Enable Monitoring**.
2. SteamFinish locates every Steam library and watches `steamapps` in each of them.
3. When nothing is downloading, installing, staging, validating, queued or paused any more,
   it waits out a **quiet period** (45 s by default) to be sure no new download appears.
4. A **cancellable countdown** runs (60 s by default).
5. The chosen action runs: **Shutdown**, **Sleep**, **Hibernate** or **Restart**.

Anything Steam starts during the quiet period or the countdown sends it straight back to watching.

## Live progress

The Monitor tab mirrors what the Steam client shows, refreshed every second:

- **Downloading data** — bytes off the network, with its **own** percentage
- **Installing files** — bytes written to disk, on a **separate** percentage
- **Network**, **Peak** and **Disk usage**, derived from how the counters move
- **Time left**, **Finishes at** (the clock time it should be done) and **Left to download**
- **Up next** — the games still waiting, each with its size and percentage. The download currently
  running has its own card at the top and is not repeated in the list.

The two percentages differ on purpose. Steam labels a game by its *staged* share, so a download can
read 78% transferred and 75% installed at the same moment; both figures are now shown rather than
one standing in for the other.

With **Keep the status live while the window is open** on (the default) the numbers keep updating
even while monitoring is off. Hidden in the tray with monitoring off, nothing is read from disk.

### Which game is actually downloading

This is harder than it looks. Steam's `StateFlags` do **not** distinguish the running download from
the queue behind it — while one game downloaded and another sat stalled, both read `1026`
(`UpdateRequired|UpdateStarted`), and the `Locked` flag was set on the *stalled* one, not the live
one. So no flag can be trusted for this.

SteamFinish decides by observation instead: it tracks each app's byte counters across scans and the
one whose counters actually grow is the live download. Until movement has been measured — right
after launch — it falls back to the app whose manifest Steam rewrote most recently.

### Paused downloads

Steam does not set `UpdatePaused` when you press pause either. A paused Khazan reads `1026`, exactly
the same as a running one; the only difference is that its counters stop and Steam stops rewriting
its manifest.

So a pause is detected the same way: when the current download has not moved for
`StalledAfterSeconds` (60 by default), the status reads **Paused**, the bar turns amber, the speeds
read `0 bps` and the estimates blank out — matching what the Steam client shows. If the download was
already paused before SteamFinish started, a manifest older than two minutes gives it away.

Because the two cannot be told apart from disk alone, a dropped connection reads as paused too. That
is the safe direction: either way it is not finished, and the countdown stays blocked.

## Xbox app support

Steam and the **Xbox app / Microsoft Store** are watched together. Tick either or both under
**Settings → Launchers to watch**; the action waits for every ticked launcher to be finished, and
each download carries a **Steam** or **Xbox** badge so a mixed queue stays readable.

Xbox progress comes from Gaming Services' own bookkeeping, not from guessing at file sizes. Each
in-flight install has a JSON checkpoint under
`HKLM\SOFTWARE\Microsoft\GamingServices\StreamingCheckpoints` holding exactly what the Xbox app
itself displays:

```json
{ "State": "Running", "Type": "Install", "QueueOrder": 0,
  "Status": { "Operation": "Streaming",
              "Progress": { "Package": { "TotalBytes": 77016702976, "StreamedBytes": 563023872 } } },
  "PC": { "PackageFullName": "WarnerBros.Interactive.PHX_1.0.16.0_x64__ktmk1xygcecda" } }
```

The friendly title comes from the game's own `MicrosoftGame.config`, found by joining the checkpoint's
content GUID to the games folder — every drive that can hold Xbox games has a hidden `.GamingRoot`
file at its root naming that folder (`RGBX`, a version word, then a UTF-16 path), which is how the
Xbox app locates them too.

Xbox states are translated into the same vocabulary Steam uses, so the monitor engine, the transfer
meter and the whole UI treat both platforms identically:

| Checkpoint | Treated as |
| --- | --- |
| `State: Running`, `Operation: Streaming` | downloading |
| `State: Running`, any other operation | installing |
| `State` containing `Paus` | paused — blocks, exactly like Steam |
| Queued, suspended, or a state this build has not seen | outstanding (blocks; the safe direction) |
| Fully streamed and no longer running | finished |

> `AppInstallManager`, the documented WinRT install API, is deliberately not used: it only reports
> installs the calling app started itself, so it cannot see the Xbox app's downloads. Verified empty
> on a machine that was actively downloading.

## How "finished" is decided

Every library's `steamapps\appmanifest_*.acf` file is parsed and its `StateFlags` inspected:

| Situation | `StateFlags` example | Treated as |
| --- | --- | --- |
| Downloading, staging, committing, validating, preallocating, uninstalling | `UpdateRunning`, `Downloading`, `Staging`, … | **busy** |
| Queued, whether or not it has started | `UpdateStarted` (1026, 1042, …) | **busy** |
| Update queued with bytes assigned | `UpdateRequired` + `BytesToDownload > BytesDownloaded` | **busy** |
| Download paused | `UpdatePaused` | **busy** (configurable) |
| `steamapps\downloading\<appid>` present for an unsettled app | — | **busy** |
| Update available but never started | `FullyInstalled \| UpdateRequired`, no byte counters | **idle** |
| Installed, game running | `FullyInstalled \| AppRunning` | **idle** |

Note that "not the live download" never means "finished" — a stalled or queued game still blocks the
countdown. Identifying the live one only affects what is *displayed*, never when the action fires.

Two details worth knowing:

- **A dropped connection is not a finished download.** Steam keeps the update flags set while it
  retries, so the state never reads as idle and the countdown never starts.
- **Stale byte counters are ignored.** Steam leaves `BytesToDownload` behind on finished games; on
  their own those counters mean nothing, otherwise the countdown could never start again.

Libraries are found through `HKCU\Software\Valve\Steam` and both the modern and the legacy
`libraryfolders.vdf` layouts, so multiple libraries on multiple drives all work. You can add more
by hand in **Settings**.

## Safety behaviour

- **Nothing is scanned while monitoring is off.** No timers, no watchers, no disk reads.
- **"Only act after a download has been seen"** is on by default, so enabling monitoring on an idle
  machine cannot power it off a minute later. Turn it off if you want SteamFinish to act on an
  already-idle PC.
- **Cancelling keeps monitoring on but disarms the action** — a fresh download has to appear before
  another countdown can start. Otherwise cancelling would be pointless while Steam stays idle.
- **If Steam's state cannot be read**, the phase becomes *blocked* and no action fires.
- Cancelling also issues `shutdown /a`, which aborts a system shutdown that some *other* program may
  have queued. SteamFinish's own countdown runs in-app, so there is nothing of its own to abort.
- Apps are asked to close normally. **Force apps to close** adds `/f` to `shutdown.exe`, which
  discards unsaved work — it is off by default.

## Telegram notifications

SteamFinish can message you on Telegram while a download runs and, most usefully, right before it
powers the PC off.

**Setup** — all in **Settings → Telegram notifications**:

1. Press **Create a bot (@BotFather)**, send `/newbot`, and copy the token it gives you.
2. Paste the token into **Bot token**. It is masked once entered; tick **Show the token** to read it
   back. Anyone holding the token controls the bot, so it is kept off the screen by default.
3. Press **Find my chat**, then send `/start` to your bot. See below.
4. Press **Send test message**. It checks the token with `getMe`, reports the bot's `@username` and
   posts a message to every chat, so you know both halves are right before relying on it.
5. Tick **Send Telegram messages**.

### Finding the chat ID from inside the app

Pressing **Find my chat** listens for the next message your bot receives, then sends a **six-digit
code** to whichever chat that was. The same code appears in the app, and the chat is only added once
you confirm the two match. That handshake is the point: it proves the app is talking to *your* chat
and not to whoever else happened to message the bot.

- **Yourself** — open the bot in Telegram and send `/start`.
- **A group** — add the bot to the group, then send `/start` there. Being added is enough on its own:
  SteamFinish also listens for `my_chat_member`, which arrives even when Telegram's privacy mode
  hides ordinary group messages from bots.
- **A channel** — add the bot as an administrator, then post anything.

Anything sent *before* you press the button is skipped, so an old message cannot pair by accident.
Add as many chats as you like; every message goes to all of them.

Once you confirm, the code message in Telegram is **edited into a confirmation** so the chat ends on
a clear result rather than a stale number. If the edit is refused — Telegram forbids it after 48
hours, and in channels without the right permissions — a fresh message is sent instead.

Configured chats are listed by **name and kind** rather than as bare numbers, for example
`Hussam (private chat)` or `Gaming Nights (group)`, with the id underneath so it stays verifiable.
Names are resolved through `getChat` and cached, so the list still reads properly offline.

Typing an ID by hand still works — [@userinfobot](https://t.me/userinfobot) reports yours.

**What gets sent**

| Trigger | Contents |
| --- | --- |
| A download starts | Game name, size, how many are queued behind it |
| Every *N* percent (default 5) | Bar, percentage, bytes, current speed, ETA, queue depth |

| Everything finishes | Game list with sizes, total size, how long it took, average speed, and the action with its countdown |
| The countdown is cancelled | Which action was cancelled and why |

Progress and start messages describe the *download*, not the shutdown, so they are sent whenever
Telegram is configured for them — monitoring does not have to be armed. Turning them off in settings
stops both the messages and the background scanning they need.

### Deciding from your phone

The finish message carries two buttons, so the countdown can be settled without walking to the PC:

| Button | What happens |
| --- | --- |
| **⚡ Shutdown now** | Skips the rest of the countdown and runs the action immediately |
| **🛑 Don't** | Cancels it; the PC stays on and monitoring continues |

Either way the message itself is **rewritten** to say what was decided and who decided it, and the
buttons are removed so they cannot be pressed twice. If the countdown is instead settled at the PC,
the same message is rewritten to say that.

Every countdown mints a fresh random token embedded in both buttons. A button from an earlier
countdown — still sitting in the chat history — is rejected, so an old message can never power the
PC off. Note that anyone who can use the bot can press them; turn the buttons off under
**Settings → Telegram** if the bot lives in a shared group.

The finish message arrives *before* the countdown ends, so there is time to react:

```
🎮 SteamFinish

✅ اكتملت جميع التنزيلات

📦 الألعاب (2):
• The First Berserker: Khazan — 22.5 GB
• DARK SOULS III — 23.7 GB

💾 الحجم الكلي: 46.2 GB
💽 على القرص: 77.5 GB
⏱ استغرق: 3 ساعات و47 دقيقة
🚀 متوسط السرعة: 27.1 Mbps

🔌 سيتم إطفاء الحاسبة خلال 60 ثانية
افتح SteamFinish للإلغاء
```

Messages come in **Arabic** by default; switch to English under **Message language**. Progress steps
are tracked per game, so each one reports its own 5%, 10%, 15%… Turning Telegram on halfway through a
download does not fire a burst of catch-up messages.

> The bot token is stored in plain text in `%AppData%\SteamFinish\settings.json`, like any other
> setting. Anyone who can read that file can post as your bot, so treat it the way you would a
> password. It is never written to the log.

## Appearance

**Settings → Appearance** offers **Match Windows**, **Light** and **Dark**. The default follows the
Windows app theme and re-checks whenever Windows itself changes, so switching your system to dark at
night takes SteamFinish with it.

Themes are two palette dictionaries with identical keys ([Palette.Light.xaml](src/SteamFinish/Themes/Palette.Light.xaml),
[Palette.Dark.xaml](src/SteamFinish/Themes/Palette.Dark.xaml)) that are swapped at runtime. Every
control style in [Controls.xaml](src/SteamFinish/Themes/Controls.xaml) reads its colours with
`DynamicResource`, so the swap repaints the window in place — no restart, no reload. A test asserts
the two palettes define exactly the same keys and that no control style pins a brush statically.

## Language

The interface ships in **English and Arabic**, switched under **Settings → Language**. The change
applies immediately — every label rebinds in place, and Arabic lays the whole window out
right-to-left. Values that are not words (speeds, sizes, percentages, chat ids, file paths, the bot
token) are pinned left-to-right so `0 bps` does not come out as `bps 0`.

This is separate from **Telegram → Message language**, which controls what the bot writes to your
phone. You can read the app in English and get Arabic messages, or the other way round.

## Requirements

- Windows 10 1809 or newer
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

> The specification asked for .NET 8. The project targets **`net10.0-windows`** because that is the
> runtime installed on the build machine; nothing in the code is version-specific. To move back,
> change `TargetFramework` in the three `.csproj` files and install the .NET 8 Desktop Runtime.

## Build and run

```powershell
dotnet build                 # whole solution
dotnet test                  # 64 unit and end-to-end tests
dotnet run --project src/SteamFinish
```

Produce a redistributable build:

```powershell
dotnet publish src/SteamFinish -c Release -r win-x64 --self-contained false
# add --self-contained true /p:PublishSingleFile=true for a runtime-free single .exe
```

Regenerate the icon after editing the artwork in `tools/Make-Icon.ps1`:

```powershell
.\tools\Make-Icon.ps1
```

## Releases and updating

Two workflows live in [.github/workflows](.github/workflows):

| Workflow | Runs on | What it does |
| --- | --- | --- |
| `ci.yml` | every push and PR to `main` | restore, build, run the full test suite |
| `release.yml` | a `v*` tag, or manually | builds, tests, publishes and attaches a release |

Cutting a release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The workflow publishes a **self-contained single-file** `SteamFinish.exe` (~148 MB — the .NET
runtime is bundled) and attaches two ways to get it, each with a `.sha256` beside it:

| Asset | For |
| --- | --- |
| `SteamFinish-<version>-setup.exe` | The installer: asks where to install and whether to add a Desktop shortcut |
| `SteamFinish-<version>-win-x64.zip` | Portable — unzip and run |

Re-running the workflow for the same tag replaces the assets instead of failing. You can also
trigger it by hand from the Actions tab and type the version in.

### The installer

Built from [installer/SteamFinish.iss](installer/SteamFinish.iss) with Inno Setup, which the
workflow installs on the runner. The wizard shows its normal pages, so you choose:

- **where to install** — per-user by default (`%LocalAppData%\Programs\SteamFinish`), so no
  administrator prompt; the wizard still offers an all-users install
- **a Desktop shortcut** — a tick box
- **start with Windows** — an optional tick box, writing the same registry value the app itself uses

It carries a fixed `AppId`, so installing a newer build upgrades the existing one rather than adding
a second entry to Apps & features, and it offers to close a running copy instead of failing on a
locked file. Uninstalling leaves `%AppData%\SteamFinish` alone, so settings survive a reinstall.

### Updating from inside the app

The running version is shown in the header (`v1.0.0`) and under **Settings → Version**. The app asks
GitHub for the newest release on start-up — quietly, only speaking up when there is something newer —
and **Check for updates** asks on demand.

When a newer release exists, **Download and install** fetches it, checks it against the published
SHA256, and hands over to a small script that waits for the app to close, swaps the files and starts
the new build. A running executable cannot overwrite itself, which is why the swap happens after exit.

Automatic checking can be turned off, and the repository it checks is `UpdateRepository` in the
settings file.

### Updating the copy on your Desktop

```powershell
iwr -useb https://raw.githubusercontent.com/hmh6a/SteamFinish/main/tools/Update-SteamFinish.ps1 | iex
```

Or, from a checkout:

```powershell
.\tools\Update-SteamFinish.ps1 -Repo hmh6a/SteamFinish
```

It fetches the newest release, checks it against the published SHA256, closes a running SteamFinish,
replaces `Desktop\SteamFinish`, and starts the new build. The repository is remembered after the
first run, so afterwards the bare command is enough. It exits early when you are already on the
latest version.

**Your settings are safe**: everything lives in `%AppData%\SteamFinish`, which the updater never
touches — bot token, chat list, theme, language and all.

The only destructive step refuses to run unless the target folder actually contains
`SteamFinish.exe`, so pointing it at the Desktop itself, or at any folder that is not one of its own
installs, is an error rather than a deletion.

## Settings

Stored in `%AppData%\SteamFinish\settings.json` and saved automatically.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Action` | `Shutdown` | Shutdown, Sleep, Hibernate or Restart |
| `CountdownSeconds` | `60` | Length of the cancellable countdown |
| `ConfirmationSeconds` | `45` | Quiet period before the countdown starts |
| `StartWithWindows` | `false` | Adds a per-user `Run` registry entry with `--minimized` |
| `StartMinimized` | `false` | Start in the tray without showing the window |
| `CloseToTray` | `true` | Closing the window hides it instead of exiting |
| `TrayNotifications` | `true` | Balloon notifications (errors always show) |
| `SoundNotification` | `true` | Play a sound when the countdown starts |
| `AutoDetectLibraries` | `true` | Detect libraries from the registry and `libraryfolders.vdf` |
| `ManualLibraries` | `[]` | Extra library roots (folders containing `steamapps`) |
| `RequireDownloadBeforeAction` | `true` | Arm only after a download has been observed |
| `IgnorePausedDownloads` | `false` | Treat a paused download as finished |
| `ForceCloseApps` | `false` | Pass `/f` to `shutdown.exe` |
| `EnableLogging` | `true` | Write `%AppData%\SteamFinish\steamfinish.log` |
| `LiveStatusWhileOpen` | `true` | Keep reading Steam's state while the window is open |
| `PollIntervalSeconds` | `1` | Scan interval; JSON only, no UI |
| `StalledAfterSeconds` | `60` | Standstill before a download reads as paused; JSON only |
| `Language` | `English` | Interface language: `English` or `Arabic` |
| `Theme` | `System` | `System`, `Light` or `Dark` |
| `WatchSteam` | `true` | Watch Steam downloads |
| `WatchXbox` | `true` | Watch Xbox app / Microsoft Store installs |
| `UpdateRepository` | `hmh6a/SteamFinish` | GitHub repository updates are fetched from |
| `CheckForUpdates` | `true` | Look for a newer release on start-up |
| `Telegram.ChatLabels` | `{}` | Cached chat names for the list; cosmetic, refreshed automatically |
| `Telegram.Enabled` | `false` | Send Telegram messages |
| `Telegram.BotToken` | `""` | Token from @BotFather |
| `Telegram.ChatIds` | `[]` | Every chat that receives the messages |
| `Telegram.NotifyOnStart` | `true` | Message when a download starts |
| `Telegram.NotifyOnProgress` | `true` | Message on each progress step |
| `Telegram.ProgressStepPercent` | `5` | Percent between progress messages |
| `Telegram.NotifyOnFinish` | `true` | Message before the action runs |
| `Telegram.NotifyOnCancel` | `true` | Message when the countdown is cancelled |
| `Telegram.Language` | `Arabic` | `Arabic` or `English` |

Only one copy runs at a time; launching it again brings the existing window to the front.

## Project layout

```
src/SteamFinish.Core/      Logic with no UI dependency — fully unit tested
  Vdf/                     KeyValues parser for .vdf and .acf files
  Steam/                   Library discovery, manifest scanning, transfer rates, status text
  Monitoring/              The state machine that decides when to act, plus session recording
  Notifications/           Telegram client, message templates and send rules
  Power/                   shutdown.exe and SetSuspendState
  Formatting/ Settings/ Startup/ Logging/
src/SteamFinish/           WPF app: window, view model, tray icon, timers
tests/SteamFinish.Tests/   xUnit tests, including end-to-end runs over real folders
tools/Make-Icon.ps1        Generates the multi-resolution app icon
```

`MonitorEngine` owns no timers and touches no disk — it takes a snapshot plus the current time and
returns the next phase, which is why the timing rules can be tested without waiting in real time.

## Not implemented

Listed in the specification under future improvements: Epic Games, EA App, Ubisoft Connect,
Battle.net and GOG Galaxy support, and ntfy notifications. (Xbox app support, multi-platform
monitoring, Telegram notifications, event logging and the dark theme are done.)

---

## بالعربية

**SteamFinish** برنامج صغير لويندوز يطفئ الحاسبة تلقائياً بعد انتهاء تنزيلات Steam،
وفقط عندما يقوم المستخدم بتفعيل المراقبة.

- يراقب `steamapps/downloading` وملفات `appmanifest_*.acf` في كل مكتبات Steam.
- يعرض التقدّم لحظياً: نسبة التحميل ونسبة التثبيت **منفصلتين**، مع السرعة والذروة واستهلاك القرص،
  والوقت المتبقي وساعة الاكتمال المتوقعة.
- يعرض قائمة "التالي" للألعاب المنتظرة فقط — اللعبة الجارية لها بطاقتها الخاصة ولا تتكرر بالقائمة.
- يحدّد اللعبة التي تُحمّل فعلاً عبر مراقبة تغيّر البايتات، لأن أعلام Steam لا تفرّق بين الجارية
  والمنتظرة ولا حتى بين الجارية والمتوقفة مؤقتاً.
- يعرض حالة **الإيقاف المؤقت**: عند توقف البايتات تظهر "Paused" ويتحوّل الشريط للبرتقالي وتصبح
  السرعة `0 bps` — تماماً كما يعرضها Steam.
- **اكتشاف الـ chat ID تلقائياً**: زر يفتح @BotFather لإنشاء البوت، ثم زر "Find my chat" ينتظر
  رسالتك (`/start`) ويرسل **رمزاً من ٦ أرقام** إلى تلك المحادثة ويعرض نفس الرمز في البرنامج؛
  لا يُضاف الـ chat ID إلا بعد أن تؤكد تطابق الرمزين. يعمل مع المحادثة الخاصة والكروبات والقنوات،
  ويمكن إضافة أكثر من محادثة. الإدخال اليدوي ما زال متاحاً كما هو.
- التوكن مخفي افتراضياً في الواجهة، مع خيار إظهاره عند الحاجة.
- تظهر المحادثات المضافة **باسمها ونوعها** — مثل `Hussam (private chat)` أو `Gaming Nights (group)` —
  مع المعرّف تحتها، بدل رقم مجرّد.
- عند تأكيد الربط تُعدَّل رسالة الرمز في تيليجرام إلى رسالة نجاح واضحة.
- **واجهة البرنامج بالعربية والإنجليزية**، تُبدَّل من الإعدادات وتُطبَّق فوراً، مع تخطيط من اليمين
  لليسار. القيم الرقمية (السرعات والأحجام والنسب والمعرّفات والمسارات) مثبّتة من اليسار لليمين
  حتى لا تظهر `0 bps` بصيغة `bps 0`.
- رسائل التقدّم كل ٥٪ تُرسَل حتى لو كانت المراقبة متوقفة، لأنها تصف التنزيل لا الإطفاء.
- **وضع داكن وفاتح**، مع خيار "حسب ويندوز" الذي يتبع إعداد النظام ويتغيّر معه أثناء التشغيل.
- **دعم تطبيق Xbox / متجر مايكروسوفت** إلى جانب Steam: يمكن تفعيل أي منهما أو كليهما، ولا يُنفَّذ
  الإجراء إلا بعد انتهاء تنزيلات كل منصّة مفعّلة. وكل تنزيل يحمل **تاك** يبيّن مصدره (Steam أو Xbox).
- تُقرأ نسبة تنزيلات Xbox من سجلّ Gaming Services نفسه — نفس الأرقام التي يعرضها تطبيق Xbox — ويُقرأ
  اسم اللعبة من ملف `MicrosoftGame.config` الخاص بها.
- **رقم الإصدار يظهر داخل البرنامج** (في الترويسة وفي الإعدادات)، وزر **تنزيل وتثبيت** يظهر تلقائياً
  عند وجود إصدار أحدث: ينزّله ويتحقق من بصمته ثم يستبدل البرنامج ويعيد تشغيله.
- **تاك بلون كل منصّة**: أزرق Steam وأخضر Xbox.
- **بناء ونشر تلقائي عبر GitHub Actions**: عند دفع وسم `v1.0.0` يُبنى البرنامج ويُختبر ويُنشَر
  إصدار فيه ملف `.exe` واحد مكتفٍ ذاتياً (لا يحتاج تثبيت .NET)، مع بصمة SHA256.
- **مُنصِّب** يسألك **أين تريد التنصيب** وهل تريد **اختصاراً على سطح المكتب** (وخيار التشغيل مع
  ويندوز). تنصيب لمستخدمك بلا صلاحيات مدير، ويحدّث النسخة الموجودة بدل تكرارها.
- **أمر تحديث واحد** يستبدل النسخة على سطح المكتب بآخر إصدار، ويتحقق من البصمة، ولا يمسّ إعداداتك
  في `%AppData%\SteamFinish`. ولا يحذف أي مجلد إلا إذا تأكّد أنه يحتوي `SteamFinish.exe`.
- **زرّان في رسالة الانتهاء على تيليجرام**: «⚡ أطفئ الآن» ينفّذ فوراً دون انتظار العد التنازلي،
  و«🛑 لا تطفئ» يلغيه وتبقى الحاسبة تعمل. وتُعدَّل الرسالة نفسها لتبيّن ما حدث ومَن قرّره، وتُزال
  الأزرار كي لا تُضغط مرتين. ولكل عدّ تنازلي رمز خاص، فزر من عدٍّ قديم لا يعمل.
- يرسل إشعارات تلكرام: عند بدء التحميل، وكل ٥٪ تقدّم، ورسالة مرتبة قبل الإطفاء تذكر الألعاب
  وأحجامها والمدة والسرعة — مع زر لتجربة الإرسال والتأكد من التوكن والجات آيدي.
- انقطاع الإنترنت أو إيقاف التنزيل مؤقتاً **لا يعتبر** انتهاء تحميل.
- ينتظر فترة هدوء (٤٥ ثانية افتراضياً) للتأكد من عدم وجود تنزيل جديد، ثم يبدأ العد التنازلي.
- أي تنزيل جديد أثناء العد يلغيه ويعود للمراقبة.
- الخيارات: إطفاء، سكون، إسبات، إعادة تشغيل — مع إمكانية الإلغاء أثناء العد.
- يعمل من System Tray ولا يقرأ القرص إطلاقاً عندما تكون المراقبة متوقفة.

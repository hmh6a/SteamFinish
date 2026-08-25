<div align="center">

<img src="docs/logo.png" width="96" alt="SteamFinish">

# SteamFinish

**Download. Finish. Power Off.**

Leave a download running overnight and let the PC shut itself down when it is done —
but only while you say so.

[![CI](https://github.com/hmh6a/SteamFinish/actions/workflows/ci.yml/badge.svg)](https://github.com/hmh6a/SteamFinish/actions/workflows/ci.yml)
[![Release](https://github.com/hmh6a/SteamFinish/actions/workflows/release.yml/badge.svg)](https://github.com/hmh6a/SteamFinish/actions/workflows/release.yml)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

</div>

---

## What it does

Press **Enable Monitoring** and SteamFinish watches your downloads. When everything has finished —
downloaded, installed and settled — it waits out a quiet period, counts down, and then shuts down,
sleeps, hibernates or restarts.

Nothing is watched until you switch it on, and a countdown can always be cancelled.

**Steam and the Xbox app are both supported**, together or separately.

## Features

- **Live progress** — separate bars for bytes downloaded and bytes installed, with speed, peak,
  disk usage, time left and the clock time it will finish at
- **A real queue** — every game still waiting, each badged **Steam** or **Xbox**
- **Knows what "finished" means** — a paused download, a dropped connection or a queued update all
  keep the PC awake
- **Telegram notifications** — progress every *N* percent, and a message before the PC powers off
  with **Shutdown now** / **Don't** buttons you can press from your phone
- **Pause a download from your phone** — `/pause`, `/resume` and `/status` in the chat, or the
  buttons that come with them
- **Arabic and English**, switched instantly, with full right-to-left layout
- **Light, dark, or follow Windows**
- **Updates itself** — checks GitHub and installs the new build with one click
- Lives in the system tray and reads nothing from disk while monitoring is off

## Install

Grab the newest [release](https://github.com/hmh6a/SteamFinish/releases):

| | |
| --- | --- |
| **`SteamFinish-x.y.z-setup.exe`** | Installer — asks where to install and whether to add a Desktop shortcut |
| **`SteamFinish-x.y.z-win-x64.zip`** | Portable — unzip and run |

The .NET runtime is bundled, so there is nothing else to install, and a per-user install needs no
administrator rights. Every asset has a `.sha256` beside it.

**Requires** Windows 10 1809 or newer.

## Using it

1. Pick the action: **Shutdown**, **Sleep**, **Hibernate** or **Restart**.
2. Start your downloads in Steam or the Xbox app.
3. Press **Enable Monitoring**.

That is all. Closing the window hides it to the tray; the tray icon has Enable, Cancel and Exit.

> **First run:** by default nothing happens until a download has actually been seen, so enabling
> monitoring on an idle PC cannot power it off a minute later. There is a setting for that.

### Telegram (optional)

In **Settings → Telegram**: press **Create a bot**, paste the token, then press **Find my chat** and
send `/start` to your bot. It sends a six-digit code to the chat and only adds it once you confirm
the codes match — so it cannot pair with the wrong chat. Groups and channels work too.

### Pausing a download from your phone

Once a chat is paired the bot answers four commands:

| | |
| --- | --- |
| `/pause` | stop Steam downloading |
| `/resume` | start it again |
| `/status` | what is downloading, how fast, and what happens when it finishes |
| `/help` | the list above |

Every reply carries **⏸ Pause** and **▶️ Resume** buttons, and so does the message announcing a new
download — so after the first message it is one tap. Only the chats you paired are obeyed: holding
the bot token is not enough to stop someone's download.

Commands reach the PC that is reading them. If more than one machine reports into the same chat —
each needs its own bot — put the name after the command to reach just one: `/pause laptop`. Every
message is headed with the machine's name, taken from Windows and editable in **Settings →
Telegram → Name of this PC**.

**This needs one bit of setup, and it is worth knowing why.** Steam has no command line, registry
key or `steam://` URL that pauses a download. What it does have is a Chromium-based client whose
Downloads page drives the downloader through a privileged `SteamClient` object, and Chromium can be
asked to expose that object locally. So SteamFinish says exactly what Steam's own pause button says
— and to be allowed to, Steam needs a marker file in its install folder and one restart:

1. **Settings → Telegram → Enable download control.** This writes the file; it needs no
   administrator rights.
2. **Restart Steam once.** The marker is only read at start-up.

The button then reports whether the channel is open, and on which port.

### About that port

Steam's control channel defaults to port 8080, and 8080 is popular — Docker, WSL and half the dev
servers ever written want it. Steam picks its port when it starts and never revisits it, so a client
that came up while something else held 8080 has no channel at all.

Neither half of that is left to the user to work out:

- **Finding it.** The port is discovered, not assumed. SteamFinish asks Windows which ports the
  running `steam.exe` is listening on and tries those, so a Steam started with `-devtools-port`
  is found without being told. The port that worked is remembered and tried first next time.
- **Freeing it.** When nothing can be reached, **Restart Steam on a free port** closes Steam with
  its own `-shutdown` and starts it again with the channel on a port nothing is using. Downloads
  carry on from where they stopped. It never happens on its own — only when that button is pressed.

To make it permanent, add `-devtools-port 8123` (any free port) to your own Steam shortcut;
SteamFinish will find it.

**Steam only.** The Xbox app exposes no way to control a download at all, so `/pause` covers Steam
and says as much.

A paused download is not a finished one, so pausing from your phone will never let the PC power
itself off — unless you have deliberately ticked *Treat paused downloads as finished*.

## Building

```powershell
dotnet build          # whole solution
dotnet test           # the test suite
dotnet run --project src/SteamFinish
```

Releases are cut by pushing a tag:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

GitHub Actions then builds, tests, publishes a self-contained executable, wraps it in an installer
and attaches everything to the release.

## How it is put together

```
src/SteamFinish.Core/     Logic with no UI dependency — fully unit tested
  Steam/ Xbox/            Reading each launcher's download state
  Monitoring/             The state machine that decides when to act
  Notifications/          Telegram messages, remote buttons, the command listener
  Control/                Pausing and resuming Steam downloads
  Power/ Settings/ Localization/ Updates/
src/SteamFinish/          WPF app: window, view models, tray icon, timers
tests/SteamFinish.Tests/  200+ tests, including end-to-end runs over real folders
installer/                Inno Setup script
```

`MonitorEngine` owns no timers and touches no disk: it takes a snapshot plus the current time and
returns the next phase. That is why the timing rules can be tested without waiting in real time.

## Notes

Working out whether a download has finished is harder than it looks. Steam's `StateFlags` do not
distinguish a running download from a queued one, and do not change at all when you press pause —
so SteamFinish decides by watching whether the byte counters actually move. Xbox progress comes from
Gaming Services' own records rather than from guessing at file sizes.

Pausing one is harder still. Steam offers nothing to the outside world for it, so the only honest
route is through the client's own JavaScript — which is why that feature asks for a marker file and
a restart instead of just working. The alternatives were worse: suspending `steam.exe` freezes the
whole client, and blocking it in the firewall knocks the client offline rather than pausing it.

The port that channel uses is discovered rather than configured. Asking Windows for the listening
sockets owned by `steam.exe` and probing those beats asking the user which port to use, because the
user does not know either — Steam chose it at start-up.

Telegram allows one long-poll listener per bot, and whichever poller confirms an update throws it
away for the others. So the command loop is the only thing polling in normal operation, and the two
flows that need the connection to themselves — pairing a chat, and the countdown buttons — take it
and give it back.

The details, and the reasoning behind each decision, are in the source comments.

---

<div align="center" dir="rtl">

## بالعربية

**SteamFinish** — برنامج صغير لويندوز يطفئ الحاسبة تلقائياً بعد انتهاء التنزيلات،
وفقط عندما تفعّل المراقبة بنفسك.

</div>

<div dir="rtl">

- يدعم **Steam** و**تطبيق Xbox** معاً أو منفصلين، وكل تنزيل يحمل تاكاً يبيّن مصدره.
- يعرض التقدّم لحظياً: نسبة التنزيل ونسبة التثبيت منفصلتين، مع السرعة والذروة والوقت المتبقي
  وساعة الاكتمال المتوقعة.
- **الإيقاف المؤقت أو انقطاع الإنترنت لا يُعتبر انتهاءً** — تبقى الحاسبة تعمل.
- بعد انتهاء كل شيء ينتظر فترة هدوء ثم يبدأ عدّاً تنازلياً يمكن إلغاؤه في أي لحظة.
- **إشعارات تيليجرام**: تقدّم كل نسبة تحدّدها، ورسالة قبل الإطفاء فيها زرّان —
  «أطفئ الآن» و«لا تطفئ» — تتحكم بهما من هاتفك.
- **إيقاف التنزيل واستئنافه من الهاتف**: أرسل `/pause` أو `/resume` أو `/status` إلى البوت،
  أو اضغط الزرّين المرفقين بكل رد. لا يُستجاب إلا للمحادثات التي ربطتها بنفسك.
- **واجهة عربية وإنجليزية** تتبدّل فوراً، مع تخطيط كامل من اليمين لليسار.
- **وضع فاتح وداكن** أو حسب إعداد ويندوز.
- **يحدّث نفسه** من GitHub بضغطة واحدة.

**التنصيب**: نزّل `setup.exe` من [الإصدارات](https://github.com/hmh6a/SteamFinish/releases) —
يسألك عن المسار وعن اختصار سطح المكتب، ولا يحتاج صلاحيات مدير. أو استخدم نسخة الـ `zip` المحمولة.
لا تحتاج تثبيت .NET، فهو مضمّن.

**الاستخدام**: اختر الإجراء، ابدأ تنزيلاتك، ثم اضغط **تفعيل المراقبة**.

**التحكم بالتنزيل من الهاتف** يحتاج خطوة إعداد واحدة، ومن المفيد معرفة سببها: لا يوفّر Steam أي أمر
أو مفتاح تسجيل أو رابط `steam://` لإيقاف التنزيل. لكنه عميل مبني على Chromium، وصفحة التنزيلات فيه
تُشغّل المُنزِّل عبر كائن `SteamClient`، ويمكن مطالبة Chromium بإتاحة ذلك الكائن محلياً. فيقول
SteamFinish للمُنزِّل ما يقوله زر الإيقاف داخل Steam تماماً — ولكي يُسمح له بذلك يحتاج Steam إلى ملف
علامة داخل مجلد تنصيبه وإعادة تشغيل واحدة:

1. **الإعدادات ← تيليجرام ← تفعيل التحكم بالتنزيل** — ينشئ الملف، ولا يحتاج صلاحيات مدير.
2. **أعد تشغيل Steam مرة واحدة** — لا يُقرأ الملف إلا عند بدء التشغيل.

بعدها يخبرك الزر إن كانت القناة مفتوحة فعلاً، وعلى أي منفذ.

**بخصوص المنفذ**: يستخدم Steam المنفذ 8080 افتراضياً، وهو منفذ مزدحم (Docker وWSL وأغلب خوادم
التطوير تريده). ويختار Steam منفذه عند بدء التشغيل ولا يعيد النظر فيه، فإن كان مشغولاً وقتها تبقى
القناة مغلقة. والبرنامج يعالج الأمرين:

- **العثور عليه**: لا يفترض البرنامج المنفذ بل يكتشفه — يسأل ويندوز عن المنافذ التي يستمع عليها
  `steam.exe` ويجرّبها، فيجد Steam المشغّل بـ `-devtools-port` بلا أن يخبره أحد، ويتذكّر المنفذ الناجح.
- **تفريغه**: عند تعذّر الوصول، يغلق زر **أعد تشغيل Steam على منفذ فارغ** برنامج Steam بأمره الخاص
  ويعيد تشغيله على منفذ لا يستخدمه أحد. التنزيل يكمل من حيث توقف، ولا يحدث هذا تلقائياً أبداً.

ولجعله دائمياً، أضف `-devtools-port 8123` إلى اختصار Steam عندك، وسيجده البرنامج وحده.

**يعمل مع Steam فقط** — تطبيق Xbox لا يوفّر أي وسيلة تحكم بالتنزيل.

**اسم الحاسبة** يظهر أعلى كل رسالة، ويُؤخذ من ويندوز ويمكن تعديله من الإعدادات. وإذا كانت أكثر من
حاسبة تراسل المحادثة نفسها (لكل واحدة بوت خاص)، أضف الاسم بعد الأمر لتخصّها: `/pause laptop`.

والتنزيل الموقوف مؤقتاً لا يُعدّ منتهياً، فالإيقاف من الهاتف لن يؤدي إلى إطفاء الحاسبة.

</div>

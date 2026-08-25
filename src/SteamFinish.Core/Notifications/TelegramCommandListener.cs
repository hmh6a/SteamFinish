using SteamFinish.Core.Control;
using SteamFinish.Core.Logging;

namespace SteamFinish.Core.Notifications;

/// <summary>
/// Listens for what the user types to the bot — <c>/pause</c>, <c>/resume</c>, <c>/status</c> — and
/// for the buttons that do the same thing, then hands the work to whoever wired up
/// <see cref="Control"/>.
///
/// Telegram will not let one bot long-poll twice at the same time, and an offset confirmed by one
/// poller throws away updates the other has not read yet. So this class is the only poller that
/// runs during normal operation, and the two flows that need the connection to themselves — pairing
/// a new chat, and the countdown prompt — take it with <see cref="Suspend"/>.
/// </summary>
public sealed class TelegramCommandListener(
    Func<TelegramOptions> options,
    ITelegramConversation conversation,
    ILog? log = null) : IDisposable
{
    /// <summary>How long a single long poll asks Telegram to hold the connection open.</summary>
    private static readonly TimeSpan PollHold = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(2);

    private readonly ILog _log = log ?? NullLog.Instance;
    private readonly Lock _sync = new();

    private CancellationTokenSource? _running;
    private int _suspensions;
    private bool _disposed;

    /// <summary>How long to wait after the first failed poll; it doubles from there.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Carries out a pause or resume. Left unset, the commands report that they cannot run.</summary>
    public Func<DownloadCommand, uint?, CancellationToken, Task<ControlResult>>? Control { get; set; }

    /// <summary>Builds the HTML for <c>/status</c>.</summary>
    public Func<string>? Status { get; set; }

    /// <summary>The download at the front of the queue, which a resume needs to nudge by name.</summary>
    public Func<uint?>? CurrentAppId { get; set; }

    public bool IsListening => _running is not null;

    /// <summary>
    /// Starts or stops the loop to match the settings. Safe to call as often as the settings change.
    /// </summary>
    public void Sync()
    {
        var settings = options();
        if (!_disposed && _suspensions == 0 && settings.IsUsable && settings.RemoteCommands)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>
    /// Hands the connection to another flow until the returned handle is disposed. Commands sent in
    /// the meantime are skipped rather than queued: a pause that arrives while the countdown message
    /// is on screen is about a download that has already finished.
    /// </summary>
    public IDisposable Suspend()
    {
        lock (_sync)
        {
            _suspensions++;
        }

        Stop();
        return new Resumption(this);
    }

    private void Release()
    {
        lock (_sync)
        {
            if (_suspensions > 0)
            {
                _suspensions--;
            }
        }

        Sync();
    }

    private void Start()
    {
        if (_running is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        _log.Info("Listening for Telegram commands.");
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Stopped, suspended, or the app is closing.
                }
                catch (Exception e)
                {
                    _log.Error("The Telegram command listener stopped.", e);
                }
            },
            CancellationToken.None);
    }

    public void Stop()
    {
        var running = _running;
        _running = null;
        if (running is null)
        {
            return;
        }

        running.Cancel();
        running.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        long? offset = null;
        var baselined = false;
        var retry = RetryDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            // A token edited in Settings makes every poll fail; pick the new one up rather than
            // spinning on the old one.
            var token = options().BotToken;

            var poll = await conversation
                .PollAsync(
                    token,
                    offset,
                    baselined ? (int)PollHold.TotalSeconds : 0,
                    TelegramUpdateReader.CommandUpdatesJson,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!poll.Ok)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _log.Warn($"Telegram commands are not being received: {poll.Description}");

                try
                {
                    await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // Back off rather than hammer a bot token that has been revoked. The baseline is
                // still owed, so a listener that starts up offline does not obey a stale backlog
                // the moment the connection comes back.
                retry = retry < LongestRetry ? retry + retry : LongestRetry;
                continue;
            }

            retry = RetryDelay;

            // Advance before handling: an update that throws has still been seen, and must not come
            // round again on the next poll.
            if (poll.Updates.Count > 0)
            {
                offset = poll.Updates[^1].UpdateId + 1;
            }

            if (!baselined)
            {
                // Anything already waiting on the server was sent while nobody was listening.
                // Acknowledging it without acting means a "/pause" from last night cannot stop
                // tonight's download.
                baselined = true;
                continue;
            }

            foreach (var update in poll.Updates)
            {
                try
                {
                    await HandleAsync(update, token, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // One bad update must not take the listener down for the rest of the session.
                    _log.Error("A Telegram command failed.", e);
                }
            }
        }
    }

    private async Task HandleAsync(TelegramUpdate update, string token, CancellationToken cancellationToken)
    {
        if (update.Callback is { } callback)
        {
            await HandleButtonAsync(callback, token, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (update.Message is not { } message || BotCommands.Parse(message.Text) is not { } command)
        {
            return;
        }

        if (!IsAllowed(message.ChatId))
        {
            // Someone who found the bot but was never paired with it. Answering would only confirm
            // that the bot is live, so it is left unanswered.
            _log.Warn($"Ignored a Telegram command from an unpaired chat ({message.ChatId}).");
            return;
        }

        var language = options().Language;

        switch (command)
        {
            case BotCommand.Status:
                await ReplyAsync(token, message.ChatId, StatusHtml(language), language, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case BotCommand.Help:
                await ReplyAsync(token, message.ChatId, NotificationMessages.Help(language), language, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                var wanted = command == BotCommand.Pause ? DownloadCommand.Pause : DownloadCommand.Resume;
                var result = await ApplyAsync(wanted, cancellationToken).ConfigureAwait(false);
                var html = result.Success
                    ? NotificationMessages.ControlDone(language, wanted)
                    : NotificationMessages.ControlFailed(language, wanted, result.Outcome);

                await ReplyAsync(token, message.ChatId, html, language, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleButtonAsync(
        TelegramCallback callback,
        string token,
        CancellationToken cancellationToken)
    {
        if (DownloadButtons.Parse(callback.Data) is not { } wanted)
        {
            // A countdown button, or one from a version that had different buttons. Clearing the
            // spinner keeps the chat from looking stuck.
            await conversation.AnswerCallbackAsync(token, callback.Id, string.Empty, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!IsAllowed(callback.ChatId))
        {
            await conversation.AnswerCallbackAsync(token, callback.Id, string.Empty, cancellationToken)
                .ConfigureAwait(false);
            _log.Warn($"Ignored a Telegram button press from an unpaired chat ({callback.ChatId}).");
            return;
        }

        var language = options().Language;
        var result = await ApplyAsync(wanted, cancellationToken).ConfigureAwait(false);

        await conversation.AnswerCallbackAsync(
            token,
            callback.Id,
            NotificationMessages.ControlToast(language, wanted, result.Success),
            cancellationToken).ConfigureAwait(false);

        var html = result.Success
            ? NotificationMessages.ControlDone(language, wanted)
            : NotificationMessages.ControlFailed(language, wanted, result.Outcome);

        await ReplyAsync(token, callback.ChatId, html, language, cancellationToken).ConfigureAwait(false);
    }

    private Task<ControlResult> ApplyAsync(DownloadCommand command, CancellationToken cancellationToken)
    {
        if (Control is not { } control)
        {
            return Task.FromResult(ControlResult.Fail(ControlOutcome.BridgeDisabled));
        }

        return control(command, CurrentAppId?.Invoke(), cancellationToken);
    }

    private string StatusHtml(MessageLanguage language) =>
        Status?.Invoke() ?? NotificationMessages.Status(language, null, 0, null, false, default);

    /// <summary>Every reply carries the buttons, so the next pause is one tap rather than a command.</summary>
    private Task ReplyAsync(
        string token,
        string chatId,
        string html,
        MessageLanguage language,
        CancellationToken cancellationToken) =>
        conversation.ReplyAsync(token, chatId, html, TelegramKeyboard.PauseResume(language), cancellationToken);

    /// <summary>
    /// Only the chats the user paired may command the PC. The bot token alone is not enough: anyone
    /// who found the bot could otherwise stop a download.
    /// </summary>
    private bool IsAllowed(string chatId) =>
        chatId.Length > 0
        && options().ChatIds.Any(id => string.Equals(id.Trim(), chatId, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    private sealed class Resumption(TelegramCommandListener listener) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            listener.Release();
        }
    }
}

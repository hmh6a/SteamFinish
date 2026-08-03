using SteamFinish.Core.Logging;
using SteamFinish.Core.Monitoring;
using SteamFinish.Core.Power;
using SteamFinish.Core.Steam;

namespace SteamFinish.Core.Notifications;

/// <summary>
/// Decides which Telegram messages to send and when. Progress steps are tracked per app, and the
/// state is always kept up to date — even while Telegram is switched off — so turning it on halfway
/// through a download does not produce a burst of catch-up messages.
/// </summary>
public sealed class TelegramNotifier(
    Func<TelegramOptions> options,
    ITelegramSender client,
    ILog? log = null,
    ITelegramRemoteControl? remote = null)
{
    private readonly ILog _log = log ?? NullLog.Instance;
    private readonly Dictionary<uint, int> _lastStep = [];

    private CancellationTokenSource? _prompt;
    private IReadOnlyList<PromptTarget> _promptTargets = [];
    private bool _suppressCancelMessage;

    /// <summary>App ids seen in the previous snapshot; <c>null</c> until the first one arrives.</summary>
    private HashSet<uint>? _known;

    /// <summary>Raised on a background thread when a send fails.</summary>
    public event Action<string>? SendFailed;

    public void Reset()
    {
        _lastStep.Clear();
        _known = null;
    }

    public void OnSnapshot(DownloadSnapshot snapshot, TransferMeter meter)
    {
        if (!snapshot.IsReliable)
        {
            return;
        }

        var settings = options();
        var pipeline = snapshot.Pipeline;
        var currentIds = pipeline.Select(app => app.AppId).ToHashSet();

        if (_known is null)
        {
            // First look: record where everything stands without announcing anything.
            _known = currentIds;
            foreach (var known in pipeline)
            {
                _lastStep[known.AppId] = StepOf(known, settings);
            }

            return;
        }

        // Steam downloads one game at a time, so only the live one is reported on.
        if (snapshot.Headline is { IsPaused: false } app)
        {
            Report(app, pipeline.Count - 1);
        }

        foreach (var goneId in _known.Where(id => !currentIds.Contains(id)).ToList())
        {
            _lastStep.Remove(goneId);
        }

        _known = currentIds;
        return;

        void Report(AppActivity app, int others)
        {
            var settings = options();

            // A game that was not in the previous snapshot has only just been queued up.
            if (!_known!.Contains(app.AppId))
            {
                _lastStep[app.AppId] = StepOf(app, settings);
                if (settings.NotifyOnStart)
                {
                    Send(NotificationMessages.DownloadStarted(settings.Language, app, others));
                }

                return;
            }

            if (!settings.NotifyOnProgress)
            {
                _lastStep[app.AppId] = StepOf(app, settings);
                return;
            }

            var reached = StepOf(app, settings);
            if (!_lastStep.TryGetValue(app.AppId, out var previous))
            {
                _lastStep[app.AppId] = reached;
                return;
            }

            if (reached <= previous)
            {
                return;
            }

            _lastStep[app.AppId] = reached;
            var step = Math.Clamp(
                settings.ProgressStepPercent,
                TelegramOptions.MinProgressStep,
                TelegramOptions.MaxProgressStep);

            Send(NotificationMessages.Progress(
                settings.Language,
                app,
                reached * step,
                meter.NetworkBytesPerSecond,
                meter.Eta,
                others));
        }
    }

    /// <summary>Raised on a background thread when a countdown button is pressed on the phone.</summary>
    public event Action<RemoteDecision>? RemoteDecisionMade;

    public void OnCountdownStarted(DownloadSummary? summary, PowerAction action, int countdownSeconds)
    {
        var settings = options().Clone();
        if (!settings.NotifyOnFinish)
        {
            return;
        }

        var html = summary is null
            ? NotificationMessages.FinishedWithoutDetails(settings.Language, action, countdownSeconds)
            : NotificationMessages.Finished(settings.Language, summary, action, countdownSeconds);

        if (remote is null || !settings.RemoteButtons || !settings.IsUsable)
        {
            Send(html);
            return;
        }

        _ = RunPromptAsync(settings, html, action);
    }

    public void OnCountdownCancelled(PowerAction action, CountdownCancelReason reason)
    {
        StopPrompt();

        if (_suppressCancelMessage)
        {
            // The button press already rewrote the message; a second one would just be noise.
            _suppressCancelMessage = false;
            return;
        }

        var settings = options();
        if (settings.NotifyOnCancel)
        {
            Send(NotificationMessages.Cancelled(settings.Language, action, reason));
        }
    }

    /// <summary>
    /// Called once the countdown is settled at the PC rather than from the phone, so the buttons are
    /// removed and the chat is left showing what actually happened.
    /// </summary>
    public void OnResolvedLocally(PowerAction action, bool executed)
    {
        StopPrompt();

        var targets = _promptTargets;
        _promptTargets = [];
        if (targets.Count == 0 || remote is null)
        {
            return;
        }

        var settings = options().Clone();
        _ = remote.EditAllAsync(
            settings.BotToken,
            targets,
            NotificationMessages.DecidedAtThePc(settings.Language, action, executed));
    }

    /// <summary>
    /// Posts the countdown message with its buttons and waits for a press. The wait ends by itself
    /// when the countdown is settled at the PC, which cancels the token.
    /// </summary>
    private async Task RunPromptAsync(TelegramOptions settings, string html, PowerAction action)
    {
        StopPrompt();
        var cancellation = new CancellationTokenSource();
        _prompt = cancellation;
        var token = RemoteControl.NewToken();

        try
        {
            var targets = await remote!
                .SendWithButtonsAsync(
                    settings,
                    html,
                    NotificationMessages.ButtonShutdownNow(settings.Language, action),
                    NotificationMessages.ButtonSkip(settings.Language),
                    token,
                    cancellation.Token)
                .ConfigureAwait(false);

            if (targets.Count == 0)
            {
                _log.Warn("The countdown message could not be delivered, so no buttons are live.");
                return;
            }

            _promptTargets = targets;

            var pressed = await remote
                .WaitForDecisionAsync(settings.BotToken, token, cancellation.Token)
                .ConfigureAwait(false);

            if (pressed is not { } outcome)
            {
                return;
            }

            _log.Info($"Telegram button pressed: {outcome.Decision}.");

            await remote.AnswerCallbackAsync(
                settings.BotToken,
                outcome.Callback.Id,
                NotificationMessages.Toast(settings.Language, outcome.Decision)).ConfigureAwait(false);

            await remote.EditAllAsync(
                settings.BotToken,
                targets,
                NotificationMessages.DecisionTaken(
                    settings.Language,
                    outcome.Decision,
                    action,
                    outcome.Callback.From)).ConfigureAwait(false);

            _promptTargets = [];
            _suppressCancelMessage = outcome.Decision == RemoteDecision.Skip;
            RemoteDecisionMade?.Invoke(outcome.Decision);
        }
        catch (OperationCanceledException)
        {
            // Settled at the PC while we were waiting.
        }
        catch (Exception e)
        {
            _log.Error("The Telegram countdown prompt failed.", e);
        }
    }

    private void StopPrompt()
    {
        var prompt = _prompt;
        _prompt = null;
        prompt?.Cancel();
        prompt?.Dispose();
    }

    /// <summary>Verifies the token and posts a test message to every configured chat.</summary>
    public Task<TelegramResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var settings = options().Clone();

        // The test must work before the feature is switched on, otherwise it could never be set up.
        settings.Enabled = true;
        return client.TestAsync(settings, NotificationMessages.Test(settings.Language), cancellationToken);
    }

    /// <summary>Which progress step the app currently sits in.</summary>
    private static int StepOf(AppActivity app, TelegramOptions settings)
    {
        var step = Math.Clamp(settings.ProgressStepPercent, TelegramOptions.MinProgressStep, TelegramOptions.MaxProgressStep);
        var percent = (int)Math.Floor(Math.Clamp(app.Progress ?? 0, 0, 1) * 100);
        return percent / step;
    }

    private void Send(string html)
    {
        var settings = options().Clone();
        if (!settings.IsUsable)
        {
            return;
        }

        // The send is started here and completed in the background: a slow or unreachable Telegram
        // must never hold up monitoring, and the outcome is only reported through SendFailed.
        try
        {
            _ = client.SendAsync(settings, html).ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        _log.Error("Telegram notification threw.", task.Exception);
                        SendFailed?.Invoke(task.Exception?.GetBaseException().Message ?? "unknown error");
                    }
                    else if (task.IsCompletedSuccessfully && !task.Result.Success)
                    {
                        _log.Warn($"Telegram notification failed: {task.Result.Message}");
                        SendFailed?.Invoke(task.Result.Message);
                    }
                },
                TaskScheduler.Default);
        }
        catch (Exception e)
        {
            _log.Error("Telegram notification could not be started.", e);
            SendFailed?.Invoke(e.Message);
        }
    }
}

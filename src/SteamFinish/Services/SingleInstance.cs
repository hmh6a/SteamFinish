namespace SteamFinish.Services;

/// <summary>
/// Keeps one SteamFinish per user session. A second launch signals the running instance to show its
/// window and then exits, which matters because the app normally lives in the tray.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SteamFinish.Instance.9C4F1B2E";
    private const string EventName = @"Local\SteamFinish.Activate.9C4F1B2E";

    private readonly Mutex _mutex;
    private EventWaitHandle? _activation;
    private CancellationTokenSource? _listener;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Returns the owning instance, or <c>null</c> when another copy is already running (in which
    /// case that copy has been asked to come to the foreground).
    /// </summary>
    public static SingleInstance? Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isOwner);
        if (isOwner)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        SignalRunningInstance();
        return null;
    }

    /// <summary>Invokes <paramref name="onActivate"/> whenever another launch asks us to show up.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _listener = new CancellationTokenSource();
        var token = _listener.Token;
        var handle = _activation;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (handle.WaitOne(500) && !token.IsCancellationRequested)
                {
                    onActivate();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SteamFinish.Activation",
        };

        thread.Start();
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            // The other instance is starting up or shutting down; nothing useful to do.
        }
    }

    public void Dispose()
    {
        _listener?.Cancel();
        _listener?.Dispose();
        _activation?.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not held; releasing is best effort.
        }

        _mutex.Dispose();
    }
}

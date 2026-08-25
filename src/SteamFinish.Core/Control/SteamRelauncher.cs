using System.Diagnostics;
using System.Runtime.Versioning;
using SteamFinish.Core.Logging;

namespace SteamFinish.Core.Control;

public enum RelaunchOutcome
{
    /// <summary>Steam was restarted and is listening on <see cref="RelaunchResult.Port"/>.</summary>
    Done,

    SteamNotFound,

    /// <summary>Steam would not close within the time allowed, so it was left alone.</summary>
    WouldNotClose,

    /// <summary>Steam closed but would not start again.</summary>
    WouldNotStart,
}

public sealed record RelaunchResult(RelaunchOutcome Outcome, int Port = 0, string? Detail = null)
{
    public bool Success => Outcome == RelaunchOutcome.Done;
}

/// <summary>
/// Restarts Steam with its control channel on a port that is actually free.
///
/// Steam picks the port when it starts and never afterwards, so a client that came up while
/// something else held 8080 has no channel until it is restarted. Rather than telling the user to
/// go and free a port, this hands Steam <c>-devtools-port</c> pointing at one nothing is using.
///
/// This closes the user's Steam, so it is never done on its own — only when the button that says so
/// is pressed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamRelauncher(ILog? log = null)
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILog _log = log ?? NullLog.Instance;

    /// <summary>
    /// Asks Steam to close, waits for it to go, and starts it again with the control channel on a
    /// free port. Returns the port it was told to use.
    /// </summary>
    public async Task<RelaunchResult> RestartWithControlAsync(
        string? steamPath,
        CancellationToken cancellationToken = default)
    {
        if (steamPath is null || !File.Exists(Path.Combine(steamPath, "steam.exe")))
        {
            return new RelaunchResult(RelaunchOutcome.SteamNotFound);
        }

        var executable = Path.Combine(steamPath, "steam.exe");
        var port = SteamPorts.FindFreePort();

        if (SteamCefBridge.IsSteamRunning())
        {
            _log.Info("Asking Steam to close so its control channel can be opened.");

            // -shutdown is Steam's own "quit" and lets it finish writing its manifests, which
            // killing the process would not.
            if (!Start(executable, "-shutdown", out var shutdownError))
            {
                return new RelaunchResult(RelaunchOutcome.WouldNotClose, Detail: shutdownError);
            }

            if (!await WaitForExitAsync(cancellationToken).ConfigureAwait(false))
            {
                return new RelaunchResult(
                    RelaunchOutcome.WouldNotClose,
                    Detail: $"Steam was still running after {ShutdownTimeout.TotalSeconds:0} seconds.");
            }
        }

        if (!Start(executable, $"-cef-enable-debugging -devtools-port {port}", out var startError))
        {
            return new RelaunchResult(RelaunchOutcome.WouldNotStart, Detail: startError);
        }

        _log.Info($"Steam restarted with its control channel on port {port}.");
        return new RelaunchResult(RelaunchOutcome.Done, port);
    }

    private async Task<bool> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ShutdownTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!SteamCefBridge.IsSteamRunning())
            {
                // The process is gone, but its ports linger for a moment.
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private bool Start(string executable, string arguments, out string? error)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
            });

            error = null;
            return true;
        }
        catch (Exception e)
        {
            _log.Warn($"Could not run '{executable} {arguments}': {e.Message}");
            error = e.Message;
            return false;
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SteamFinish.Core.Logging;

namespace SteamFinish.Core.Power;

/// <summary>
/// Carries out the power action. Shutdown and restart go through <c>shutdown.exe</c>, which handles
/// the privilege elevation itself; sleep and hibernate use the power management API.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPowerController(ILog? log = null) : IPowerController
{
    private readonly ILog _log = log ?? NullLog.Instance;

    public void Execute(PowerAction action, bool force)
    {
        _log.Info($"Executing power action {action} (force={force}).");

        switch (action)
        {
            case PowerAction.Shutdown:
                RunShutdown(force ? "/s /t 0 /f" : "/s /t 0");
                break;

            case PowerAction.Restart:
                RunShutdown(force ? "/r /t 0 /f" : "/r /t 0");
                break;

            case PowerAction.Sleep:
                Suspend(hibernate: false, force);
                break;

            case PowerAction.Hibernate:
                if (!IsPwrHibernateAllowed())
                {
                    throw new PowerActionException(
                        "Hibernate is turned off on this PC. Enable it with 'powercfg /hibernate on' or pick another action.");
                }

                Suspend(hibernate: true, force);
                break;

            default:
                throw new PowerActionException($"Unsupported power action '{action}'.");
        }
    }

    public void AbortPendingShutdown()
    {
        try
        {
            // Exit code 1116 simply means no shutdown was in progress, which is the normal case.
            var exitCode = RunShutdownCore("/a", out _);
            if (exitCode == 0)
            {
                _log.Info("Aborted a pending system shutdown.");
            }
        }
        catch (Exception e)
        {
            _log.Warn($"Could not run 'shutdown /a': {e.Message}");
        }
    }

    private void RunShutdown(string arguments)
    {
        var exitCode = RunShutdownCore(arguments, out var error);
        if (exitCode != 0)
        {
            throw new PowerActionException(
                $"'shutdown {arguments}' failed with exit code {exitCode}."
                + (string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error.Trim()}"));
        }
    }

    private static int RunShutdownCore(string arguments, out string error)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new PowerActionException("Could not start shutdown.exe.");
            error = process.StandardError.ReadToEnd();
            _ = process.StandardOutput.ReadToEnd();

            // The command returns as soon as the request is queued; the box goes down afterwards.
            return process.WaitForExit(TimeSpan.FromSeconds(15)) ? process.ExitCode : 0;
        }
        catch (Exception e) when (e is not PowerActionException)
        {
            throw new PowerActionException($"Could not run 'shutdown {arguments}': {e.Message}", e);
        }
    }

    private void Suspend(bool hibernate, bool force)
    {
        // SetSuspendState blocks until the machine wakes up again, so it must not run on the UI thread.
        var thread = new Thread(() =>
        {
            if (!SetSuspendState(hibernate, force, disableWakeEvent: false))
            {
                var code = Marshal.GetLastWin32Error();
                _log.Error($"SetSuspendState(hibernate: {hibernate}) failed with Win32 error {code}.");
            }
        })
        {
            IsBackground = true,
            Name = "SteamFinish.Suspend",
        };

        thread.Start();
    }

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsPwrHibernateAllowed();
}

namespace SteamFinish.Core.Power;

public enum PowerAction
{
    Shutdown,
    Sleep,
    Hibernate,
    Restart,
}

public interface IPowerController
{
    /// <summary>Runs the action. Throws <see cref="PowerActionException"/> when it cannot be started.</summary>
    void Execute(PowerAction action, bool force);

    /// <summary>Aborts a system shutdown that is already counting down. No-op when none is pending.</summary>
    void AbortPendingShutdown();
}

public sealed class PowerActionException(string message, Exception? inner = null)
    : Exception(message, inner);

namespace Huddle;

/// <summary>What the command loop does when its input ends or is interrupted.</summary>
public enum ShutdownAction
{
    /// <summary>Leave every session running and exit huddle, exactly as `quit` does.</summary>
    Detach,
    /// <summary>Terminate every running session, then exit.</summary>
    StopAll,
}

/// <summary>
/// The decision behind an abnormal exit, kept out of the loop so it can be tested.
///
/// 2026-09-06 incident: huddle was relaunched by hand after a rebuild, inherited a stdin
/// that was already at end-of-file, and one second after recovering seven live sessions it
/// terminated all seven. Nobody typed anything. Two mistakes compounded:
///
///   1. End-of-input was routed to the same branch as Ctrl+C, and that branch stops every
///      session. But EOF is the ABSENCE of a keystroke. It says nobody is at the console,
///      which is an argument for letting go of the fleet, not for killing it.
///   2. The confirmation prompt that was supposed to make teardown deliberate asked the
///      same dead stdin, read null, and counted null as "yes".
///
/// Note the asymmetry that made it so surprising: `quit` deliberately leaves sessions
/// running. The destructive path was the one that fired when nobody was there to stop it.
/// </summary>
public static class ShutdownPolicy
{
    /// <summary>
    /// Input ended: no more commands can arrive, whatever the reason (a closed pipe, a
    /// redirected launch, a console that went away). huddle can no longer be operated, so
    /// it lets go. Sessions keep running and are persisted for `recover`, same as `quit`.
    /// Terminating them would be acting on an instruction nobody gave.
    /// </summary>
    public static ShutdownAction OnEndOfInput() => ShutdownAction.Detach;

    /// <summary>
    /// The answer to "terminate them in progress? (y/N)". Only an explicit yes is consent.
    ///
    /// A null answer means the prompt could not be read - the operator did not decline,
    /// they were never asked. That is the one case this used to treat as agreement.
    /// With no sessions running there is nothing to consent TO, so it is vacuously true.
    /// </summary>
    public static bool IsConsent(string? answer, int liveSessions)
    {
        if (liveSessions == 0) return true;
        if (answer == null) return false;
        answer = answer.Trim();
        return answer.Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

namespace Huddle;

/// <summary>
/// One place where a verb handler is allowed to throw. huddle is a long-running
/// orchestrator with child sessions attached to it, so an unhandled exception out of a
/// handler does not cost the operator a command — it costs them the console, and the
/// fleet's supervisor with it.
///
/// S2 (review 2026-08-22): `reload` gained a pre-validation guard that caught only
/// SettingsException. A trailing comma in huddle.json raises JsonException, which escaped
/// a bare `ui.HandleCommand(line)` in Program.cs and killed huddle — the exact outcome the
/// guard was added to prevent. The specific catch is the fix for `reload`; this is the
/// backstop that makes it true for EVERY verb, including ones not written yet.
/// </summary>
public static class CommandGuard
{
    /// <summary>Run one console command. Never throws. An exception is logged, named by
    /// type so the operator can report it, and reported as <see cref="CommandResult.Continue"/>
    /// — the loop survives. Quit and Shutdown pass through untouched: swallowing a fault
    /// must never turn into swallowing the operator's exit.</summary>
    public static CommandResult Run(Func<CommandResult> command, Action<string> log)
    {
        try
        {
            return command();
        }
        catch (Exception ex)
        {
            log($"Command failed — {ex.GetType().Name}: {ex.Message}");
            log("huddle is still running. If this repeats, the exception type above is the thing to report.");
            return CommandResult.Continue;
        }
    }
}

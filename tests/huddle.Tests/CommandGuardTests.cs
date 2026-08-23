using Huddle;
namespace Huddle.Tests;

// S2 (review 2026-08-22): a verb handler that throws must never unwind Main. Before this
// guard, Program.cs called ui.HandleCommand(line) bare, so a JsonException out of
// `reload` killed huddle with child sessions attached.
public class CommandGuardTests
{
    [Fact]
    public void S2_a_throwing_command_does_not_propagate_and_keeps_the_loop_alive()
    {
        var logged = new List<string>();
        var result = CommandGuard.Run(() => throw new InvalidOperationException("boom"), logged.Add);
        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains(logged, l => l.Contains("boom"));
    }

    [Fact]
    public void S2_the_exception_type_is_named_so_the_operator_can_report_it()
    {
        var logged = new List<string>();
        CommandGuard.Run(() => throw new System.Text.Json.JsonException("trailing comma"), logged.Add);
        Assert.Contains(logged, l => l.Contains("JsonException"));
    }

    [Fact]
    public void S2_a_normal_result_passes_straight_through()
    {
        var logged = new List<string>();
        Assert.Equal(CommandResult.Shutdown, CommandGuard.Run(() => CommandResult.Shutdown, logged.Add));
        Assert.Equal(CommandResult.Quit, CommandGuard.Run(() => CommandResult.Quit, logged.Add));
        Assert.Empty(logged);
    }

    // Quit and Shutdown are how the operator leaves; swallowing an exception must never
    // turn into swallowing the exit itself.
    [Fact]
    public void S2_guard_never_invents_an_exit()
    {
        var logged = new List<string>();
        Assert.Equal(CommandResult.Continue, CommandGuard.Run(() => throw new Exception("x"), logged.Add));
    }
}

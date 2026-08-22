using Huddle;
namespace Huddle.Tests;

/// <summary>
/// Regression for the 2026-08-09..22 dispatch black hole: a prompt carrying newlines was
/// cut at the first one by cmd.exe, so every dispatched session got the shell-rules
/// preamble and no task. The flattened prompt must carry the whole task on one line.
/// </summary>
public class PromptCommandLineTests
{
    [Fact]
    public void Preamble_plus_task_survives_as_one_line()
    {
        var prompt = SessionManager.ShellDisciplinePreamble + "Implement the plan.\n\nREAD FIRST: docs/x.md\nThen commit.";
        var flat = SessionManager.FlattenForCommandLine(prompt);
        Assert.DoesNotContain('\n', flat);
        Assert.DoesNotContain('\r', flat);
        Assert.Contains("Implement the plan.", flat);
        Assert.Contains("READ FIRST: docs/x.md", flat);
        Assert.Contains("Then commit.", flat);
        Assert.Contains("SHELL RULES", flat);
    }

    [Fact]
    public void Paragraph_breaks_are_visible_and_crlf_is_normalised()
    {
        Assert.Equal("a | b c", SessionManager.FlattenForCommandLine("a\r\n\r\n\r\nb\r\nc"));
    }

    [Fact]
    public void Single_line_prompt_is_unchanged()
    {
        Assert.Equal("fix the bug", SessionManager.FlattenForCommandLine("fix the bug"));
    }

    [Fact]
    public void Quotes_still_escape_after_flattening()
    {
        var flat = SessionManager.FlattenForCommandLine("say \"hi\"\nnow");
        Assert.Equal("say \\\"hi\\\" now", SessionManager.EscapeForCmdQuoted(flat));
    }
}

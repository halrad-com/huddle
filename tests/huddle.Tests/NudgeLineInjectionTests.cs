using System.Text.Json;
using Huddle;
namespace Huddle.Tests;

/// <summary>
/// The nudge line is now INJECTED into the recipient's console rather than only appended
/// to pending.txt, so it has to be safe to type. Two properties matter and neither did
/// before: it must be one line, and it must be bounded. Subjects are free text an agent
/// hand-writes.
/// </summary>
public class NudgeLineInjectionTests
{
    private static IpcMessage Mail(string from, string subject) => new()
    {
        From = from,
        Subject = subject,
        Type = "info",
        Body = JsonSerializer.SerializeToElement("x")
    };

    [Fact]
    public void The_line_names_the_sender_and_the_path()
    {
        var line = LedgerMailIngest.NudgeLine(
            Mail("myapp:architect", "Take it; here is the seam"),
            "ipc/otherapp_architect/inbox/001.json", null);

        Assert.Contains("myapp:architect", line);
        Assert.Contains("ipc/otherapp_architect/inbox/001.json", line);
    }

    [Fact]
    public void A_newline_in_the_subject_never_reaches_the_console()
    {
        // An embedded newline in injected text is an extra Enter: it would submit the
        // line early and drop the tail into the prompt as a second stray message.
        var line = LedgerMailIngest.NudgeLine(
            Mail("a:one", "first line\r\nsecond line\nthird"),
            "ipc/x/inbox/1.json", null);

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
    }

    [Fact]
    public void A_runaway_subject_is_bounded()
    {
        var line = LedgerMailIngest.NudgeLine(
            Mail("a:one", new string('x', 5000)), "ipc/x/inbox/1.json", null);

        // Bounded by the subject cap plus the fixed envelope (sender + path), not by the
        // subject's own length. The exact total does not matter; that it is not 5000 does.
        Assert.True(line.Length < 400, $"nudge line was {line.Length} chars");
        Assert.Contains("…", line);
        Assert.Contains("ipc/x/inbox/1.json", line);
    }

    [Fact]
    public void A_subject_at_the_cap_is_left_alone()
    {
        var exact = new string('y', LedgerMailIngest.MaxSubjectInNudge);
        var line = LedgerMailIngest.NudgeLine(Mail("a:one", exact), "ipc/x/inbox/1.json", null);

        Assert.Contains(exact, line);
        Assert.DoesNotContain("…", line);
    }
}

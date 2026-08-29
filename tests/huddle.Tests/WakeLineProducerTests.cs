using Huddle;
namespace Huddle.Tests;

/// <summary>
/// The regression guard for the bug that started this: a wake that names nobody.
///
/// <para>aaf0814 moved the mail line out of the injected keystroke into pending.txt for
/// the hook to fold in. 6d3eb25 then found an idle session never reaches a turn boundary
/// and added a bare submit for the fold to land on — MailWake.WakeLine, "[huddle] you have
/// mail". Both commits were right about their own problem, and together they meant that
/// whenever the fold did not happen the recipient was handed a ping with no sender, no
/// subject and no path. Nothing failed. Nothing could: the choice lived in a lambda inside
/// Program.Main, which no test can reach.</para>
///
/// <para>So the guard is structural. PendingWake.LineFor is the single producer of injected
/// wake text, and this asserts that no inject site bypasses it for the bare carrier. It
/// reads the source because that is where the mistake is made — a behavioural test cannot
/// see a call site, and this exact regression shipped through a suite that was green.</para>
/// </summary>
public class WakeLineProducerTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")))
                return d.FullName;
        throw new InvalidOperationException($"repo root not found above {dir}");
    }

    private static string ProgramSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Program.cs"));

    [Fact]
    public void No_inject_site_types_the_contentless_carrier()
    {
        var src = ProgramSource();

        // The carrier is a fallback inside PendingWake.LineFor, never a thing a caller
        // reaches for. If this fires, someone has put the naked ping back on a wake path
        // and agents are again being interrupted by a message that identifies nothing.
        Assert.DoesNotContain("Inject(pid, MailWake.WakeLine", src);
        Assert.DoesNotContain("MailWake.WakeLine, ConsoleUI.Log", src);
    }

    [Fact]
    public void Every_wake_injection_goes_through_the_single_producer()
    {
        var src = ProgramSource();

        var injects = src.Split("PromptInjector.Inject(").Length - 1;   // the call, not the prose or InjectInProcess
        var viaProducer = src.Split("PendingWake.LineFor").Length - 1;

        // Both wake paths — delivery and the retry tick's re-drive — and no third way in.
        Assert.Equal(2, injects);
        Assert.Equal(2, viaProducer);
    }

    [Fact]
    public void The_producer_names_the_sender_it_was_given()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-wakeguard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var p = Path.Combine(dir, "pending.txt");
            File.WriteAllLines(p, new[]
            {
                "[huddle mail from myapp:architect] here is the seam — read ipc/t/inbox/1.json"
            });

            var line = PendingWake.LineFor(p);
            Assert.Contains("myapp:architect", line);
            Assert.NotEqual(MailWake.WakeLine, line);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

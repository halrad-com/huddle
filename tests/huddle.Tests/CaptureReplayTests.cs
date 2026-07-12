using System.Text;
using Huddle;
namespace Huddle.Tests;

public class CaptureReplayTests
{
    [Fact]
    public void RunCommand_ParsesSummaryFromOutputToken()
    {
        // runner = cmd that copies a canned summary into {output}. A real PowerShell
        // runner writes this file as UTF-8 WITH BOM (PowerShell 5.1 Set-Content) — lock
        // that in so a future refactor to raw-byte parsing can't silently regress it.
        var fixture = Path.Combine(Path.GetTempPath(), $"sum-{Guid.NewGuid():N}.json");
        File.WriteAllText(fixture, """{"summary":{"total":5,"passed":4,"failed":1,"skipped":0}}""", new UTF8Encoding(true));
        var r = CaptureReplay.RunCommand($"cmd /c copy /y \"{fixture}\" \"{{output}}\"", null, _ => { });
        Assert.True(r.Ran); Assert.Equal(5, r.Total); Assert.Equal(1, r.Failed);
        File.Delete(fixture);
    }

    [Fact]
    public void RunCommand_NoSummaryWritten_ReportsRunnerFailure()
    {
        var r = CaptureReplay.RunCommand("cmd /c exit 2", null, _ => { });
        Assert.False(r.Ran); Assert.Contains("no report", r.Error);
    }

    [Fact]
    public void RunCommand_RunnerFailure_SurfacesStderrTailThroughLog()
    {
        // A runner that fails a prerequisite check typically explains why on stderr and
        // exits non-zero without writing a report. That explanation must reach the log
        // callback (previously it was drained and discarded) so operators see *why* the
        // runner failed instead of just "no report".
        var logLines = new List<string>();
        var r = CaptureReplay.RunCommand(
            "cmd /c \"echo PREREQ_MARKER 1>&2 & exit 2\"", null, logLines.Add);
        Assert.False(r.Ran);
        Assert.Contains(logLines, l => l.Contains("PREREQ_MARKER"));
    }
}

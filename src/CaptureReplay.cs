using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Huddle;

/// <summary>
/// Runs a repo's captured regression tests (MBXHVAL capture suites) against a running
/// instance via mbxhval, and reports pass/fail. The back half of the capture-to-test
/// loop: agents emit captures (committed in the target repo under
/// MBXHVAL/tests/suites/captures); `replay` runs them.
///
/// Invocation is pinned to mbxhval's real CLI (confirmed, not guessed):
///   validate --suite-dir &lt;dir&gt; --host &lt;h&gt; --port &lt;p&gt; --report json --output &lt;tmp&gt; --no-quality
/// Counts are read from the report FILE's ".summary" — NOT stdout (Spectre banners pollute
/// it) and NOT the root object. Connection is --host/--port (mbxhval has no --base-url).
/// </summary>
public static class CaptureReplay
{
    public record Result(bool Ran, int Total, int Passed, int Failed, int Skipped, string? Error);

    public static Result Run(string capturesDir, string mbxhvalPath, string host, int port, Action<string> log)
    {
        if (!Directory.Exists(capturesDir) || Directory.GetFiles(capturesDir, "*.yaml").Length == 0)
        {
            log($"replay: no capture suites at {capturesDir}");
            return new Result(false, 0, 0, 0, 0, "no captures");
        }
        if (string.IsNullOrWhiteSpace(mbxhvalPath) || !File.Exists(mbxhvalPath))
        {
            log($"replay: mbxhval not found at '{mbxhvalPath}' — set mbxhvalPath in huddle.json");
            return new Result(false, 0, 0, 0, 0, "mbxhval not found");
        }

        // A stale runner fails tests that use suite features it predates — and those
        // failures are indistinguishable from real server failures (2026-07-04: a
        // Jun 27 mbxhval without ${var:json} substitution 404'd 9 report-play/stream
        // captures and the DUT took the blame). Surface the binary's age every run.
        var built = File.GetLastWriteTime(mbxhvalPath);
        var age = DateTime.Now - built;
        var ageNote = age.TotalDays >= 2 ? $" — {(int)age.TotalDays} days old, rebuild if suites are newer" : "";
        log($"replay: mbxhval built {built:yyyy-MM-dd HH:mm}{ageNote}");

        var tmp = Path.Combine(Path.GetTempPath(), $"huddle-replay-{Guid.NewGuid():N}.json");
        var inner = $"validate --suite-dir \"{capturesDir}\" --host {host} --port {port} " +
                    $"--report json --output \"{tmp}\" --no-quality";

        // mbxhval ships as a .dll (run via `dotnet <dll>`) or a published .exe.
        string fileName, arguments;
        if (mbxhvalPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "dotnet";
            arguments = $"\"{mbxhvalPath}\" {inner}";
        }
        else
        {
            fileName = mbxhvalPath;
            arguments = inner;
        }

        log($"replay: {fileName} {arguments}");

        int exit;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            // Drain both pipes so the child can't block on a full buffer.
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            exit = p.ExitCode;
        }
        catch (Exception ex)
        {
            log($"replay: failed to launch mbxhval — {ex.Message}");
            return new Result(false, 0, 0, 0, 0, ex.Message);
        }

        return ParseSummary(tmp, exit, log, $"is the test instance at {host}:{port} running?");
    }

    /// <summary>
    /// Generic replay: run an arbitrary command that writes the summary JSON to the
    /// path substituted for the {output} token. Same Result semantics as Run().
    /// </summary>
    public static Result RunCommand(string command, string? workingDir, Action<string> log)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"huddle-replay-{Guid.NewGuid():N}.json");
        var resolved = command.Replace("{output}", tmp);
        log($"replay: {resolved}");
        int exit;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        bool reportExists;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + resolved,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!string.IsNullOrWhiteSpace(workingDir)) psi.WorkingDirectory = workingDir;
            using var p = Process.Start(psi)!;
            // Concurrent drain: reading stdout and stderr sequentially can deadlock if the
            // child fills the unread pipe's buffer while blocked writing to the other. Read
            // both asynchronously so neither can back the child up, and capture the text so
            // failures can be surfaced below instead of silently discarded.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);
            stdout.Append(stdoutTask.Result);
            stderr.Append(stderrTask.Result);
            p.WaitForExit();
            exit = p.ExitCode;
            reportExists = File.Exists(tmp);
        }
        catch (Exception ex)
        {
            log($"replay: failed to launch runner — {ex.Message}");
            return new Result(false, 0, 0, 0, 0, ex.Message);
        }

        var result = ParseSummary(tmp, exit, log, "runner failed or prerequisites missing?");
        if (exit != 0 || !reportExists)
            LogRunnerTail(stdout.ToString(), stderr.ToString(), log);
        return result;
    }

    // Lists the failing tests from the report's results[] array (MBXHVAL schema) so the
    // operator sees WHAT failed, not just a count. Defensive: a generic replayCommand
    // runner may write a summary-only report with no results[] — then this logs nothing.
    private static void LogFailures(JsonElement root, Action<string> log)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return;
        const int cap = 25;
        int shown = 0, failed = 0;
        foreach (var r in results.EnumerateArray())
        {
            bool passed = r.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True;
            bool skipped = r.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.True;
            if (passed || skipped) continue;
            failed++;
            if (shown >= cap) continue;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var id = S("testId"); if (id.Length == 0) id = S("testName");
            // statusCode is null for connection-level failures; TryGetInt32 throws
            // on non-Number kinds, so guard the kind first.
            var status = r.TryGetProperty("statusCode", out var sc)
                && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out var n) ? n.ToString() : "?";
            var error = S("error");
            if (error.Length > 90) error = error[..90] + "…";
            log($"replay:   FAIL {id} -> {status} {error}");
            shown++;
        }
        if (failed > shown)
            log($"replay:   … and {failed - shown} more failures (see report)");
    }

    // Surfaces the runner's own output when it failed to produce a usable report — the
    // failing-gate lines and prereq reasons a runner prints to stdout/stderr were
    // previously discarded entirely. Tail-only (last ~10 non-empty lines) to avoid
    // flooding the huddle log with a full build/test transcript.
    private static void LogRunnerTail(string stdout, string stderr, Action<string> log)
    {
        var lines = (stdout + "\n" + stderr)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        foreach (var line in lines.TakeLast(10))
            log($"replay[runner]: {line}");
    }

    // Shared by Run() (mbxhval) and RunCommand() (generic replayCommand): report-exists
    // check + parse + delete. `noReportHint` is the trailing question appended to the
    // "no report written" log line — it differs per caller (host:port reachability for
    // mbxhval, generic runner/prereqs for RunCommand) so the message stays actionable.
    // File.ReadAllText handles a UTF-8 BOM transparently (e.g. PowerShell 5.1 Set-Content
    // output from an external PowerShell runner) — do not switch to raw-byte parsing without checking.
    internal static Result ParseSummary(string tmp, int exit, Action<string> log, string noReportHint)
    {
        // No report file => the runner failed before writing one (mbxhval: connection
        // failure; generic command: crashed or prerequisites missing). Distinguish that
        // from real test failures, which always produce a report.
        if (!File.Exists(tmp))
        {
            log($"replay: no report written (exit {exit}) — {noReportHint}");
            return new Result(false, 0, 0, 0, 0, $"no report (exit {exit})");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(tmp));
            var summary = doc.RootElement.GetProperty("summary");
            int G(string k) => summary.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : 0;
            var result = new Result(true, G("total"), G("passed"), G("failed"), G("skipped"), null);
            log($"replay: total={result.Total} passed={result.Passed} failed={result.Failed} skipped={result.Skipped}");
            if (result.Failed > 0) LogFailures(doc.RootElement, log);
            return result;
        }
        catch (Exception ex)
        {
            log($"replay: could not parse report {tmp} — {ex.Message}");
            return new Result(false, 0, 0, 0, 0, ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}

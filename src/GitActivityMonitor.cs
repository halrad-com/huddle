using System.Text;

namespace Huddle;

/// <summary>
/// Surfaces git network activity in the huddle console so it is obvious when an
/// agent moved code — and, more importantly, so a session blocked on a GitHub /
/// Azure credential prompt (a pop-under the operator never sees) is announced
/// with the session and repo that is asking.
///
/// Two independent signals, one poll timer:
///
///  A. Auth requests. Each spawned session's git is pointed (via a
///     GIT_CONFIG_SYSTEM override injected at spawn — see
///     <see cref="WriteCredentialLoggerConfig"/>) at a logging credential helper
///     that runs BEFORE the real Git Credential Manager. When git asks for
///     credentials, that helper — `huddle --cred-log` (see
///     <see cref="RunCredLog"/>) — drops a small file into the auth dir, then
///     outputs nothing so GCM still performs the real prompt. We tail the drop
///     dir and log "session X is requesting host Y credentials", then delete the
///     drop. The helper only ever sees the request (host/protocol), never the
///     credential.
///
///  B. Movement. Each registered repo's remote-tracking reflog
///     (&lt;git-common-dir&gt;/logs/refs/remotes/**) records "update by push",
///     "fetch" and "pull" with the new sha. We tail those files and log the
///     completed transfer and its direction. The reflog is git's own record, so
///     this catches any actor — agent, operator, or huddle itself — and both
///     directions.
///
/// Poll, never FileSystemWatcher: huddle has been bitten repeatedly by dropped
/// FSW events on atomic writes, and both reflog appends and drop files are cheap
/// to stat on a timer.
/// </summary>
public sealed class GitActivityMonitor : IDisposable
{
    private readonly List<(string Name, string Root)> _repos;
    private readonly string _authDropDir;
    private readonly Action<string> _log;

    private System.Threading.Timer? _timer;
    private int _running; // 0/1 re-entrancy guard for the timer callback
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // Reflog file path -> byte length last seen. A file present at Start() is
    // seeded to its current length so huddle startup never replays history (and a
    // transfer that happened while huddle was down is silently missed, by design).
    // A file that FIRST appears after startup (a brand-new remote-tracking ref) is
    // absent here, so its content is emitted from offset 0 — that is genuinely new
    // activity, not history.
    private readonly Dictionary<string, long> _reflogOffsets = new();

    // Repo root -> resolved git common dir (where logs/refs/remotes lives), cached.
    // A null value means "already tried and this root is not a resolvable repo".
    private readonly Dictionary<string, string?> _commonDirs = new();

    public GitActivityMonitor(IEnumerable<(string Name, string Root)> repos, string authDropDir, Action<string> log)
    {
        _repos = repos.ToList();
        _authDropDir = authDropDir;
        _log = log;
    }

    public void Start()
    {
        try { Directory.CreateDirectory(_authDropDir); } catch { /* best effort */ }
        // Seed reflog offsets to current lengths so we only report transfers from
        // now on, not the whole recorded history.
        try { SeedReflogs(); } catch (Exception ex) { _log($"git-activity: seed failed: {ex.Message}"); }
        _timer = new System.Threading.Timer(Tick, null, Interval, Interval);
    }

    private void Tick(object? state)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return; // a tick is still running
        try
        {
            PollAuthDrops();
            PollReflogs();
        }
        catch (Exception ex) { _log($"git-activity: poll failed: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    // ---- auth request drops (A) ---------------------------------------------

    private void PollAuthDrops()
    {
        string[] files;
        try { files = Directory.GetFiles(_authDropDir, "auth-*.txt"); }
        catch { return; }
        foreach (var file in files.OrderBy(f => f))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; } // writer may still hold it; retry next tick
            var line = FormatAuthLine(text);
            if (line != null) ConsoleUI.LogGit(line, attention: true);
            try { File.Delete(file); } catch { /* re-emits next tick if it lingers */ }
        }
    }

    /// <summary>
    /// A drop file body is a single tab-separated line: instanceId, sessionId,
    /// protocol, host. Returns the console line to show — including a short session
    /// id so the specific agent can be traced when a repo runs several — or null if
    /// the drop is empty/malformed. Pure — unit-tested.
    /// </summary>
    public static string? FormatAuthLine(string dropText)
    {
        if (string.IsNullOrWhiteSpace(dropText)) return null;
        var parts = dropText.Trim().Split('\t');
        var instance = parts.Length > 0 ? parts[0].Trim() : "";
        var sessionId = parts.Length > 1 ? parts[1].Trim() : "";
        var host = parts.Length > 3 ? parts[3].Trim() : "";
        if (instance.Length == 0 || host.Length == 0) return null;
        var idTag = sessionId.Length >= 8 ? $" [{sessionId[..8]}]"
                  : sessionId.Length > 0 ? $" [{sessionId}]" : "";
        return $"[git-auth] {instance}{idTag} is requesting {host} credentials — if it hangs, check that session's window";
    }

    // ---- movement / reflog (B) ----------------------------------------------

    private void SeedReflogs()
    {
        foreach (var file in AllReflogFiles())
        {
            try { _reflogOffsets[file] = new FileInfo(file).Length; } catch { /* ignore */ }
        }
    }

    private void PollReflogs()
    {
        foreach (var (name, root) in _repos)
        {
            var remotesDir = RemotesLogDir(root);
            if (remotesDir == null) continue;
            string[] files;
            try { files = Directory.GetFiles(remotesDir, "*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files)
            {
                // origin/HEAD is a symbolic pointer, not a transfer — skip its noise.
                if (string.Equals(Path.GetFileName(file), "HEAD", StringComparison.Ordinal)) continue;

                long len;
                try { len = new FileInfo(file).Length; } catch { continue; }

                var prev = _reflogOffsets.TryGetValue(file, out var known) ? known : 0L;
                if (len < prev) { _reflogOffsets[file] = len; continue; } // truncated/rewritten — reseed
                if (len == prev) continue;                                 // no growth

                string added;
                try { added = ReadFrom(file, prev); }
                catch { _reflogOffsets[file] = len; continue; }

                var reference = RefFromLogPath(remotesDir, file);
                foreach (var rawLine in added.Split('\n'))
                {
                    var msg = FormatMovementLine(name, reference, rawLine);
                    if (msg != null) ConsoleUI.LogGit(msg);
                }
                _reflogOffsets[file] = len;
            }
        }
    }

    private IEnumerable<string> AllReflogFiles()
    {
        foreach (var (_, root) in _repos)
        {
            var remotesDir = RemotesLogDir(root);
            if (remotesDir == null) continue;
            string[] files;
            try { files = Directory.GetFiles(remotesDir, "*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    // Resolve <git-common-dir>/logs/refs/remotes for a repo root, or null if the
    // root is not a git repo or has no remote-tracking logs yet. Cached per root
    // (the common dir is stable; handles plain repos, worktrees and .git-file cases).
    private string? RemotesLogDir(string root)
    {
        if (!_commonDirs.TryGetValue(root, out var common))
        {
            common = GitHelper.GitCommonDir(root);
            _commonDirs[root] = common;
        }
        if (common == null) return null;
        var dir = Path.Combine(common, "logs", "refs", "remotes");
        return Directory.Exists(dir) ? dir : null;
    }

    // "origin/master" from a reflog file path under logs/refs/remotes.
    private static string RefFromLogPath(string remotesDir, string file)
    {
        var rel = Path.GetRelativePath(remotesDir, file).Replace('\\', '/');
        return rel;
    }

    private static string ReadFrom(string file, long offset)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > 0 && offset <= fs.Length) fs.Seek(offset, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Turn one raw remote-tracking reflog line into a console line, or null if the
    /// line is blank/malformed. Reflog format is
    /// "&lt;old-sha&gt; &lt;new-sha&gt; &lt;name&gt; &lt;email&gt; &lt;ts&gt; &lt;tz&gt;\t&lt;message&gt;",
    /// where message is "update by push", "fetch", "pull" (possibly with a suffix).
    /// Pure — unit-tested.
    /// </summary>
    public static string? FormatMovementLine(string repoName, string reference, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return null;
        var tab = rawLine.IndexOf('\t');
        if (tab < 0) return null;
        var meta = rawLine[..tab];
        var message = rawLine[(tab + 1)..].Trim();

        var tokens = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;
        var newSha = tokens[1];
        var shortSha = newSha.Length >= 7 ? newSha[..7] : newSha;

        string verb;
        var lower = message.ToLowerInvariant();
        if (lower.Contains("push")) verb = "pushed to";
        else if (lower.Contains("fetch")) verb = "fetched from";
        else if (lower.Contains("pull")) verb = "pulled into";
        else verb = "updated";

        return $"[git] {repoName} {verb} {reference} ({shortSha})";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // ---- credential logger helper (runs as `huddle --cred-log ...`) ----------

    /// <summary>
    /// Entry point for `huddle --cred-log &lt;instanceId&gt; &lt;dropDir&gt; &lt;operation&gt;`,
    /// invoked by git as a credential helper that runs before GCM. git appends the
    /// operation (get/store/erase) as the final argument. On a "get" we record the
    /// requested host to a drop file and output NOTHING, so git falls through to the
    /// real GCM helper for the actual authentication. Never throws and never prints
    /// to stdout — breaking git's auth path is worse than missing a log line.
    /// </summary>
    public static int RunCredLog(string[] args)
    {
        try
        {
            // args: [--cred-log, <instanceId>, <sessionId>, <dropDir>, <operation>]
            // git appends the operation, so it is always the final arg.
            var instance = args.Length > 1 ? args[1] : "";
            var sessionId = args.Length > 2 ? args[2] : "";
            var dropDir = args.Length > 3 ? args[3] : "";
            var op = args.Length > 4 ? args[^1] : "get";
            if (!string.Equals(op, "get", StringComparison.Ordinal)) return 0; // only the request matters

            string? protocol = null, host = null;
            string? line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (line.Length == 0) break; // blank line terminates the credential request
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq];
                var val = line[(eq + 1)..];
                if (key == "protocol") protocol = val;
                else if (key == "host") host = val;
            }

            if (!string.IsNullOrEmpty(dropDir) && !string.IsNullOrEmpty(host))
            {
                Directory.CreateDirectory(dropDir);
                var body = $"{instance}\t{sessionId}\t{protocol}\t{host}";
                var f = Path.Combine(dropDir, $"auth-{Guid.NewGuid():N}.txt");
                File.WriteAllText(f, body);
            }
        }
        catch { /* never break git's credential path */ }
        return 0; // no stdout -> git proceeds to the real GCM helper
    }

    /// <summary>
    /// Write a per-session git config that logs credential requests before GCM.
    /// It [include]s the real system config (so nothing else about git's behaviour
    /// changes), resets the credential.helper list to drop the inherited GCM, then
    /// re-adds our logger first and "manager" (GCM) second. Pointed at via
    /// GIT_CONFIG_SYSTEM for the spawned session only — global config is untouched.
    /// The empty reset is why this must be a config FILE and not env config: cmd.exe
    /// cannot hold an empty environment variable.
    /// </summary>
    public static void WriteCredentialLoggerConfig(
        string configPath, string huddleExe, string? systemConfigPath, string instanceId, string sessionId, string dropDir)
    {
        var exe = huddleExe.Replace('\\', '/');
        var dir = dropDir.Replace('\\', '/');
        // Shell (!) form so git runs it directly with our args; forward slashes so
        // git's config parser and shell don't choke on backslash escapes. The value
        // git must see is:  !"<exe>" --cred-log "<instanceId>" "<sessionId>" "<dir>"
        // git appends the operation (get/store/erase) as a final arg after these.
        var helper = "\"!\\\"" + exe + "\\\" --cred-log \\\"" + instanceId + "\\\" \\\"" + sessionId + "\\\" \\\"" + dir + "\\\"\"";

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(systemConfigPath) && File.Exists(systemConfigPath))
        {
            sb.AppendLine("[include]");
            sb.AppendLine($"\tpath = {systemConfigPath.Replace('\\', '/')}");
        }
        sb.AppendLine("[credential]");
        sb.AppendLine("\thelper =");            // reset — drop inherited GCM so ours runs first
        sb.AppendLine($"\thelper = {helper}");   // log the request, output nothing, fall through
        sb.AppendLine("\thelper = manager");     // real GCM performs the actual auth
        File.WriteAllText(configPath, sb.ToString());
    }
}

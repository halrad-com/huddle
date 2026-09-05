using System.Runtime.InteropServices;

namespace Huddle;

public enum CommandResult
{
    Continue,
    Quit,
    Shutdown
}

// Minimal user32 P/Invoke for bringing a session's console window to the foreground.
// Windows-only; the rest of huddle is Windows-targeted too, so no cross-platform guard.
internal static class WindowFocus
{
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Try to bring the window identified by <paramref name="hWnd"/> to the foreground.
    /// Restores it first if minimized. Returns false if the handle is zero.
    /// SetForegroundWindow may still be denied by Windows if huddle is not the
    /// current foreground process, but typically the user just typed a command
    /// into it, so it is.
    /// </summary>
    public static bool BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        return SetForegroundWindow(hWnd);
    }
}

public class ConsoleUI
{
    private readonly SessionManager _manager;
    public IpcManager? Ipc { get; set; }
    public Orchestrator? Orchestrator { get; set; }

    /// <summary>The huddle.json this instance was started with — what `settings` reads,
    /// writes and what `reload` re-validates. Set by Program.cs from its resolved path.</summary>
    public string ConfigPath { get; init; } = "huddle.json";

    /// <summary>The live peek hotkey, so `settings peekHotkey &lt;chord&gt;` can re-register
    /// it on this running process instead of waiting for a reload. Settable rather than
    /// init-only because Program.cs builds the listener after the UI. Null when no listener
    /// was created (tests), and the set path falls back to the generic message then.</summary>
    public PeekHotkeySwitch? PeekHotkeys { get; set; }

    private IDocumentSource? _docSource;
    private readonly IDocumentOpener _docOpener = new ShellDocumentOpener();
    private List<DocumentEntry> _lastDocs = new();
    // Active after a `find` run: translates shared display numbers to backing lists.
    // Null whenever the last listing came from plain docs/history (legacy numbering).
    private FindMap? _findMap;
    private int _docsPageOffset;                 // next entry index for `docs more`
    private const int DocsPageSize = 10;

    // huddle root = the directory holding logs/ (DataDir), i.e. where huddle.json lives.
    private IDocumentSource DocSource => _docSource ??= new CompositeDocumentSource(
        new ScratchpadDocumentSource(_manager.DataDir, _manager.Repos, Log),   // declared (wins on dedupe)
        new FilesystemDocSource(Directory.GetParent(_manager.DataDir)?.FullName ?? ".", _manager.Repos, Log),  // auto-discovered repo docs
        new GitChurnSource(_manager.Repos, Log));

    public ConsoleUI(SessionManager manager)
    {
        _manager = manager;
    }

    public void PrintBanner()
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"=== claude huddle {BuildInfo.Short} ===");
        }
        finally { Console.ResetColor(); }
        Console.WriteLine("Claude Code session orchestrator");
        Console.WriteLine();
    }

    /// <summary>Every registered repo as (name, root) — a session can owe work in a repo
    /// that is not its own, so obligations are read across all of them.</summary>
    private IEnumerable<(string Name, string Root)> RepoPairs() =>
        _manager.Config.Sessions.Select(s => (s.Name, s.Root));

    public void PrintStatus()
    {
        var now = DateTime.Now;
        Console.WriteLine();

        if (_manager.Instances.Count == 0)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("No instances. Use 'start <repo> [persona]' to launch one.");
            }
            finally { Console.ResetColor(); }
            Console.WriteLine();
            return;
        }

        // Group instances by repo for display
        var projectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        var grouped = _manager.Instances.Values
            .GroupBy(i => i.RepoName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            foreach (var instance in group.OrderBy(i => i.InstanceId))
            {
                try
                {
                    var color = instance.Status switch
                    {
                        SessionStatus.Running => ConsoleColor.Green,
                        SessionStatus.Crashed => ConsoleColor.Red,
                        SessionStatus.Starting => ConsoleColor.Yellow,
                        SessionStatus.Stopping => ConsoleColor.Yellow,
                        SessionStatus.AutoRestarting => ConsoleColor.Yellow,
                        _ => ConsoleColor.Gray
                    };

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"[{now:HH:mm:ss}] ");

                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write($"{instance.InstanceId,-24} ");

                    Console.ForegroundColor = color;
                    Console.Write($"{instance.Status,-16} ");

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    if (instance.Status == SessionStatus.Running)
                        Console.Write($"({instance.FormatUptime(),-8}) ");
                    else if (instance.Status == SessionStatus.Crashed)
                        Console.Write($"(exit {instance.LastExitCode,-4}) ");
                    else if (instance.Status == SessionStatus.AutoRestarting && instance.AutoRestartAt.HasValue)
                    {
                        var remaining = (int)Math.Ceiling((instance.AutoRestartAt.Value - DateTime.Now).TotalSeconds);
                        if (remaining < 0) remaining = 0;
                        Console.Write($"(in {remaining}s)     ");
                    }
                    else
                        Console.Write($"{"",10} ");

                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write(ShortenPath(instance.Root));

                    if (instance.ActivePersona != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.Write($"  [{instance.ActivePersona}]");
                    }

                    // §5.6: a session cannot look clean while it owes work. The audited
                    // session reported "nothing in flight, inbox clear" repeatedly while
                    // holding four unread assignments — huddle knew and said nothing.
                    var owed = Obligations.StatusNote(
                        Obligations.For(instance.InstanceId, RepoPairs(), DateTimeOffset.Now), DateTimeOffset.Now);
                    if (owed.Length > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write($"  {owed}");
                    }

                    // Project attribution + declared purpose: the operator should
                    // never have to ask a window why it exists (2026-08-09 feedback).
                    // Show what we KNOW; where a stamp was expected (the session has a
                    // declared task) but absent, flag the gap — absence is information,
                    // but only on task-spawned sessions, not casual bare starts.
                    var hasTask = !string.IsNullOrWhiteSpace(instance.DeclaredPurpose);
                    if (!string.IsNullOrEmpty(instance.Project))
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write($"  [{instance.Project}]");
                    }
                    else if (hasTask)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("  [no-project]");
                    }
                    if (hasTask)
                    {
                        var task = instance.DeclaredPurpose!.Replace('\r', ' ').Replace('\n', ' ');
                        if (task.Length > 48) task = task[..48] + "…";
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"  {task}");
                    }

                    // Reflect agent trouble from the session's transcript: a current
                    // API error (500/529/rate-limit) is called out in red; otherwise a
                    // long idle gap (transcript not growing) is noted plainly — it can't
                    // tell "stuck" from "waiting at the prompt", so it isn't an alarm.
                    if (instance.Status == SessionStatus.Running && instance.SessionId is Guid sid)
                    {
                        var tpath = SessionTrouble.TranscriptPath(projectsRoot, instance.Root, sid);
                        if (tpath != null)
                        {
                            var reason = SessionTrouble.ApiErrorReason(tpath);
                            if (reason != null)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.Write($"  [!] API: {reason}");
                            }
                            // One threshold, named once: PeekModel's doc comment claims the
                            // status verb shares it, and a hardcoded 3 here made that claim
                            // false the moment either side moved.
                            else if (SessionTrouble.LastActivity(tpath) is { } la
                                     && DateTime.Now - la > TimeSpan.FromMinutes(PeekModel.IdleThresholdMinutes))
                            {
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.Write($"  idle {(int)(DateTime.Now - la).TotalMinutes}m");
                            }
                        }
                    }

                    Console.WriteLine();
                }
                finally { Console.ResetColor(); }
            }
        }

        // Two live sessions on one identity cannot see each other's mail, and until the
        // OwnerGuid fix could not see each other's claims either (I016). The spawn guard
        // stops new ones; this makes a pair that already exists visible here rather than
        // through a failed edit hours later.
        var dupes = Obligations.DuplicateIdentities(
            _manager.Instances.Values.Where(i => i.IsAlive).Select(i => (i.InstanceId, i.Process?.Id ?? 0)));
        foreach (var d in dupes)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ** DUPLICATE IDENTITY ** {d}");
            }
            finally { Console.ResetColor(); }
        }

        Console.WriteLine();
    }

    // Help renders FROM Verbs.Catalog (HelpView) — the hand-maintained list this
    // method used to hold was a second source that drifted (the hodgepodge). Bare
    // help = compact groups; 'help all' = grouped usage; 'help <verb>' = one usage.
    public void PrintHelp(string arg = "")
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var a = arg.Trim();
            IReadOnlyList<string> lines;
            if (a.Length == 0)
            {
                Console.WriteLine("Commands (grouped; aliases still work):");
                lines = HelpView.RenderCompact(Verbs.Catalog);
            }
            else if (a.Equals("all", StringComparison.OrdinalIgnoreCase))
                lines = HelpView.RenderFull(Verbs.Catalog);
            else
                lines = HelpView.RenderVerb(Verbs.Catalog, a);
            foreach (var line in lines) Console.WriteLine(line);
        }
        finally { Console.ResetColor(); }
    }

    public void PrintPersonas(string[] personas)
    {
        if (personas.Length == 0)
        {
            Log("No personas found.");
            return;
        }

        Console.WriteLine();
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{"persona",-22} {"model",-22} {"effort",-7} {"bare",-5} {"tuning",-40}");
            Console.WriteLine(new string('-', 100));
            foreach (var p in personas)
            {
                var cfg = _manager.GetPersonaTuning(p);
                var tuning = FormatTuningSummary(cfg);

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"{p,-22} ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{ShortenModel(cfg.Model) ?? "(default)",-22} ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"{cfg.Effort ?? "-",-7} ");
                Console.ForegroundColor = cfg.Bare == true ? ConsoleColor.Green : ConsoleColor.DarkGray;
                Console.Write($"{(cfg.Bare == true ? "yes" : "-"),-5} ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(tuning);
            }
        }
        finally { Console.ResetColor(); }
        Console.WriteLine();
    }

    private static string? ShortenModel(string? model) => model switch
    {
        null => null,
        "claude-opus-4-7"   => "opus-4-7",
        "claude-sonnet-4-6" => "sonnet-4-6",
        "claude-haiku-4-5"  => "haiku-4-5",
        _ => model
    };

    private static string FormatTuningSummary(PersonaConfig cfg)
    {
        var parts = new List<string>();
        if (cfg.Tools != null) parts.Add($"tools=[{string.Join(",", cfg.Tools)}]");
        if (cfg.DisallowedTools != null) parts.Add($"deny=[{string.Join(",", cfg.DisallowedTools)}]");
        if (cfg.AllowedTools != null) parts.Add($"allow=[{string.Join(",", cfg.AllowedTools)}]");
        if (cfg.McpServers is { Count: > 0 }) parts.Add($"mcp=[{string.Join(",", cfg.McpServers.Keys)}]{(cfg.StrictMcp == true ? "!" : "")}");
        return parts.Count == 0 ? "(prompt only)" : string.Join("  ", parts);
    }

    public void PrintRepos()
    {
        Console.WriteLine();
        foreach (var (name, def) in _manager.Repos.OrderBy(r => r.Key))
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"  {name,-20} ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(def.Purpose);

                if (def.Aliases != null && def.Aliases.Length > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write($"  ({string.Join(", ", def.Aliases)})");
                }
                Console.WriteLine();
            }
            finally { Console.ResetColor(); }
        }
        Console.WriteLine();
    }

    public void PrintPrompt()
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("> ");
        }
        finally { Console.ResetColor(); }
    }

    public CommandResult HandleCommand(string input)
    {
        var parts = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return CommandResult.Continue;

        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (cmd)
        {
            case "status" or "s":
                PrintStatus();
                break;

            case "start":
                if (string.IsNullOrEmpty(arg))
                {
                    Log("Usage: start <repo> [persona] [prompt]");
                }
                else
                {
                    var startParts = arg.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    var repoName = startParts[0];
                    var persona = startParts.Length > 1 ? startParts[1].Trim() : null;
                    var prompt = startParts.Length > 2 ? startParts[2].Trim() : null;
                    _manager.Start(repoName, persona, prompt: prompt);
                }
                break;

            case "stop":
                if (string.IsNullOrEmpty(arg))
                    Log("Usage: stop <instance|repo>");
                else
                    _manager.Stop(arg);
                break;

            case "restart" or "r":
                if (string.IsNullOrEmpty(arg))
                    Log("Usage: restart <instance>");
                else
                    _manager.Restart(arg);
                break;

            case "resume":
                if (string.IsNullOrEmpty(arg))
                    Log("Usage: resume <instance|repo:persona|n>  (n = row from the last 'history' listing)");
                else if (int.TryParse(arg.Trim(), out var historyIdx))
                    HandleHistoryResume(historyIdx);
                else
                    _manager.Resume(arg);
                break;

            case "history":
                HandleHistory(arg);
                break;

            case "find":
                HandleFind(arg);
                break;

            case "recover":
                HandleRecover(arg);
                break;

            case "projects":
                HandleProjects(arg);
                break;

            case "project":
                HandleProjectDetail(arg);
                break;

            case "handoffs" or "handoff":
                HandleHandoffs(arg);
                break;

            case "stats":
                HandleStats(arg);
                break;

            case "personas" or "p":
                PrintPersonas(_manager.GetAvailablePersonas());
                break;

            case "repos":
                PrintRepos();
                break;

            case "send":
                HandleSend(arg);
                break;

            case "say":
                HandleSay(arg);
                break;

            case "broadcast":
                HandleBroadcast(arg);
                break;

            case "shell":
                HandleShell(arg);
                break;

            case "messages" or "msg":
                HandleMessages(arg);
                break;

            case "huddle":
                HandleHuddle(arg);
                break;

            case "delegate":
                HandleDelegate(arg);
                break;

            case "tasks":
                HandleTasks();
                break;

            case "progress":
                HandleProgress();
                break;

            case "conflicts":
                HandleConflicts();
                break;

            case "census":
                HandleCensus(arg);
                break;

            case "queue":
                HandleQueue();
                break;

            case "settings":
                HandleSettings(arg);
                break;

            case "ledger":
                HandleLedger(arg);
                break;

            case "replay":
                HandleReplay(arg);
                break;

            case "docs":
                HandleDocs(arg);
                break;

            case "open":
                HandleOpen(arg);
                break;

            case "reload" or "rebuild":
                if (HandleReload(arg)) return CommandResult.Quit;
                break;

            case "direct":
                HandleDirect(arg);
                break;

            case "scan":
                HandleScan();
                break;

            case "janitor":
                HandleJanitor();
                break;

            case "backlog" or "unread":
                HandleBacklog();
                break;

            case "focus" or "goto":
                HandleFocus(arg);
                break;

            case "peek":
                PeekController.Show(_manager, Ipc, Log);
                break;

            case "quit" or "q" or "exit":
                return CommandResult.Quit;

            case "shutdown":
                return CommandResult.Shutdown;

            case "ver" or "version":
                Console.WriteLine(BuildInfo.Full);
                break;

            case "help" or "h" or "?":
                PrintHelp(arg);
                break;

            default:
                Log($"Unknown command: {cmd}. Type 'help' for commands.");
                break;
        }

        return CommandResult.Continue;
    }

    private void HandleSend(string arg)
    {
        if (Ipc == null)
        {
            Log("IPC is disabled. Enable 'ipc' in huddle.json.");
            return;
        }

        var sendParts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (sendParts.Length < 2)
        {
            Log("Usage: send <instance> <message>");
            return;
        }

        var targetId = sendParts[0];
        var message = sendParts[1];

        // Resolve instance to get SafePathName
        if (!_manager.Instances.TryGetValue(targetId, out var target))
        {
            Log($"Unknown instance: {targetId}");
            return;
        }

        Ipc.Send("_huddle", target.SafePathName, message, message, "info");
    }

    private void HandleSay(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Log("Usage: say <instance|repo:persona> <text>");
            return;
        }

        var targetId = parts[0];
        var text = parts[1];

        // Direct match first, then alias resolution (e.g. "app:architect" → "myapp:architect").
        if (!_manager.Instances.TryGetValue(targetId, out var instance))
            instance = _manager.ResolveInstance(targetId);

        if (instance == null)
        {
            Log($"Unknown instance: {targetId}");
            return;
        }

        if (instance.Process == null || instance.Process.HasExited)
        {
            Log($"{instance.InstanceId} is not running.");
            return;
        }

        var pid = instance.Process.Id;
        // Explicit operator action — deliver even if that console is in the
        // foreground (force bypasses the operator-busy hold used for auto-nudges).
        if (PromptInjector.Inject(pid, text, Log, force: true))
            Log($"say → {instance.InstanceId} (PID {pid}): {text}");
        else
            Log($"say → {instance.InstanceId}: injection failed (see log above)");
    }

    private void HandleShell(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            Log("Usage: shell <data> | shell <repo> <data>");
            return;
        }

        // If first token is a registered repo/alias, peel it off as WorkingDirectory.
        string? workingDir = null;
        var remainder = arg.Trim();
        var firstSpace = remainder.IndexOf(' ');
        if (firstSpace > 0)
        {
            var firstToken = remainder[..firstSpace];
            var resolved = _manager.ResolveRepoName(firstToken);
            if (_manager.Repos.TryGetValue(resolved, out var def))
            {
                workingDir = def.Root;
                remainder = remainder[(firstSpace + 1)..].Trim();
            }
        }
        else
        {
            // Single-token form: `shell <repo>` hands the repo root to the OS file handler.
            var resolved = _manager.ResolveRepoName(remainder);
            if (_manager.Repos.TryGetValue(resolved, out var def))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = def.Root,
                    UseShellExecute = true
                });
                Log($"shell: {def.Root}");
                return;
            }
        }

        // Split remainder into FileName + Arguments. Honor a leading quoted path.
        string fileName;
        string? arguments = null;
        if (remainder.StartsWith('"'))
        {
            var close = remainder.IndexOf('"', 1);
            if (close > 0)
            {
                fileName = remainder[1..close];
                var rest = remainder[(close + 1)..].Trim();
                arguments = rest.Length > 0 ? rest : null;
            }
            else
            {
                fileName = remainder;
            }
        }
        else
        {
            var sp = remainder.IndexOf(' ');
            if (sp < 0) { fileName = remainder; }
            else
            {
                fileName = remainder[..sp];
                arguments = remainder[(sp + 1)..].Trim();
            }
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true
            };
            if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
            if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;

            System.Diagnostics.Process.Start(psi);
            Log(workingDir != null ? $"shell: {fileName} (in {workingDir})" : $"shell: {fileName}");
        }
        catch (Exception ex)
        {
            Log($"shell: failed — {ex.Message}");
        }
    }

    // `replay <repo>` — run the repo's captured regression tests (MBXHVAL capture suites)
    // against its configured test instance via mbxhval, and report pass/fail.
    private void HandleReplay(string arg)
    {
        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            Log("Usage: replay <repo> [host[:port]]  — run the repo's captured regression tests via mbxhval; optional host targets a remote DUT (cross-box, e.g. to exercise auth gates that exempt local callers)");
            return;
        }
        var repoArg = parts[0];

        var resolved = _manager.ResolveRepoName(repoArg);
        if (!_manager.Repos.TryGetValue(resolved, out var def))
        {
            Log($"replay: unknown repo '{repoArg}'");
            return;
        }

        // Optional cross-box target: `replay app 10.1.2.3:8080`. The DUT's auth
        // gate exempts all same-machine callers (IsLocal), so auth captures can only
        // engage when the suite is aimed at a DUT on another box. Reject anything
        // malformed loudly — a silently-mangled target sends the operator off to
        // debug a DUT that was never actually tested.
        if (parts.Length > 2)
        {
            Log($"replay: too many arguments — usage: replay <repo> [host[:port]] (got '{string.Join(" ", parts[1..])}')");
            return;
        }
        string? hostOverride = null;
        int? portOverride = null;
        if (parts.Length == 2)
        {
            var target = parts[1];
            var colons = target.Count(ch => ch == ':');
            if (colons > 1)
            {
                Log($"replay: '{target}' is not a valid host[:port] target (IPv6 targets are not supported)");
                return;
            }
            if (colons == 1)
            {
                var idx = target.IndexOf(':');
                var hostPart = target[..idx];
                if (hostPart.Length == 0 || !int.TryParse(target[(idx + 1)..], out var p) || p < 1 || p > 65535)
                {
                    Log($"replay: '{target}' is not a valid host[:port] target");
                    return;
                }
                hostOverride = hostPart;
                portOverride = p;
            }
            else hostOverride = target;
        }

        CaptureReplay.Result r;
        if (!string.IsNullOrWhiteSpace(def.ReplayCommand))
        {
            if (hostOverride != null)
                Log($"replay: host override ignored — '{resolved}' uses a custom replayCommand");
            r = CaptureReplay.RunCommand(def.ReplayCommand!, def.ReplayWorkingDir ?? def.Root, Log);
        }
        else
        {
            var mbxhvalPath = _manager.Config.MbxhvalPath;
            if (string.IsNullOrWhiteSpace(mbxhvalPath))
            {
                Log("replay: set mbxhvalPath in huddle.json (path to a built mbxhval.dll or .exe)");
                return;
            }

            var capturesDir = Path.Combine(def.Root, "MBXHVAL", "tests", "suites", "captures");
            var host = hostOverride ?? (string.IsNullOrWhiteSpace(def.ReplayHost) ? "127.0.0.1" : def.ReplayHost!);
            var port = portOverride ?? def.ReplayPort ?? 8080;
            if (hostOverride != null)
                Log($"replay: cross-box target {host}:{port}");

            r = CaptureReplay.Run(capturesDir, mbxhvalPath!, host, port, Log);
        }

        if (r.Ran)
            Log(r.Failed == 0
                ? $"replay {resolved}: ALL GREEN — {r.Passed}/{r.Total} passed"
                : $"replay {resolved}: {r.Failed} FAILED — {r.Passed}/{r.Total} passed");
    }

    // `docs ?` — print the filter key.
    private void PrintDocsKey()
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("docs filters:");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  docs        ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Output — human deliverables (specs, designs, reports, READMEs)");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("  docs plans  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("+ Plans — planning docs (plans, roadmaps, ideas)");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write("  docs churn  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("+ Churn — git working-tree changes (source included), on demand");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  docs @repo  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("restrict to one repo — aliases resolve (e.g. 'docs @app', 'docs @myapp')");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  docs <kw>   ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("filter by folder / title / repo — e.g. 'docs reference', 'docs specs'");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  docs -1w    ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("time window — -Nh / -Nd / -Nw (combinable: 'docs plans -1d')");
            Console.WriteLine("  docs more   next 10 entries of the last listing");
            Console.WriteLine("  open <n>    open the nth entry from the last listing");
        }
        finally { Console.ResetColor(); }
    }

    // `docs [output|plans|churn] [@repo] [<keyword>] [-<N><h|d|w>]` — list documents, newest first.
    //   level    optional first token: output (default) | plans | churn.
    //   @repo    restrict to one repo; aliases resolve (@app -> myapp).
    //   keyword  free text; matches title / path / repo / session (folder names work:
    //            `docs reference`, `docs specs`). Slashes normalize, so `docs /plans/` works.
    //   window   -1d / -2w / -12h — keep only docs touched within that span.
    // Bare `docs` is curated quiet (home repo + cross-repo reference tier only); ANY filter
    // searches every discovered + declared doc.
    private void HandleDocs(string arg)
    {
        var a = arg.Trim();
        // `docs ?` prints the key and lists nothing, so it leaves any find map alone.
        if (a.Equals("?", StringComparison.OrdinalIgnoreCase) || a.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            PrintDocsKey();
            return;
        }

        // `docs more` — continue the previous listing, next page of 10.
        if (a.Equals("more", StringComparison.OrdinalIgnoreCase))
        {
            if (_lastDocs.Count == 0) { Log("No listing to continue — run 'docs' first."); return; }
            if (_docsPageOffset >= _lastDocs.Count) { Log("End of list — all entries shown. Use 'open <n>' to open one."); return; }
            _findMap = null;                    // paging a plain listing → legacy numbering
            PrintDocsPage();
            return;
        }
        _findMap = null;

        // Parse: optional leading level token; @repo, time-window, and keyword tokens in any order.
        var tokens = a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var level = DocLevel.Output;
        DateTime? cutoff = null;
        string? window = null;
        string? repoFilter = null;
        var keywordParts = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (i == 0)
            {
                var low = t.ToLowerInvariant();
                if (low == "output") { level = DocLevel.Output; continue; }
                if (low == "plans") { level = DocLevel.Plans; continue; }
                if (low == "churn") { level = DocLevel.Churn; continue; }
            }
            if (t.StartsWith('@') && t.Length > 1)
            {
                repoFilter = _manager.ResolveRepoName(t[1..].ToLowerInvariant());
                continue;
            }
            var c = ParseWindow(t);
            if (c.HasValue) { cutoff = c; window = t.TrimStart('-'); }
            else keywordParts.Add(t);
        }
        var keyword = string.Join(' ', keywordParts).Trim();
        var filtersActive = keyword.Length > 0 || cutoff.HasValue || repoFilter != null;

        IEnumerable<DocumentEntry> docs = DocSource.GetDocuments(level);
        var hidden = new List<DocumentEntry>();                     // curated-out cross-repo docs (for the footer)
        if (!filtersActive)
        {
            var kept = new List<DocumentEntry>();
            foreach (var e in docs)
                (IsCuratedOut(e) ? hidden : kept).Add(e);
            docs = kept;
        }
        if (repoFilter != null)
            docs = docs.Where(e => string.Equals(e.Repo, repoFilter, StringComparison.OrdinalIgnoreCase));
        if (cutoff.HasValue)
            docs = docs.Where(e => e.Timestamp.HasValue && e.Timestamp.Value >= cutoff.Value);
        if (keyword.Length > 0)
        {
            var k = keyword.Replace('\\', '/');
            docs = docs.Where(e => Matches(e, k));
        }
        _lastDocs = docs.ToList();

        if (_lastDocs.Count == 0)
        {
            var qual = new List<string>();
            if (repoFilter != null) qual.Add($"in repo '{repoFilter}'");
            if (keyword.Length > 0) qual.Add($"matching '{keyword}'");
            if (window != null) qual.Add($"in the last {window}");
            if (qual.Count > 0)
                Log($"No documents {string.Join(" ", qual)}.");
            else
                Log(level == DocLevel.Output
                    ? "No documents declared. Sessions record artifacts in a '## Documents' scratchpad section."
                    : "No documents at this level.");
            return;
        }

        _docsPageOffset = 0;
        PrintDocsPage();
        if (hidden.Count > 0)
            PrintHiddenFooter(hidden);
        var summary = $"{_lastDocs.Count} document(s)";
        if (repoFilter != null) summary += $" in {repoFilter}";
        if (keyword.Length > 0) summary += $" matching '{keyword}'";
        if (window != null) summary += $" in the last {window}";
        Log($"{summary}. Use 'open <n>' to open one.");
    }

    private const int FindGroupCap = 10;

    private const string FindUsage =
        "Usage: find <keyword> [@repo] [-Nh|-Nd|-Nw] — content search across docs, sessions, notes, mail";

    private void HandleFind(string arg)
    {
        var a = arg.Trim();
        // `find ?` must not run as a keyword — "?" matches almost every transcript line.
        if (a.Equals("?", StringComparison.OrdinalIgnoreCase) || a.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Log(FindUsage);
            return;
        }
        DateTime? cutoff = null;
        string? window = null, repoFilter = null;
        var keywordParts = new List<string>();
        foreach (var t in a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.StartsWith('@') && t.Length > 1)
            {
                repoFilter = _manager.ResolveRepoName(t[1..].ToLowerInvariant());
                continue;
            }
            var c = ParseWindow(t);
            if (c.HasValue) { cutoff = c; window = t.TrimStart('-'); }
            else keywordParts.Add(t);
        }
        var keyword = string.Join(' ', keywordParts).Trim();
        if (keyword.Length == 0)
        {
            Log(FindUsage);
            return;
        }

        var ipcDir = Ipc?.IpcDir
            ?? Path.Combine(Directory.GetParent(_manager.DataDir)?.FullName ?? ".", "ipc");
        var search = new ContentSearch(
            DocSource, CreateTranscriptStore(), _manager.DataDir, ipcDir,
            sid => _manager.Instances.Values.FirstOrDefault(i =>
                i.IsAlive && i.SessionId.HasValue &&
                string.Equals(i.SessionId.Value.ToString(), sid, StringComparison.OrdinalIgnoreCase))
                ?.InstanceId,
            Log);
        var r = search.Search(keyword, repoFilter, cutoff);

        var total = r.Docs.Count + r.Sessions.Count + r.Notes.Count + r.Mail.Count;
        if (total == 0)
        {
            // No listing printed, so the previous one still stands: leave the map AND both
            // backing lists alone. Nulling the map here would strand the earlier find's rows
            // on screen under legacy numbering — `open 3` would open a different row.
            var qual = new List<string> { $"for '{keyword}'" };
            if (repoFilter != null) qual.Add($"in repo '{repoFilter}'");
            if (window != null) qual.Add($"in the last {window}");
            Log($"No hits {string.Join(" ", qual)}.");
            return;
        }

        _lastDocs = new List<DocumentEntry>();
        _lastHistory = new List<SessionSummary>();
        _findMap = new FindMap();

        PrintFindDocGroup("Docs", r.Docs);
        PrintFindSessions(r.Sessions);
        PrintFindDocGroup("Notes", r.Notes);
        PrintFindMail(r.Mail);

        // Everything a find puts in the backing lists is already on screen, so the paging
        // cursors sit at the end: a following `docs more` / `history more` says so instead
        // of re-printing find rows from a stale offset.
        _docsPageOffset = _lastDocs.Count;
        _historyPageOffset = _lastHistory.Count;

        if (r.TranscriptsTruncated)
            PrintDim($"  (newest {_manager.Config.Settings.Int("transcriptMaxScan")} transcripts scanned)");
        Log($"{total} hit(s) for '{keyword}'. 'open <n>' to open, 'resume <n>' to reopen a session.");
    }

    private void PrintDim(string text)
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(text);
        }
        finally { Console.ResetColor(); }
    }

    // Docs and Notes groups share row shape: DocumentEntry into _lastDocs.
    private void PrintFindDocGroup(string header, IReadOnlyList<DocumentEntry> entries)
    {
        if (entries.Count == 0) return;
        Console.WriteLine();
        PrintDim($"{header} ({entries.Count})");
        foreach (var e in entries.Take(FindGroupCap))
        {
            var n = _findMap!.Add(FindMap.Kind.Doc, _lastDocs.Count);
            _lastDocs.Add(e);
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {n,3}. ");
                Console.ForegroundColor = e.Level == DocLevel.Plans ? ConsoleColor.Cyan : ConsoleColor.White;
                Console.Write($"[{e.Level,-6}] ");
                Console.Write(Hyperlink(e.Path, e.Title));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                // Notes rows leave SourceSession empty (the title names the session already).
                if (!string.IsNullOrEmpty(e.SourceSession)) Console.Write($"  {e.SourceSession}");
                if (e.Note != null && e.Note != "auto") Console.Write($"  {e.Note}");
                if (e.Timestamp.HasValue) Console.Write($"  {e.Timestamp:yyyy-MM-dd HH:mm}");
                Console.WriteLine();
            }
            finally { Console.ResetColor(); }
        }
        if (entries.Count > FindGroupCap)
            PrintDim($"  … {entries.Count - FindGroupCap} more — narrow with @repo, a keyword, or -Nw");
    }

    private void PrintFindSessions(IReadOnlyList<SessionHit> sessions)
    {
        if (sessions.Count == 0) return;
        Console.WriteLine();
        PrintDim($"Sessions ({sessions.Count})");
        foreach (var h in sessions.Take(FindGroupCap))
        {
            var n = _findMap!.Add(FindMap.Kind.Session, _lastHistory.Count);
            _lastHistory.Add(h.Summary);
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {n,3}. ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"[{h.Summary.Repo}]".PadRight(14));
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($" {h.Summary.Title,-50}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {FormatWhen(h.Summary.LastActivity)}  {h.MatchCount} match(es)");
                Console.Write(h.LiveInstanceId != null
                    ? $"  — LIVE, 'focus {h.LiveInstanceId}'"
                    : $"  — 'resume {n}'");
                Console.WriteLine();
            }
            finally { Console.ResetColor(); }
        }
        if (sessions.Count > FindGroupCap)
            PrintDim($"  … {sessions.Count - FindGroupCap} more — narrow with @repo or -Nw");
    }

    private void PrintFindMail(IReadOnlyList<MailHit> mail)
    {
        if (mail.Count == 0) return;
        Console.WriteLine();
        PrintDim($"Mail ({mail.Count})");
        foreach (var m in mail.Take(FindGroupCap))
        {
            var n = _findMap!.Add(FindMap.Kind.Doc, _lastDocs.Count);
            _lastDocs.Add(new DocumentEntry(
                Title: m.Subject, Path: m.Path, SourceSession: $"{m.From} → {m.To}",
                Repo: "", Timestamp: m.Timestamp, Level: DocLevel.Output, Note: "mail"));
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {n,3}. ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{m.From} → {m.To}  ");
                Console.Write(Hyperlink(m.Path, $"\"{m.Subject}\""));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {m.Timestamp:yyyy-MM-dd HH:mm}  ({m.State})");
                Console.WriteLine();
            }
            finally { Console.ResetColor(); }
        }
        if (mail.Count > FindGroupCap)
            PrintDim($"  … {mail.Count - FindGroupCap} more — narrow with @repo or -Nw");
    }

    // Print the next page (DocsPageSize entries) of the last listing, newest first.
    // Numbering is absolute into _lastDocs so 'open <n>' works across pages.
    private void PrintDocsPage()
    {
        Console.WriteLine();
        var end = Math.Min(_docsPageOffset + DocsPageSize, _lastDocs.Count);
        for (var i = _docsPageOffset; i < end; i++)
        {
            var e = _lastDocs[i];
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {i + 1,3}. ");

                Console.ForegroundColor = e.Level switch
                {
                    DocLevel.Output => ConsoleColor.White,
                    DocLevel.Plans => ConsoleColor.Cyan,
                    _ => ConsoleColor.DarkGray
                };
                Console.Write($"[{e.Level,-6}] ");   // [Output] [Plans ] [Churn ]

                // OSC 8 hyperlink around the title (falls back to plain text if unsupported).
                Console.Write(Hyperlink(e.Path, e.Title));

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {e.SourceSession}");
                if (e.Timestamp.HasValue)
                    Console.Write($"  {e.Timestamp:yyyy-MM-dd HH:mm}");
                Console.WriteLine();
            }
            finally { Console.ResetColor(); }
        }
        _docsPageOffset = end;
        var remaining = _lastDocs.Count - end;
        if (remaining > 0)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  … {remaining} more — 'docs more' for the next {Math.Min(DocsPageSize, remaining)}, 'open <n>' to open");
            }
            finally { Console.ResetColor(); }
        }
        Console.WriteLine();
    }

    // Parse a relative time-window token: -<N><h|d|w> (hours/days/weeks). Returns the
    // cutoff instant (now - span), or null if the token isn't a window.
    private static DateTime? ParseWindow(string token)
    {
        if (token.Length < 3 || token[0] != '-') return null;
        var unit = char.ToLowerInvariant(token[^1]);
        if (unit is not ('h' or 'd' or 'w')) return null;
        if (!int.TryParse(token.AsSpan(1, token.Length - 2), out var n) || n <= 0) return null;
        var span = unit switch
        {
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            'w' => TimeSpan.FromDays(7 * n),
            _ => TimeSpan.Zero
        };
        return DateTime.Now - span;
    }

    // How recent an auto-discovered cross-repo doc must be to survive bare-list curation.
    // Freshly authored/edited docs surface at the top of the default view no matter the repo;
    // stale ones stay quiet so the 300+ discovered docs don't flood `docs`.
    private static readonly TimeSpan BareRecencyWindow = TimeSpan.FromDays(7);

    // Bare-list curation: auto-discovered cross-repo (non-home) docs are hidden from the
    // unfiltered listing UNLESS they are under the reference tier OR were touched within
    // BareRecencyWindow. Declared docs and churn always stay.
    private static bool IsCuratedOut(DocumentEntry e)
    {
        if (e.Note != "auto") return false;              // declared / churn: always keep
        if (e.Repo == "huddle") return false;            // home repo: full
        if (e.Path.Replace('\\', '/').Contains("/reference/", StringComparison.OrdinalIgnoreCase))
            return false;                                // reference tier: always keep
        if (e.Timestamp.HasValue && DateTime.Now - e.Timestamp.Value <= BareRecencyWindow)
            return false;                                // recently authored/edited: surface it
        return true;
    }

    // Footer for the curated bare list: how many cross-repo docs were hidden, by repo,
    // with the hint to reveal them. Keeps the quiet default honest — the omission is visible.
    private static void PrintHiddenFooter(List<DocumentEntry> hidden)
    {
        var byRepo = hidden.GroupBy(e => e.Repo)
                           .OrderByDescending(g => g.Count())
                           .Select(g => $"{g.Key} {g.Count()}")
                           .ToList();
        var shown = string.Join(", ", byRepo.Take(4));
        if (byRepo.Count > 4) shown += ", …";
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  … {hidden.Count} more in other repos ({shown}) — 'docs @<repo>', 'docs <kw>', or 'docs churn' to show");
        }
        finally { Console.ResetColor(); }
    }

    // Keyword match across the fields a human would search; path slashes pre-normalized.
    private static bool Matches(DocumentEntry e, string k) =>
        (e.Title?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Path?.Replace('\\', '/').Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Note?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Repo?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.SourceSession?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false);

    // `open <n>` — open the nth entry from the most recent `docs` listing.
    private void HandleOpen(string arg)
    {
        if (_findMap != null)
        {
            if (!int.TryParse(arg.Trim(), out var fn) || _findMap.Resolve(fn) is not { } slot)
            {
                Log($"Usage: open <n>  (1..{_findMap.Count} of the find listing)");
                return;
            }
            if (slot.kind == FindMap.Kind.Session)
            {
                Log($"{fn} is a session — use 'resume {fn}' (or 'history {fn}' for detail).");
                return;
            }
            // Slot indexes are written alongside the backing list, so this holds by
            // construction — checked anyway because the invariant spans five call sites.
            if (slot.index >= _lastDocs.Count)
            {
                Log($"Usage: open <n>  (1..{_findMap.Count} of the find listing)");
                return;
            }
            var found = _lastDocs[slot.index];
            if (_docOpener.Open(found.Path, Log))
                Log($"open: {found.Title} — {found.Path}");
            return;
        }
        if (_lastDocs.Count == 0)
        {
            Log("Nothing to open — run 'docs' first.");
            return;
        }
        if (!int.TryParse(arg.Trim(), out var n) || n < 1 || n > _lastDocs.Count)
        {
            Log($"Usage: open <n>  (1..{_lastDocs.Count})");
            return;
        }

        var entry = _lastDocs[n - 1];
        if (_docOpener.Open(entry.Path, Log))
            Log($"open: {entry.Path}");
    }

    // ---- `history` — list past sessions from Claude Code transcripts ----------
    // Spec: docs/superpowers/specs/2026-07-14-session-history-verb-design.md
    // Filters reuse the docs grammar (@repo, keyword, -N{h,d,w}); `history <n>`
    // shows detail and loads that session's files into _lastDocs so `open <n>`
    // works unchanged; `resume <n>` reopens the conversation in its cwd.

    private const int HistoryPageSize = 15;
    private List<SessionSummary> _lastHistory = new();
    private int _historyPageOffset;

    // ---- `projects` / `project <slug>` — the lens (projects phase 1) -----------
    // Spec: docs/superpowers/specs/2026-08-09-projects-artifacts-tasks-design.md
    // Repo layer (docs/projects/<slug>/) is standalone truth; projects-map.json
    // overlays notes/links; live bindings (sessions, claims, roster) are derived
    // fresh at read time — nothing stored, nothing to go stale.

    // Set by Program: <configDir>/projects-map.json (may not exist — that's fine).
    public string? ProjectsMapPath { get; set; }

    // `ledger [all|<id>|open [--by-age]|orphans] [--repo <name>] [--owner <instance>]`
    // Read-only (spec §5.1, Phase 1): parse every configured repo's docs/ledger/,
    // replay its events, render. Huddle writes nothing here — `accept` and `drop`
    // are Phase 2.
    private void HandleLedger(string arg)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        string? repoFilter = null, ownerFilter = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "--repo" && i + 1 < tokens.Count) { repoFilter = tokens[i + 1]; tokens.RemoveRange(i, 2); i--; continue; }
            if (tokens[i] == "--owner" && i + 1 < tokens.Count) { ownerFilter = tokens[i + 1]; tokens.RemoveRange(i, 2); i--; continue; }
        }
        // `--by-age` is accepted and is the DEFAULT (and only) ordering for `open` —
        // OpenByAge always sorts oldest-first. The flag exists so the operator can say
        // it out loud; it is documented as the default rather than left as a token that
        // silently does nothing.
        tokens.Remove("--by-age");

        var repos = _manager.Repos.Select(kv => (Name: kv.Key, kv.Value.Root)).ToList();
        if (repoFilter != null)
            repos = repos.Where(r => r.Name.Equals(repoFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        var snaps = repos.Select(r => LedgerView.Load(r.Name, r.Root)).ToList();

        var verb = tokens.Count > 0 ? tokens[0].ToLowerInvariant() : "";
        Console.WriteLine();
        switch (verb)
        {
            case "open":
            {
                var items = LedgerView.OpenByAge(snaps, DateTimeOffset.Now);
                if (ownerFilter != null)
                    items = items.Where(i => string.Equals(i.Owner, ownerFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                Console.Write(LedgerView.RenderOpenByAge(items));
                break;
            }
            case "orphans":
                Console.Write(LedgerView.RenderOrphans(snaps));
                break;
            case "":
            case "all":
            {
                // Which repo is "current" comes from the working directory measured against
                // the configured roots — not from a repo literally named "huddle", which
                // printed blank lines on any install without one (L4).
                var current = LedgerView.CurrentSnapshots(
                    snaps, repos.Select(r => (r.Name, r.Root)), Directory.GetCurrentDirectory(), repoFilter);
                if (current.Count == 0) { Log(LedgerView.NoCurrentLedger); return; }
                foreach (var s in current)
                {
                    Console.WriteLine($"  {s.Repo}  ({s.Dir})");
                    var warning = LedgerView.DeclaredRepoWarning(s);
                    if (warning != null) Console.WriteLine($"  ! {warning}");
                    Console.Write(LedgerView.RenderTree(s, includeClosed: verb == "all"));
                }
                break;
            }
            case "accept":
            case "drop":
            case "decline":
                HandleLedgerWrite(verb, tokens.Skip(1).ToList(), snaps, repos, repoFilter);
                break;
            default:
            {
                if (!LedgerId.TryParse(tokens[0], out var id))
                {
                    Log("usage: ledger [all | <id> | open [--by-age] | orphans | accept <id> | " +
                        "drop <id> <why> | decline <id> [note]] [--repo <name>] [--owner <instance>]");
                    return;
                }
                // Each snapshot carries its own event log, so RenderOne scopes history
                // per repo (L2) — and we no longer re-read every events.jsonl here.
                Console.Write(LedgerView.RenderOne(snaps, id));
                break;
            }
        }
        Console.WriteLine();
    }

    /// <summary>
    /// The three write verbs. The RULES live in <see cref="LedgerCommandsWrite"/> and are
    /// tested without a console; this resolves which repo's ledger is meant, appends what
    /// the rules return, and prints the refusal verbatim when they say no.
    ///
    /// <para>Nothing is written on a refusal, because the rules answer with an event
    /// rather than performing one — a rejected transition cannot leave half of itself
    /// behind.</para>
    /// </summary>
    private void HandleLedgerWrite(
        string verb, List<string> rest, List<LedgerRepoSnapshot> snaps,
        List<(string Name, string Root)> repos, string? repoFilter)
    {
        if (rest.Count == 0)
        {
            Log($"usage: ledger {verb} <id>{(verb == "drop" ? " <why>" : verb == "decline" ? " [note]" : "")}");
            return;
        }
        if (!LedgerId.TryParse(rest[0], out var id))
        {
            Log($"ledger {verb}: \"{rest[0]}\" is not an id (expect E-/S-/U-/F-/D-/T- and a number)");
            return;
        }
        var note = string.Join(' ', rest.Skip(1)).Trim();

        // A qualified id names its own repo; otherwise the same scoping every read verb
        // uses — the explicit --repo, else the repo the working directory is in.
        var candidates = id.Repo is { } r
            ? snaps.Where(s => s.Repo.Equals(r, StringComparison.OrdinalIgnoreCase)).ToList()
            : LedgerView.CurrentSnapshots(snaps, repos, Directory.GetCurrentDirectory(), repoFilter).ToList();

        if (candidates.Count == 0) { Log(LedgerView.NoCurrentLedger); return; }

        var snap = candidates[0];
        var actor = "operator";
        var now = DateTimeOffset.UtcNow;

        LedgerEvent? ev;
        string why;
        var ok = verb switch
        {
            "accept" => LedgerCommandsWrite.TryAccept(snap, id, actor, now, out ev, out why),
            "drop" => LedgerCommandsWrite.TryDrop(snap, id, note, actor, now, out ev, out why),
            _ => LedgerCommandsWrite.TryDecline(snap, id, note, actor, now, out ev, out why),
        };
        if (!ok || ev is null) { Log($"ledger {verb}: {why}"); return; }

        var writer = new LedgerWriter(snap.Dir, Log);
        writer.Append(ev);
        Log($"ledger: {snap.Repo}:{ev.Id} {ev.To ?? ev.Event.Replace("task-", "")}" +
            $"{(ev.Note is { Length: > 0 } n ? $" — {n}" : "")}" +
            $"{(ev.Ungated ? "  (ungated — no parent deliverable to gate against)" : "")}");
    }

    private List<ProjectInfo> DiscoverProjects()
    {
        string? mapJson = null;
        try
        {
            if (ProjectsMapPath != null && File.Exists(ProjectsMapPath))
                mapJson = File.ReadAllText(ProjectsMapPath);
        }
        catch (Exception ex) { Log($"projects: cannot read map ({ex.Message}) — repo layer only."); }

        return ProjectMap.Discover(
            _manager.Repos.Select(kv => (kv.Key, kv.Value.Root)), mapJson, Log);
    }

    // `handoffs [@repo] [n]` — the handoff trail, newest first, from the ledger. These
    // are also announced live (`[handoff] a -> b: task`) the moment the mail lands, so
    // this verb is the history, not the only way to see them.
    private void HandleHandoffs(string arg)
    {
        var ledger = Ipc?.Handoffs;
        if (ledger == null) { Log("handoffs: IPC is disabled."); return; }

        string? repo = null;
        var limit = 20;
        foreach (var tok in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.StartsWith("@", StringComparison.Ordinal))
                repo = _manager.ResolveRepoName(tok[1..]) ?? tok[1..];
            else if (int.TryParse(tok, out var n) && n > 0)
                limit = n;
        }

        IEnumerable<HandoffEntry> q = ledger.ReadAll().OrderByDescending(h => h.At);
        if (repo != null)
            q = q.Where(h => HandoffRepoOf(h.From).Equals(repo, StringComparison.OrdinalIgnoreCase)
                          || HandoffRepoOf(h.To).Equals(repo, StringComparison.OrdinalIgnoreCase));

        var shown = q.Take(limit).ToList();
        if (shown.Count == 0) { Log("No handoffs recorded yet."); return; }

        Log($"{shown.Count} handoff(s), newest first:");
        foreach (var h in shown)
        {
            Log($"  {h.At:MM-dd HH:mm}  {h.From} -> {h.To}   {h.Task}");
            if (!string.IsNullOrWhiteSpace(h.State))
                Console.WriteLine($"                        {h.State}");
        }
    }

    // Repo half of an "repo:persona" instance id (whole string if unqualified).
    private static string HandoffRepoOf(string instanceId)
    {
        var i = instanceId.IndexOf(':');
        return i > 0 ? instanceId[..i] : instanceId;
    }

    private List<WorkLedgerClaim> ReadActiveClaims()
    {
        var dir = Ipc?.ClaimsDir;
        if (dir == null || !Directory.Exists(dir)) return new();
        return new WorkLedgerClaims(dir, Log).ReadAll().ToList();
    }

    /// <summary>
    /// `stats [&lt;repo&gt;] [--who] [--since 30d|12h] [html]` — what moved where, who touched
    /// it, how much, and when, for every configured repo.
    ///
    /// Everything comes from corpora huddle already holds (remote-tracking reflogs, git log,
    /// the session roster, claims, the queue, mail, handoffs, logs/git-activity.jsonl), so
    /// the verb answers for the past week the day it ships rather than only from now on.
    /// Nothing here touches the network: local clones are the source.
    /// </summary>
    private void HandleStats(string arg)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var now = DateTimeOffset.Now;
        var since = now.AddDays(-_manager.Config.Settings.Int("statsSinceDays"));
        bool who = false, html = false; string? repoFilter = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "--who") { who = true; continue; }
            if (tokens[i] == "html") { html = true; continue; }
            if (tokens[i] == "--since" && i + 1 < tokens.Count)
            {
                if (!StatsView.TryParseSince(tokens[++i], now, out since)) { Log("stats: --since takes 30d or 12h"); return; }
                continue;
            }
            repoFilter = tokens[i];
        }

        var stateFile = StateFile ?? Path.Combine("logs", "state.json");
        var logsDir = Path.GetDirectoryName(stateFile) ?? "logs";
        var roster = SessionState.LoadEntries(stateFile).Select(RosterWindow.From).ToList();
        var activity = new GitActivityLog(Path.Combine(logsDir, "git-activity.jsonl")).ReadSince(since);
        var claims = ReadActiveClaims();
        var units = Orchestrator?.Queue.All().Select(x => x.unit).ToList() ?? new List<WorkUnit>();
        var handoffs = new HandoffLedger(Path.Combine(logsDir, "handoffs.jsonl")).ReadAll();

        // Mail volume per repo: every mailbox whose safe-name starts "<repo>_", counted by
        // file mtime so --since bounds this corpus too rather than reporting all history.
        int MailFor(string repo)
        {
            var prefix = repo.Replace(':', '_') + "_";
            var ipc = Ipc?.IpcDir ?? "ipc";
            if (!Directory.Exists(ipc)) return 0;
            return Directory.GetDirectories(ipc).Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Sum(d => new[] { "inbox", "processed" }.Sum(sub => Directory.Exists(Path.Combine(d, sub))
                    ? Directory.GetFiles(Path.Combine(d, sub)).Count(f => File.GetLastWriteTime(f) >= since.LocalDateTime) : 0));
        }
        var src = new StatsSources(roster, activity, claims, units, MailFor, handoffs);

        var repos = _manager.Config.Sessions.Where(s => repoFilter == null || s.Name.Equals(repoFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (repos.Count == 0) { Log($"stats: no repo '{repoFilter}'"); return; }
        var snaps = repos.Select(r => RepoStatsCollector.Collect(r.Name, r.Root, since, now, src)).ToList();

        if (html) { HandleStatsHtml(snaps, since, now); return; }
        Console.WriteLine();
        Console.Write(who ? StatsView.RenderWho(snaps) : StatsView.RenderAll(snaps, since, now));
        Console.WriteLine();
    }

    private void HandleStatsHtml(IReadOnlyList<RepoStatsSnapshot> snaps, DateTimeOffset since, DateTimeOffset now)
    {
        // The heatmap is a YEAR of commits regardless of --since: a 7-day window would
        // render 51 empty columns and say nothing. Only the graph is widened — every
        // other figure on the page stays inside the window the operator asked for.
        var wide = snaps.Select(s =>
        {
            if (s.Commits == null) return s;
            return s with { Commits = s.Commits with { CommitTimes = GitLogStats.CommitTimesSince(s.Root, now.AddDays(-364)) } };
        }).ToList();

        var outPath = Path.Combine(Path.GetDirectoryName(StateFile ?? ".") ?? ".", "stats.html");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, StatsView.RenderHtml(wide, since, now, $"huddle {BuildInfo.Short}"));
            Log($"stats: wrote {wide.Count} repo(s) -> {Hyperlink(outPath, outPath)}");
            Log($"  open it: shell {outPath}");
        }
        catch (Exception ex)
        {
            Log($"stats: html write failed — {ex.Message}");
        }
    }

    private void HandleProjects(string arg)
    {
        var a = arg.Trim();
        if (a.StartsWith("html", StringComparison.OrdinalIgnoreCase))
        {
            HandleProjectsHtml(a["html".Length..].Trim());
            return;
        }
        if (a.Length > 0)
        {
            Log("Usage: projects [html [path]]   (or 'project <slug>' for detail)");
            return;
        }

        var projects = DiscoverProjects();
        if (projects.Count == 0)
        {
            Log("No projects found. A project = docs/projects/<slug>/project.md in a registered repo (see the projects spec).");
            return;
        }

        var claims = ReadActiveClaims();
        Log($"{projects.Count} project(s):");
        foreach (var p in projects)
        {
            var live = _manager.Instances.Values.Count(i => i.IsAlive &&
                string.Equals(i.Project, p.Slug, StringComparison.OrdinalIgnoreCase));
            var held = claims.Count(c => string.Equals(c.Project, p.Slug, StringComparison.OrdinalIgnoreCase));

            var extras = "";
            if (p.MapOnly) extras += "  (map-only — no project.md found)";
            else
            {
                if (p.SprintId != null) extras += $"  sprint {p.SprintId}" + (p.SprintVersion != null ? $" ({p.SprintVersion})" : "");
                if (live > 0) extras += $"  {live} live";
                if (held > 0) extras += $"  {held} claim(s)";
                if (p.MapNotes != null || p.MapLinks.Count > 0) extras += "  map";
                var moreRepos = p.Repos.Count - 1;
                if (moreRepos > 0) extras += $"  +{moreRepos} repo(s)";
            }

            Log($"  {p.Slug,-14} {p.Status,-8} {p.Title}  ({(p.MapOnly ? "-" : p.HomeRepo)}){extras}");
            if (p.Warning != null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"                 ! {p.Warning}");
                Console.ResetColor();
            }
        }
        Log("Use 'project <slug>' for detail.");
    }

    // `projects html [path]` — render the lens to a self-contained HTML page.
    // The reproducible output demo: regenerated from live data on every run.
    private void HandleProjectsHtml(string pathArg)
    {
        var projects = DiscoverProjects();
        var claims = ReadActiveClaims();

        var entries = projects.Select(p => new ProjectReportEntry(
            p,
            GatherAgents(p.Slug),
            claims.Where(c => string.Equals(c.Project, p.Slug, StringComparison.OrdinalIgnoreCase)).ToList()
        )).ToList();

        var outPath = pathArg.Length > 0
            ? Path.GetFullPath(pathArg)
            : Path.Combine(
                Path.GetDirectoryName(StateFile ?? ".") ?? ".",
                "workspace", "projects-report.html");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, ProjectReport.Render(entries, $"huddle {BuildInfo.Short}"));
            Log($"projects: wrote {entries.Count} project(s) -> {Hyperlink(outPath, outPath)}");
            Log($"  open it: shell {outPath}");
        }
        catch (Exception ex)
        {
            Log($"projects: html write failed — {ex.Message}");
        }
    }

    // The usual suspects for a project: agents that work/worked on it, newest state
    // first (live > recoverable > past), deduped by instance id. Sources: the live
    // registry, the crash-recovery roster, and the state archive — history deepens
    // as project stamps accumulate.
    private List<ProjectAgent> GatherAgents(string slug)
    {
        var agents = new List<ProjectAgent>();
        bool Match(string? project) => string.Equals(project, slug, StringComparison.OrdinalIgnoreCase);

        foreach (var i in _manager.Instances.Values.Where(i => i.IsAlive && Match(i.Project)))
            agents.Add(new ProjectAgent(i.InstanceId, i.ActivePersona, i.DeclaredPurpose, "live", i.StartedAt));

        foreach (var r in _manager.Recoverable.Where(r => Match(r.Project)))
            agents.Add(new ProjectAgent(r.InstanceId, r.Persona, r.DeclaredPurpose, "recoverable", r.DiedAt));

        // Past: the state archive (recover/dismiss outcomes) — one JSON object per line.
        try
        {
            var archive = Path.Combine(Path.GetDirectoryName(StateFile ?? ".") ?? ".", "state-archive.jsonl");
            if (File.Exists(archive))
            {
                foreach (var line in File.ReadLines(archive))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(line);
                        if (!doc.RootElement.TryGetProperty("entry", out var entry)) continue;
                        var project = entry.TryGetProperty("project", out var pj) ? pj.GetString() : null;
                        if (!Match(project)) continue;
                        var when = doc.RootElement.TryGetProperty("archivedAt", out var at) &&
                                   at.ValueKind == System.Text.Json.JsonValueKind.String &&
                                   DateTime.TryParse(at.GetString(), out var t) ? t : (DateTime?)null;
                        agents.Add(new ProjectAgent(
                            entry.TryGetProperty("instanceId", out var id) ? id.GetString() ?? "?" : "?",
                            entry.TryGetProperty("persona", out var pe) ? pe.GetString() : null,
                            entry.TryGetProperty("declaredPurpose", out var dp) ? dp.GetString() : null,
                            "past", when));
                    }
                    catch (Exception) { /* one bad line never kills the roster */ }
                }
            }
        }
        catch (Exception) { /* archive unreadable — live + recoverable still shown */ }

        // Dedupe by instance id: strongest state wins (live > recoverable > past).
        static int Rank(string s) => s switch { "live" => 0, "recoverable" => 1, _ => 2 };
        return agents
            .GroupBy(a => a.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => Rank(a.State)).ThenByDescending(a => a.LastSeen).First())
            .OrderBy(a => Rank(a.State)).ThenByDescending(a => a.LastSeen)
            .ToList();
    }

    private void HandleProjectDetail(string arg)
    {
        var slug = arg.Trim();
        if (slug.Length == 0) { Log("Usage: project <slug>"); return; }

        var p = DiscoverProjects().FirstOrDefault(x =>
            x.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
        if (p == null) { Log($"project: no project '{slug}' — 'projects' lists what exists."); return; }

        Log($"{p.Slug} — {p.Title}  [{p.Status}]{(p.MapOnly ? "  (map-only)" : "")}");
        if (!string.IsNullOrEmpty(p.Goal)) Log($"  goal: {p.Goal}");
        if (p.Repos.Count > 0) Log($"  repos: {string.Join(", ", p.Repos)}   home: {p.HomeRepo}");
        if (p.SprintId != null) Log($"  sprint: {p.SprintId}{(p.SprintVersion != null ? $"  version: {p.SprintVersion}" : "")}");
        if (p.Warning != null) Log($"  ! {p.Warning}");
        if (p.MapNotes != null) Log($"  map notes: {p.MapNotes}");
        foreach (var link in p.MapLinks) Log($"  link: {link}");

        // Typed artifacts + declared children load into _lastDocs so `open <n>` works.
        _findMap = null;
        _lastDocs = new List<DocumentEntry>();
        if (!p.MapOnly)
        {
            var projectDoc = Path.Combine(p.Dir, "project.md");
            _lastDocs.Add(new DocumentEntry("project.md", projectDoc, p.Slug, p.HomeRepo, null, DocLevel.Output, "project"));
            foreach (var t in p.TypedArtifacts)
                _lastDocs.Add(new DocumentEntry(t, Path.Combine(p.Dir, t), p.Slug, p.HomeRepo, null, DocLevel.Output, "typed"));
        }
        foreach (var (repoName, path) in FindDeclaredChildren(p.Slug))
        {
            if (_lastDocs.Any(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue; // hand-listed/typed already covers it
            _lastDocs.Add(new DocumentEntry(Path.GetFileName(path), path, p.Slug, repoName, null, DocLevel.Output, "declared"));
        }
        _docsPageOffset = _lastDocs.Count;

        if (_lastDocs.Count > 0)
        {
            Log($"  artifacts ({_lastDocs.Count}) — 'open <n>':");
            for (var i = 0; i < _lastDocs.Count; i++)
                Log($"  {i + 1,3}. {Hyperlink(_lastDocs[i].Title, _lastDocs[i].Path)}  ({_lastDocs[i].Repo}, {_lastDocs[i].Note})");
        }

        // Live bindings — derived fresh, never stored.
        var liveSessions = _manager.Instances.Values
            .Where(i => i.IsAlive && string.Equals(i.Project, p.Slug, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var s in liveSessions)
            Log($"  live: {s.InstanceId}  ({s.FormatUptime()})");

        foreach (var c in ReadActiveClaims().Where(c =>
                     string.Equals(c.Project, p.Slug, StringComparison.OrdinalIgnoreCase)))
            Log($"  claim: {c.SessionId} holds {c.Files.Count} file(s) ({c.BatchId})");

        foreach (var r in _manager.Recoverable.Where(r =>
                     string.Equals(r.Project, p.Slug, StringComparison.OrdinalIgnoreCase)))
            Log($"  recoverable: {r.InstanceId} — 'recover' to relaunch");
    }

    // Frontmatter-declared children: any docs/**/*.md in a registered repo whose
    // frontmatter says `project: <slug>`. Bounded: first 2KB per file is enough to
    // cover any sane frontmatter block; unreadable files are skipped.
    private IEnumerable<(string Repo, string Path)> FindDeclaredChildren(string slug)
    {
        foreach (var (repoName, def) in _manager.Repos.Select(kv => (kv.Key, kv.Value)))
        {
            var docsDir = Path.Combine(def.Root, "docs");
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(docsDir)) continue;
                files = Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories);
            }
            catch (Exception) { continue; }

            foreach (var f in files)
            {
                string head;
                try
                {
                    using var reader = new StreamReader(f);
                    var buf = new char[2048];
                    var n = reader.Read(buf, 0, buf.Length);
                    head = new string(buf, 0, n);
                }
                catch (Exception) { continue; }

                if (!head.StartsWith("---")) continue;
                var fm = ProjectMap.ParseFrontmatter(head);
                if (fm.TryGetValue("project", out var declared) &&
                    declared.Equals(slug, StringComparison.OrdinalIgnoreCase))
                    yield return (repoName, f);
            }
        }
    }

    // ---- `recover` — crash-recovery roster, show & pick (I010 F2) --------------
    // Spec: docs/superpowers/specs/2026-08-09-oracle-recovery-design.md
    // Dead sessions retained by SessionState.Recover are listed with declared
    // purpose (fallback: transcript opening prompt) and last evidence; `recover <n>`
    // relaunches via the resume spawn path; dismiss archives without relaunching.
    // Entries are never deleted — they move to logs/state-archive.jsonl.

    private List<SessionStateEntry>? _recoverMap;

    // Set by Program at startup; the archive lives next to it.
    public string? StateFile { get; set; }

    private void HandleRecover(string arg)
    {
        var a = arg.Trim();

        if (a.Length == 0) { PrintRecoverList(); return; }

        if (a.StartsWith("dismiss", StringComparison.OrdinalIgnoreCase))
        {
            var rest = a["dismiss".Length..].Trim();
            if (rest.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = RecoverSnapshot();
                foreach (var e in snapshot) ArchiveRosterEntry(e, "dismissed");
                Log($"recover: dismissed {snapshot.Count} entr(y/ies) — archived, not deleted.");
                _recoverMap = null;
                return;
            }
            if (int.TryParse(rest, out var dn))
            {
                var entry = MapEntry(dn);
                if (entry == null) return;
                ArchiveRosterEntry(entry, "dismissed");
                Log($"recover: dismissed '{entry.InstanceId}' — archived.");
                _recoverMap = null;
                return;
            }
            Log("Usage: recover dismiss <n|all>");
            return;
        }

        if (a.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = RecoverSnapshot();
            if (snapshot.Count == 0) { Log("recover: nothing recoverable."); return; }
            var launched = 0;
            foreach (var e in snapshot)
                if (RecoverOne(e)) launched++;
            Log($"recover: relaunched {launched}/{snapshot.Count}.");
            _recoverMap = null;
            return;
        }

        if (int.TryParse(a, out var n))
        {
            var entry = MapEntry(n);
            if (entry == null) return;
            if (RecoverOne(entry)) _recoverMap = null;
            return;
        }

        Log("Usage: recover [n|all|dismiss <n|all>]");
    }

    // The listed order (the map) — built by PrintRecoverList; `recover all` uses the
    // roster directly when no listing was printed this session.
    private List<SessionStateEntry> RecoverSnapshot() =>
        _recoverMap != null ? new(_recoverMap) : new(_manager.Recoverable);

    private SessionStateEntry? MapEntry(int n)
    {
        var map = _recoverMap ?? _manager.Recoverable;
        if (n < 1 || n > map.Count)
        {
            Log(map.Count == 0
                ? "recover: nothing recoverable."
                : $"recover: {n} is out of range (1..{map.Count}) — run 'recover' to list.");
            return null;
        }
        return map[n - 1];
    }

    private void PrintRecoverList()
    {
        var roster = _manager.Recoverable;
        if (roster.Count == 0) { Log("recover: nothing recoverable."); return; }

        var store = CreateTranscriptStore();

        // Topology (F2): hubs — dispatchers and sessions with mail waiting — list
        // first so `recover all` brings coordinators up before their workers.
        var ipcRoot = Ipc?.IpcDir;
        var processedDir = ipcRoot != null ? Path.Combine(ipcRoot, "_huddle", "processed") : null;
        var topo = new Dictionary<SessionStateEntry, TopologyInfo>();
        foreach (var e in roster)
        {
            topo[e] = ipcRoot != null && processedDir != null
                ? RecoveryTopology.Analyze(e.InstanceId, e.StartedAt, processedDir, ipcRoot)
                : new TopologyInfo(null, 0, false);
        }
        _recoverMap = roster.OrderByDescending(e => topo[e].IsHub).ToList();

        Log($"{roster.Count} session(s) recoverable — 'recover <n>' to relaunch, 'recover all', 'recover dismiss <n|all>':");
        for (var i = 0; i < _recoverMap.Count; i++)
        {
            var e = _recoverMap[i];
            var purpose = e.DeclaredPurpose;
            DateTime? lastEvidence = e.DiedAt;

            // Fallbacks come from the transcript — never fatal when it's missing.
            if (e.SessionId != null)
            {
                try
                {
                    var detail = store.GetDetail(e.SessionId);
                    if (detail != null)
                    {
                        if (string.IsNullOrWhiteSpace(purpose))
                            purpose = detail.Summary.OpeningPrompt;
                        lastEvidence = detail.Summary.LastActivity ?? lastEvidence;
                    }
                }
                catch { /* unreadable transcript: show what state.json knows */ }
            }

            purpose = string.IsNullOrWhiteSpace(purpose) ? "(unknown)" : purpose.Replace('\r', ' ').Replace('\n', ' ');
            if (purpose.Length > 100) purpose = purpose[..100] + "…";
            var personaLabel = e.Persona != null ? $" [{e.Persona}]" : "";

            var t = topo[e];
            var marks = "";
            if (!string.IsNullOrEmpty(e.Project)) marks += $"  [{e.Project}]";
            if (t.IsHub) marks += t.UnreadMail > 0 ? $"  HUB ({t.UnreadMail} waiting)" : "  HUB";
            if (t.DispatchedBy != null) marks += $"  ← dispatched by {t.DispatchedBy}";

            Log($"{i + 1,3}. {e.InstanceId}{personaLabel}  ({e.RepoName}){marks}");
            Log($"     task: {purpose}");
            Log($"     last: {Ago(lastEvidence)}   resume: claude --resume {e.SessionId ?? "(none)"}");
        }
    }

    private bool RecoverOne(SessionStateEntry entry)
    {
        if (entry.SessionId == null)
        {
            Log($"recover: '{entry.InstanceId}' has no session id — nothing to resume. 'recover dismiss' to archive it.");
            return false;
        }
        var repoName = _manager.ResolveRepoName(entry.RepoName);
        if (!_manager.Repos.TryGetValue(repoName, out var def))
        {
            Log($"recover: repo '{entry.RepoName}' is not registered — cannot pick a working directory.");
            return false;
        }
        if (!_manager.ResumeTranscript(entry.SessionId, def.Root))
            return false; // ResumeTranscript logs why (incl. the still-live guard)

        ArchiveRosterEntry(entry, "recovered");
        Log($"recover: relaunched '{entry.InstanceId}' — {entry.SessionId} in {def.Root}");
        return true;
    }

    // Remove from the roster, append to logs/state-archive.jsonl, persist state.
    private void ArchiveRosterEntry(SessionStateEntry entry, string outcome)
    {
        _manager.Recoverable.Remove(entry);
        try
        {
            if (StateFile != null)
            {
                var archive = Path.Combine(Path.GetDirectoryName(StateFile) ?? ".", "state-archive.jsonl");
                var record = System.Text.Json.JsonSerializer.Serialize(new
                {
                    archivedAt = DateTime.Now,
                    outcome,
                    entry
                });
                File.AppendAllText(archive, record + Environment.NewLine);
                SessionState.Save(StateFile, _manager.Instances, _manager.Recoverable);
            }
        }
        catch (Exception ex)
        {
            Log($"recover: archive write failed ({ex.Message}) — entry removed from roster for this run only.");
        }
    }

    private static string Ago(DateTime? t)
    {
        if (t == null) return "(unknown)";
        var span = DateTime.Now - t.Value;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m ago";
        return $"{(int)span.TotalDays}d ago";
    }

    private TranscriptStore CreateTranscriptStore()
    {
        var projectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var roots = _manager.Repos.ToDictionary(kv => kv.Key, kv => kv.Value.Root, StringComparer.OrdinalIgnoreCase);
        return new TranscriptStore(projectsRoot, roots, Log,
            _manager.Config.Settings.Int("transcriptMaxScan"));
    }

    private void HandleHistory(string arg)
    {
        var a = arg.Trim();

        if (a.Equals("more", StringComparison.OrdinalIgnoreCase))
        {
            if (_lastHistory.Count == 0) { Log("No listing to continue — run 'history' first."); return; }
            if (_historyPageOffset >= _lastHistory.Count) { Log("End of list — all sessions shown."); return; }
            _findMap = null;                    // paging a plain listing → legacy numbering
            PrintHistoryPage();
            return;
        }

        // `history <n>` — detail view for a row of the last listing. This is the one route
        // that must NOT clear the map: after a find, <n> is a shared display number and
        // PrintHistoryDetail translates it. Every other route below builds a fresh listing.
        if (int.TryParse(a, out var idx))
        {
            PrintHistoryDetail(idx);
            return;
        }
        _findMap = null;

        // Filters: @repo, -N{h,d,w}, remaining tokens = keyword (docs grammar).
        DateTime? cutoff = null;
        string? window = null;
        string? repoFilter = null;
        var keywordParts = new List<string>();
        foreach (var t in a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.StartsWith('@') && t.Length > 1)
            {
                repoFilter = _manager.ResolveRepoName(t[1..].ToLowerInvariant());
                continue;
            }
            var c = ParseWindow(t);
            if (c.HasValue) { cutoff = c; window = t.TrimStart('-'); }
            else keywordParts.Add(t);
        }
        var keyword = string.Join(' ', keywordParts).Trim();

        var store = CreateTranscriptStore();
        _lastHistory = store.ListSessions(new HistoryFilter(repoFilter, keyword.Length > 0 ? keyword : null, cutoff)).ToList();
        _historyPageOffset = 0;

        if (_lastHistory.Count == 0)
        {
            var qual = new List<string>();
            if (repoFilter != null) qual.Add($"in repo '{repoFilter}'");
            if (keyword.Length > 0) qual.Add($"matching '{keyword}'");
            if (window != null) qual.Add($"in the last {window}");
            Log(qual.Count > 0 ? $"No sessions {string.Join(" ", qual)}." : "No session transcripts found.");
            return;
        }

        PrintHistoryPage();
        var summary = $"{_lastHistory.Count} session(s)";
        if (repoFilter != null) summary += $" in {repoFilter}";
        if (keyword.Length > 0) summary += $" matching '{keyword}'";
        if (window != null) summary += $" in the last {window}";
        if (store.LastListTruncated) summary += $" (newest {store.MaxScan} transcripts scanned)";
        Log($"{summary}. 'history <n>' for detail, 'resume <n>' to reopen.");
    }

    private void PrintHistoryPage()
    {
        Console.WriteLine();
        var end = Math.Min(_historyPageOffset + HistoryPageSize, _lastHistory.Count);
        for (var i = _historyPageOffset; i < end; i++)
        {
            var s = _lastHistory[i];
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {i + 1,3}. ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"[{s.Repo}]".PadRight(14));
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($" {s.Title,-70}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {FormatWhen(s.LastActivity)}");
                if (s.FileCount > 0) Console.Write($"  {s.FileCount} file(s)");
                Console.WriteLine();
            }
            finally { Console.ResetColor(); }
        }
        _historyPageOffset = end;
        var remaining = _lastHistory.Count - end;
        if (remaining > 0)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  … {remaining} more — 'history more' for the next {Math.Min(HistoryPageSize, remaining)}");
            }
            finally { Console.ResetColor(); }
        }
        Console.WriteLine();
    }

    private void PrintHistoryDetail(int n)
    {
        var displayN = n;                 // what the operator typed; a shared number under a find map
        if (_findMap != null)
        {
            if (_findMap.Resolve(n) is not { } slot)
            {
                Log($"Usage: history <n>  (1..{_findMap.Count} of the find listing)");
                return;
            }
            if (slot.kind == FindMap.Kind.Doc)
            {
                Log($"{n} is a document — use 'open {n}'.");
                return;
            }
            n = slot.index + 1;   // fall through to the legacy body with the backing index
        }
        if (_lastHistory.Count == 0) { Log("No listing — run 'history' first."); return; }
        if (n < 1 || n > _lastHistory.Count) { Log($"Usage: history <n>  (1..{_lastHistory.Count})"); return; }

        var s = _lastHistory[n - 1];
        var detail = CreateTranscriptStore().GetDetail(s.Id);
        if (detail == null) { Log($"history: transcript for {s.Id} no longer readable."); return; }

        Console.WriteLine();
        try
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  {s.Title}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  {s.Repo} · {s.Cwd} · {FormatStamp(s.StartedAt)} → {FormatStamp(s.LastActivity)} · session {s.Id[..Math.Min(8, s.Id.Length)]}");
            Console.WriteLine();
            if (s.OpeningPrompt.Length > 0)
                Console.WriteLine($"  Started with:  \"{s.OpeningPrompt}\"");
            if (detail.LastPrompt.Length > 0)
                Console.WriteLine($"  Left off at:   \"{detail.LastPrompt}\"");

            if (detail.Files.Count > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"  Files this session wrote ({detail.Files.Count}):");
                // Load into _lastDocs so the existing `open <n>` works unchanged. Replacing
                // the list invalidates any find map — the numbers printed below are 1..N
                // into the new list, so `open <n>` reverts to legacy numbering here.
                _findMap = null;
                _lastDocs = detail.Files.Select(f => new DocumentEntry(
                    Title: Path.GetFileName(f), Path: f, SourceSession: s.Id[..Math.Min(8, s.Id.Length)],
                    Repo: s.Repo, Timestamp: null, Level: DocLevel.Output, Note: "history")).ToList();
                _docsPageOffset = _lastDocs.Count; // listing fully shown here; 'docs more' N/A
                var shown = Math.Min(detail.Files.Count, 20);
                for (var i = 0; i < shown; i++)
                {
                    var exists = File.Exists(detail.Files[i]);
                    Console.ForegroundColor = exists ? ConsoleColor.Gray : ConsoleColor.DarkGray;
                    Console.WriteLine($"    {i + 1,3}. {Hyperlink(detail.Files[i], detail.Files[i])}{(exists ? "" : "  (gone)")}");
                }
                if (detail.Files.Count > shown)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"    … {detail.Files.Count - shown} more");
                }
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
            // With the map cleared above, resume takes the backing index; while it is still
            // live, resume translates through it and wants the number the operator typed.
            var resumeN = _findMap == null ? n : displayN;
            Console.WriteLine($"  → 'open <n>' to open a file · 'resume {resumeN}' to reopen this conversation");
        }
        finally { Console.ResetColor(); }
        Console.WriteLine();
    }

    private void HandleHistoryResume(int n)
    {
        if (_findMap != null)
        {
            if (_findMap.Resolve(n) is not { } slot)
            {
                Log($"Usage: resume <n>  (1..{_findMap.Count} of the find listing)");
                return;
            }
            if (slot.kind == FindMap.Kind.Doc)
            {
                Log($"{n} is a document — use 'open {n}'.");
                return;
            }
            // Holds by construction (slots are written alongside _lastHistory); the guard
            // keeps the invariant local rather than spread across the call sites.
            if (slot.index >= _lastHistory.Count)
            {
                Log($"Usage: resume <n>  (1..{_findMap.Count} of the find listing)");
                return;
            }
            var found = _lastHistory[slot.index];
            _manager.ResumeTranscript(found.Id, found.Cwd);
            return;
        }
        if (_lastHistory.Count == 0) { Log("No listing — run 'history' first (resume <n> picks from it)."); return; }
        if (n < 1 || n > _lastHistory.Count) { Log($"Usage: resume <n>  (1..{_lastHistory.Count})"); return; }
        var s = _lastHistory[n - 1];
        _manager.ResumeTranscript(s.Id, s.Cwd);
    }

    private static string FormatWhen(DateTime? t)
    {
        if (!t.HasValue) return "unknown";
        var d = DateTime.Now.Date - t.Value.Date;
        if (d.TotalDays < 1) return $"today {t:HH:mm}";
        if (d.TotalDays < 2) return $"yesterday {t:HH:mm}";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} days ago";
        return $"{t:yyyy-MM-dd}";
    }

    private static string FormatStamp(DateTime? t) => t.HasValue ? $"{t:yyyy-MM-dd HH:mm}" : "?";

    // Set once at startup (Program.Main) from VtConsole.TryEnable(). When the console
    // can't process VT sequences (legacy conhost without VT, redirected output), the
    // OSC 8 escapes would print as literal garbage — so Hyperlink() degrades to plain
    // text and listings stay readable. `open <n>` still opens entries either way.
    public static bool HyperlinksEnabled = true;

    // OSC 8 hyperlink escape sequence: ESC ]8;;URI ST  text  ESC ]8;; ST
    // Build the file URI with new Uri(...).AbsoluteUri — it percent-encodes spaces,
    // normalizes backslashes, and adds the drive letter. Do NOT string-concat
    // "file:///" + path: that breaks for any artifact path with a space or backslash.
    private static string Hyperlink(string path, string text)
    {
        if (!HyperlinksEnabled) return text;
        string uri;
        try { uri = new Uri(path).AbsoluteUri; }
        catch { uri = path; }
        return $"]8;;{uri}\\{text}]8;;\\";
    }

    // `reload` — rebuild huddle and relaunch without killing anything. Spawns a detached
    // helper (build-restart.cmd) that waits for THIS process to exit, rebuilds, then starts
    // a fresh instance; we then exit via the normal graceful quit path (orchestrator + IPC
    // disposed, child claude sessions left running). ARIA-style exit/wait/build/restart —
    // the build happens during the wait, after the publish lock releases. Confirmed first
    // because it tears down the orchestrator. Returns true if the caller should now quit.
    // `settings` / `settings <key>` / `settings <key> <value>` / `settings unset <key>`.
    // Reads resolve from the config loaded at startup; writes go straight to huddle.json
    // through the same validated write-back the CLI uses, so the two cannot disagree
    // about what is legal.
    private void HandleSettings(string arg)
    {
        // Split ONCE: the first token is the key (or `unset`), and EVERYTHING after it is
        // the value, spaces included. Splitting into three dropped the tail, so
        // `settings backoffSeconds 2, 5, 15` silently wrote "2," (S5).
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            foreach (var line in SettingsCli.Render(_manager.Config.Settings, ConfigPath).Split('\n'))
                Console.WriteLine(line.TrimEnd('\r'));
            return;
        }

        var head = parts[0];
        var rest = parts.Length > 1 ? parts[1].Trim() : "";

        if (head.Equals("unset", StringComparison.OrdinalIgnoreCase))
        {
            // `unset` names a key; without one it is a usage error, not a setting called
            // "unset" (S5).
            var key = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (key == null) { Log("settings: usage — settings unset <key>"); return; }
            if (SettingsWriter.TryUnset(ConfigPath, key, out var uerr))
            {
                // peekHotkey applies its unset on the spot, exactly as its set path does.
                // Unsetting is the operator's way out of a failed chord experiment, and
                // sending them to `reload` for the one key that needs no reload made the
                // feature's headline claim true going in and false coming out. Unset means
                // "go back to letting huddle choose", so the candidate walk is what runs,
                // and the switch's own message says which chord that landed on.
                if (SettingsCatalog.TryGet(key, out var ud)
                    && ud.Key.Equals("peekHotkey", StringComparison.OrdinalIgnoreCase)
                    && PeekHotkeys != null)
                {
                    Log($"settings: unset {ud.Key} — back to the built-in candidate chords");
                    PeekHotkeys.TrySetFirstAvailable(PeekChord.Candidates, out var unsetMessage);
                    Log(unsetMessage);
                }
                else Log($"settings: unset {key} — reverts to default; takes effect on reload");
            }
            else Log($"settings: refused — {uerr}");
            return;
        }

        if (rest.Length == 0)
        {
            if (!SettingsCatalog.TryGet(head, out var d)) { Log($"settings: unknown setting \"{head}\""); return; }
            var r = _manager.Config.Settings.Get(d.Key);

            // peekHotkey is the one key this process can change without a reload, so the
            // startup-loaded config is NOT the truth for it: after `settings peekHotkey X`
            // the chord is X and the config object still says whatever it said at launch.
            // Reporting that stale value is how an operator sets a chord, checks it, sees
            // the old one and concludes the change failed. Ask the thing that owns it.
            if (d.Key.Equals("peekHotkey", StringComparison.OrdinalIgnoreCase) && PeekHotkeys != null)
            {
                var bound = PeekHotkeys.Active ? PeekHotkeys.Chord : "nothing bound";
                Log($"{d.Key} = {bound}  (live, {d.Kind})  {d.Help}");
                return;
            }

            Log($"{d.Key} = {r.Value}  ({r.Source}, {d.Applies}, {d.Kind}{(d.Kind == SettingKind.Int ? $" {d.Min}..{d.Max}" : "")})  {d.Help}");
            return;
        }

        // Even a Live setting only re-resolves when the config is reloaded — the
        // message says so rather than implying an effect that has not happened.
        //
        // peekHotkey is the one exception: the switch re-registers the chord on this
        // process, so the reload wording would be plainly wrong for it. Its own message
        // replaces the suffix, because only the switch knows whether the new chord was
        // actually granted.
        if (SettingsWriter.TrySet(ConfigPath, head, rest, out var err, out var def))
        {
            if (def!.Key.Equals("peekHotkey", StringComparison.OrdinalIgnoreCase) && PeekHotkeys != null)
            {
                Log($"settings: set {def.Key} = {rest}");
                PeekHotkeys.TrySet(rest, out var hotkeyMessage);
                Log(hotkeyMessage);
            }
            else
                Log($"settings: set {def.Key} = {rest}" +
                    (def.Applies == SettingApplies.Startup ? " — takes effect on reload" : " — live on next read after reload"));
        }
        else Log($"settings: refused — {err}");
    }

    private bool HandleReload(string arg)
    {
        // Killing a live orchestrator with sessions attached over a typo would be worse
        // than the typo: validate huddle.json BEFORE this process agrees to exit, and
        // leave the running settings in force when it will not load.
        //
        // EVERY load failure, not just SettingsException — a trailing comma raises
        // JsonException, a deleted file FileNotFoundException, and those used to escape
        // and kill huddle outright, which is the exact outcome this guard exists to
        // prevent (S2). Whatever went wrong, the answer is the same: refuse and stay up.
        try
        {
            HuddleConfig.Load(ConfigPath);
        }
        catch (SettingsException ex)
        {
            Log("reload: refused — huddle.json settings would not load; running settings stay in force:");
            foreach (var e in ex.Errors) Log($"  {e}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"reload: refused — {Path.GetFullPath(ConfigPath)} would not load; running settings stay in force:");
            Log($"  {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        var repoRoot = Directory.GetCurrentDirectory();
        var helper = Path.Combine(repoRoot, "build-restart.cmd");
        if (!File.Exists(helper))
        {
            Log($"reload: helper not found at {helper} — staying up.");
            return false;
        }

        // `reload /y` (or -y / y / yes) skips the confirmation prompt.
        var a = arg.Trim().TrimStart('/', '-').ToLowerInvariant();
        var skipPrompt = a is "y" or "yes";

        if (!skipPrompt)
        {
            Console.Write("Rebuild huddle and relaunch? Child sessions keep running. [y/N]: ");
            var ans = Console.ReadLine()?.Trim();
            if (!string.Equals(ans, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ans, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Log("reload: cancelled.");
                return false;
            }
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{helper}\" {Environment.ProcessId}\"",
                WorkingDirectory = repoRoot,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            Log($"reload: helper launched (waits for pid {Environment.ProcessId}, rebuilds, relaunches). Exiting now — sessions keep running.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"reload: failed to launch helper — {ex.Message}. Staying up.");
            return false;
        }
    }

    /// <summary>Result of parsing a `broadcast` command line.</summary>
    public readonly record struct BroadcastParse(string? RepoCsv, string Subject, string Message);

    // `broadcast [@repo[,repo]] <message>` — everything after the optional @repo
    // prefix is the message, verbatim. The subject is derived (first few words)
    // purely as a log/list label, so no typed text is ever dropped from the body.
    // Returns null when there is no message to send.
    public static BroadcastParse? ParseBroadcast(string arg)
    {
        arg = (arg ?? "").Trim();
        string? repoCsv = null;
        if (arg.StartsWith('@'))
        {
            var sp = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            // Only consume the first token when it actually named a repo. A bare
            // "@" names nothing, so it stays part of the message rather than
            // being silently swallowed.
            if (sp.Length > 0 && sp[0].Length > 1)
            {
                repoCsv = sp[0][1..];
                arg = sp.Length > 1 ? sp[1].Trim() : "";
            }
        }
        if (string.IsNullOrWhiteSpace(arg)) return null;
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var subject = string.Join(' ', words.Take(6));
        return new BroadcastParse(repoCsv, subject, arg);
    }

    private void HandleBroadcast(string arg)
    {
        if (Orchestrator == null || Ipc == null)
        {
            Log("Orchestrator not active. Enable 'ipc' in huddle.json.");
            return;
        }

        var parsed = ParseBroadcast(arg);
        if (parsed is null)
        {
            Log("Usage: broadcast [@repo[,repo]] <message>");
            return;
        }
        var p = parsed.Value;

        // Synthesize a broadcast command into the orchestrator's inbox so it
        // flows through the same code path as IPC-originated broadcasts.
        var subjJson = System.Text.Json.JsonSerializer.Serialize(p.Subject);
        var msgJson = System.Text.Json.JsonSerializer.Serialize(p.Message);
        var repoJson = p.RepoCsv is null ? "" : $",\"repo\":{System.Text.Json.JsonSerializer.Serialize(p.RepoCsv)}";
        var body = $"{{\"subject\":{subjJson},\"body\":{msgJson},\"type\":\"info\",\"targets\":\"all\"{repoJson}}}";
        Ipc.Send("_huddle_console", Orchestrator.HuddleMailbox, "broadcast", body, "command");
        Log(p.RepoCsv is null ? $"Broadcast queued: {p.Message}" : $"Broadcast queued to [{p.RepoCsv}]: {p.Message}");
    }

    private void HandleMessages(string arg)
    {
        if (Ipc == null)
        {
            Log("IPC is disabled. Enable 'ipc' in huddle.json.");
            return;
        }

        if (string.IsNullOrEmpty(arg))
        {
            Log("Usage: messages <instance>");
            return;
        }

        // Resolve instance to get SafePathName
        if (!_manager.Instances.TryGetValue(arg, out var instance))
        {
            Log($"Unknown instance: {arg}");
            return;
        }

        var messages = Ipc.ReadInbox(instance.SafePathName);
        if (messages.Length == 0)
        {
            Log($"No messages for '{arg}'.");
            return;
        }

        Console.WriteLine();
        foreach (var msg in messages)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  [{msg.Timestamp}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{msg.From} ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"({msg.Type}) ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(msg.Subject);
                if (!string.IsNullOrEmpty(msg.BodyText) && msg.BodyText != msg.Subject)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"    {msg.BodyText}");
                }
            }
            finally { Console.ResetColor(); }
        }
        Console.WriteLine();
    }

    private void HandleHuddle(string arg)
    {
        var groups = _manager.Config.Groups;
        if (groups == null || groups.Count == 0)
        {
            Log("No groups defined in huddle.json.");
            return;
        }

        if (string.IsNullOrEmpty(arg))
        {
            // List available groups
            Log("Available groups:");
            foreach (var (name, members) in groups)
                Log($"  {name} ({members.Count} members)");
            return;
        }

        if (!groups.TryGetValue(arg, out var group))
        {
            Log($"Unknown group: {arg}");
            return;
        }

        var started = 0;
        foreach (var member in group)
        {
            try
            {
                if (_manager.Start(member.Repo, member.Persona, prompt: member.Prompt))
                    started++;
            }
            catch (Exception ex)
            {
                Log($"Failed to start {member.Repo}:{member.Persona}: {ex.Message}");
            }
        }
        Log($"Started {started}/{group.Count} sessions for group '{arg}'.");
    }

    private void HandleDelegate(string arg)
    {
        if (Orchestrator == null)
        {
            Log("Orchestrator not active. Enable 'ipc' in huddle.json.");
            return;
        }

        // Parse: delegate "description" to <instance>
        // Find quoted description
        var quoteStart = arg.IndexOf('"');
        if (quoteStart < 0)
        {
            Log("Usage: delegate \"description\" to <instance>");
            return;
        }
        var quoteEnd = arg.IndexOf('"', quoteStart + 1);
        if (quoteEnd < 0)
        {
            Log("Usage: delegate \"description\" to <instance>");
            return;
        }

        var description = arg[(quoteStart + 1)..quoteEnd];
        var remainder = arg[(quoteEnd + 1)..].Trim();

        // Expect "to <instance>"
        if (!remainder.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
        {
            Log("Usage: delegate \"description\" to <instance>");
            return;
        }

        var targetId = remainder[3..].Trim();
        if (string.IsNullOrEmpty(targetId))
        {
            Log("Usage: delegate \"description\" to <instance>");
            return;
        }

        // Null means the target's repo has no ledger that can be written. Refuse rather
        // than dispatch an obligation nothing is recording — the delegation would look
        // fine here and be invisible in `tasks` and `ledger` after the next restart.
        var task = Orchestrator.Tasks.Create(description, targetId, "_huddle");
        if (task is null)
        {
            Log($"Not delegated: no writable ledger for {targetId}'s repo, so the task could not be tracked.");
            return;
        }

        // Send IPC task message to target
        var targetSafe = targetId.Replace(':', '_');
        Ipc?.Send(Orchestrator.HuddleMailbox, targetSafe, $"task:{task.TaskId}", description, "task");

        // Start target if not running
        if (!_manager.Instances.ContainsKey(targetId))
        {
            var parts = targetId.Split(':', 2);
            _manager.Start(parts[0], parts.Length > 1 ? parts[1] : null, prompt: description);
        }

        Log($"Delegated {task.TaskId} to {targetId}: \"{description}\"");
    }

    private void HandleTasks()
    {
        if (Orchestrator == null)
        {
            Log("Orchestrator not active. Enable 'ipc' in huddle.json.");
            return;
        }

        var tasks = Orchestrator.Tasks.GetAll();
        if (tasks.Count == 0)
        {
            Log("No tracked tasks.");
            return;
        }

        Console.WriteLine();
        foreach (var task in tasks)
        {
            try
            {
                var color = task.State switch
                {
                    TaskState.Pending => ConsoleColor.Gray,
                    TaskState.Delegated => ConsoleColor.Yellow,
                    TaskState.InProgress => ConsoleColor.Cyan,
                    TaskState.Completed => ConsoleColor.Green,
                    TaskState.Failed => ConsoleColor.Red,
                    _ => ConsoleColor.Gray
                };

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  [{task.TaskId}] ");
                Console.ForegroundColor = color;
                Console.Write($"{task.State,-12} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{task.AssignedTo,-24} ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\"{task.Description}\"");
                if (!string.IsNullOrEmpty(task.Notes))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"             {task.Notes}");
                }
            }
            finally { Console.ResetColor(); }
        }
        Console.WriteLine();
    }

    private void HandleScan()
    {
        if (Orchestrator == null)
        {
            Log("Orchestrator not active. Enable 'ipc' in huddle.json.");
            return;
        }

        var count = Orchestrator.Scan();
        if (count == 0)
            Log("No commands in inbox.");
        else
            Log($"Processed {count} command(s) from inbox.");
    }

    private void HandleFocus(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            Log("Usage: focus <instance|repo:persona> (e.g. 'focus myapp:architect' or 'focus app:architect')");
            return;
        }

        // Direct match first, then try alias resolution (e.g. "app:architect" → "myapp:architect")
        if (!_manager.Instances.TryGetValue(arg, out var instance))
            instance = _manager.ResolveInstance(arg);

        if (instance == null)
        {
            Log($"Unknown instance: {arg}");
            return;
        }

        if (instance.Process == null || instance.Process.HasExited)
        {
            Log($"{instance.InstanceId} is not running.");
            return;
        }

        // The handle is captured at spawn (SessionWindow) rather than read from
        // Process.MainWindowHandle, which is always zero for a console process.
        // No live handle on record (recovered session, or spawn capture missed)?
        // Resolve it now by the session's tracked PID — a classic console window
        // reports the console app as its owner, so the lookup is direct.
        var hWnd = instance.WindowHandle;
        if (!SessionWindow.IsLive(hWnd) && _manager.TryCaptureWindowByPid(instance))
            hWnd = instance.WindowHandle;

        if (!SessionWindow.IsLive(hWnd))
        {
            Log($"{instance.InstanceId} has no console window huddle can identify " +
                "(likely hosted in a Windows Terminal tab). Try Alt+Tab.");
            return;
        }

        if (WindowFocus.BringToFront(hWnd))
            Log($"Focused {instance.InstanceId}.");
        else
            Log($"Windows denied foreground switch for {instance.InstanceId}. Try Alt+Tab.");
    }

    private void HandleProgress()
    {
        var found = false;
        foreach (var (_, instance) in _manager.Instances)
        {
            if (instance.Status != SessionStatus.Running)
                continue;

            var scratchpadPath = _manager.GetScratchpadPath(instance);
            if (!File.Exists(scratchpadPath))
                continue;

            var lines = File.ReadAllLines(scratchpadPath);
            var lastCheckpoint = lines
                .LastOrDefault(l => l.TrimStart().StartsWith("## Checkpoint"));

            if (lastCheckpoint == null)
                continue;

            found = true;
            try
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"  {instance.InstanceId,-24} ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(lastCheckpoint.Trim());

                // Show work ledger summary if available
                if (Ipc != null)
                {
                    var ledgerFile = Path.Combine(Ipc.WorkLedgerDir, $"{instance.SafePathName}.md");
                    if (File.Exists(ledgerFile))
                    {
                        var ledgerLines = File.ReadAllLines(ledgerFile);
                        var workingOn = ParseWorkingOn(ledgerLines);
                        var fileCount = ParseFilesSection(ledgerLines).Count;
                        if (workingOn != null)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            var suffix = fileCount > 0 ? $" ({fileCount} file{(fileCount == 1 ? "" : "s")} claimed)" : "";
                            Console.WriteLine($"  {"",24} 📋 {workingOn}{suffix}");
                        }
                    }
                }
            }
            finally { Console.ResetColor(); }
        }

        if (!found)
            Log("No checkpoint data found.");
    }

    // B016: report uncleaned entries from ipc/resledger/ whose pid is still
    // alive. Report-only — reclaim goes through scripts/sweep-orphans.ps1 -Kill
    // or the reclaimResourcesOnStop config opt-in.
    /// <summary>
    /// Show what each session still owes attention to: wake lines queued but not yet
    /// shown, and mail delivered but not yet acknowledged. Since mail stays in inbox/
    /// until the agent clears it, "unread" is a real count and not a delivery artefact.
    /// </summary>
    private void HandleBacklog()
    {
        if (Ipc == null)
        {
            Log("IPC is disabled. Enable 'ipc' in huddle.json.");
            return;
        }

        var rows = Ipc.GetBacklog();
        if (rows.Count == 0)
        {
            Log("No mail outstanding — every session is caught up.");
            return;
        }

        Console.WriteLine();
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{"session",-28} {"unread",6} {"queued",6}  oldest");
            Console.WriteLine(new string('-', 64));
            Console.ResetColor();

            foreach (var row in rows)
            {
                var running = _manager.Instances.Values.Any(i =>
                    i.SafePathName.Equals(row.Session, StringComparison.OrdinalIgnoreCase) && i.IsAlive);

                // Unread mail for a live session is the one that needs a human: it was
                // announced and the agent has not cleared it. A stopped session's
                // backlog drains by itself the moment it starts.
                Console.ForegroundColor = row.Unread > 0 && running ? ConsoleColor.Yellow : ConsoleColor.Gray;
                var age = row.Oldest.HasValue ? row.Oldest.Value.ToString("MM-dd HH:mm") : "";
                Console.Write($"{row.Session,-28} {row.Unread,6} {row.Queued,6}  {age}");

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(running ? "" : "  (stopped — drains on start)");
            }
        }
        finally { Console.ResetColor(); }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\nunread = delivered, not yet cleared by the agent   queued = wake lines not yet shown");
        Console.ResetColor();
        Console.WriteLine();
    }

    private void HandleJanitor()
    {
        if (Ipc == null)
        {
            Log("IPC is disabled. Enable 'ipc' in huddle.json.");
            return;
        }

        // Section 1: leaked resources (orphan processes).
        var ledger = new ResourceLedger(Ipc.ResLedgerDir, Log);
        var leaks = ledger.FindLeaks();
        if (leaks.Count > 0)
        {
            foreach (var (safe, entry) in leaks)
                Console.WriteLine(ResourceLedger.FormatLeak(safe, entry));
            Console.WriteLine($"janitor: {leaks.Count} leak(s). Reclaim: scripts/sweep-orphans.ps1 -Kill");
        }

        // Section 2: stale mail — unprocessed mail still sitting in inboxes.
        var staleShown = ReportStaleMail();

        if (leaks.Count == 0 && !staleShown)
            Console.WriteLine("janitor: no leaked resources, no stale mail.");
    }

    // Stale-mail section for janitor. Anything still in an ipc/<recipient>/inbox/ is
    // unprocessed (delivered mail is moved to processed/). Mail to a STOPPED recipient is
    // "old business" that can rot silently — surface it for review (tasks flagged). Mail to a
    // RUNNING recipient is just awaiting that agent's next turn — shown as a count, not rot.
    // Report-only; moves/archives nothing. Returns true if it printed anything.
    private bool ReportStaleMail()
    {
        var ipcRoot = Ipc!.IpcDir;
        if (!Directory.Exists(ipcRoot)) return false;

        var runningIds = new HashSet<string>(
            _manager.Instances.Values
                .Where(i => i.Status == SessionStatus.Running)
                .Select(i => i.SafePathName),
            StringComparer.OrdinalIgnoreCase);

        var deadMail = new SortedDictionary<string, List<(bool isTask, string age, string from, string subject)>>(
            StringComparer.OrdinalIgnoreCase);
        var liveWaiting = 0;
        var deadTaskCount = 0;

        foreach (var recipientDir in Directory.GetDirectories(ipcRoot))
        {
            var recipient = Path.GetFileName(recipientDir);
            var inbox = Path.Combine(recipientDir, "inbox");
            if (!Directory.Exists(inbox)) continue;

            var alive = runningIds.Contains(recipient);
            foreach (var file in Directory.GetFiles(inbox, "*.json"))
            {
                if (alive) { liveWaiting++; continue; }

                string from = "?", type = "?", subject = "";
                DateTime? ts = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("from", out var f)) from = f.GetString() ?? "?";
                    if (root.TryGetProperty("type", out var t)) type = t.GetString() ?? "?";
                    if (root.TryGetProperty("subject", out var s)) subject = s.GetString() ?? "";
                    if (root.TryGetProperty("timestamp", out var e)
                        && e.ValueKind == System.Text.Json.JsonValueKind.String
                        && DateTime.TryParse(e.GetString(), out var parsed)) ts = parsed;
                }
                catch { continue; }   // malformed / locked — skip, don't abort the listing

                ts ??= SafeMtime(file);
                var isTask = string.Equals(type, "task", StringComparison.OrdinalIgnoreCase);
                if (isTask) deadTaskCount++;

                if (!deadMail.TryGetValue(recipient, out var list))
                    deadMail[recipient] = list = new();
                list.Add((isTask, FormatAge(ts.Value), from, subject));
            }
        }

        var deadTotal = deadMail.Sum(kv => kv.Value.Count);
        if (deadTotal == 0 && liveWaiting == 0) return false;

        Console.WriteLine();
        if (deadTotal > 0)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                var taskNote = deadTaskCount > 0 ? $", {deadTaskCount} task{(deadTaskCount == 1 ? "" : "s")}" : "";
                Console.WriteLine($"  old business — {deadTotal} unprocessed for stopped recipients{taskNote} (review, don't lose):");
            }
            finally { Console.ResetColor(); }

            foreach (var (recipient, list) in deadMail)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  [stopped] {recipient.Replace('_', ':')}");
                    foreach (var (isTask, age, from, subject) in list.OrderByDescending(x => x.isTask))
                    {
                        Console.ForegroundColor = isTask ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
                        var mark = isTask ? "⚠ task" : "  info";
                        Console.WriteLine($"     {mark}  {age,4}  from {from.Replace('_', ':'),-26}  {Truncate(subject, 54)}");
                    }
                }
                finally { Console.ResetColor(); }
            }

            if (deadTaskCount > 0)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("  ↳ task(s) to a stopped recipient may be dropped work — 'messages <instance>' to read; re-dispatch by role or archive once resolved.");
                }
                finally { Console.ResetColor(); }
            }
        }

        if (liveWaiting > 0)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  +{liveWaiting} unprocessed for running agents (normal — awaiting their next turn).");
            }
            finally { Console.ResetColor(); }
        }

        return true;
    }

    private static DateTime SafeMtime(string file)
    {
        try { return File.GetLastWriteTime(file); } catch { return DateTime.Now; }
    }

    // One age ladder, in LedgerView.Age (the name the repo-stats work will call).
    // Mail keeps its own word for "under a minute": `backlog` has always shown "now"
    // there, and the ledger shows "0m".
    private static string FormatAge(DateTime ts)
    {
        var age = LedgerView.Age(DateTime.Now - ts);
        return age == "0m" ? "now" : age;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(no subject)" : (s.Length <= max ? s : s[..(max - 1)] + "…");

    /// <summary>
    /// How one claim spelled a file: `repo: path`, or just the path for a legacy claim that
    /// recorded no repo. The repo is half of the spelling — a path alone cannot show the
    /// operator why two claims on differently-named repos are the same file.
    /// </summary>
    private static string Spelling(string repo, string file)
        => string.IsNullOrWhiteSpace(repo) ? file : $"{repo}: {file}";

    /// <summary>
    /// Which checkout a claim is in, for the merge-risk report (I014): its recorded root plus
    /// the branch when it recorded one. Branch is display only — no decision reads it — but
    /// without it "two checkouts" is a fact the operator cannot act on.
    /// </summary>
    private static string Where(WorkLedgerClaim c)
    {
        var root = string.IsNullOrWhiteSpace(c.Root) ? c.Repo : c.Root;
        return string.IsNullOrWhiteSpace(c.Branch) ? root : $"{root} ({c.Branch})";
    }

    private void HandleConflicts()
    {
        if (Ipc == null)
        {
            Log("IPC is disabled. Enable 'ipc' in huddle.json.");
            return;
        }

        // Source A: existing freeform workledger files (prompt-written, one per session)
        var claimsFromLedger = new Dictionary<string, List<string>>(); // file -> sessions
        var staleSessions = new List<string>();
        var runningIds = new HashSet<string>(
            _manager.Instances.Values
                .Where(i => i.Status == SessionStatus.Running)
                .Select(i => i.SafePathName),
            StringComparer.OrdinalIgnoreCase);

        var ledgerDir = Ipc.WorkLedgerDir;
        if (Directory.Exists(ledgerDir))
        {
            foreach (var ledgerFile in Directory.GetFiles(ledgerDir, "*.md"))
            {
                var sessionName = Path.GetFileNameWithoutExtension(ledgerFile);
                var isStale = !runningIds.Contains(sessionName);
                if (isStale)
                {
                    staleSessions.Add(sessionName);
                    continue;
                }

                foreach (var file in ParseFilesSection(File.ReadAllLines(ledgerFile)))
                {
                    if (!claimsFromLedger.ContainsKey(file))
                        claimsFromLedger[file] = new List<string>();
                    claimsFromLedger[file].Add(sessionName);
                }
            }
        }

        // Source B: orchestrator-owned claims (new in Phase 1)
        var orchClaims = new List<WorkLedgerClaim>();
        // I013: the operator's view must decide collisions the way the ARBITER does, on
        // resolved absolute paths — nested repo roots give one physical file several
        // repo-relative spellings, and grouping on raw strings made this verb report an
        // all-clear on pairs the arbiter would refuse. The resolver is the orchestrator's
        // own (never a second one built here); with no orchestrator it stays null and the
        // comparison degrades to the pre-I013 name matching rather than failing.
        var resolveRoot = Orchestrator?.RepoRootResolver;
        var claimsDir = Ipc.ClaimsDir;
        if (Directory.Exists(claimsDir))
        {
            var reader = new WorkLedgerClaims(claimsDir, Log, resolveRoot);

            // On-demand orphan sweep: archive claims whose owning instance is no longer
            // live before reporting, so a stranded claim can't show up as a phantom holder.
            var live = _manager.Instances.Values
                .Where(i => i.IsAlive)
                .Select(i => new WorkLedgerClaims.LiveInstance(i.InstanceId, i.SessionId, i.StartedAt))
                .ToList();
            var reaped = reader.ReapOrphans(live);
            foreach (var c in reaped)
                Log($"Reaped orphan claim {c.SessionId} ({c.BatchId}) — archived, was holding {string.Join(", ", c.Files)}");

            orchClaims.AddRange(reader.ReadAll());
        }

        var conflicts = claimsFromLedger.Where(c => c.Value.Count > 1).ToList();
        // One definition of "these two claims collide" — ClaimConflictView delegates the
        // decision to WorkLedgerClaims.FindOverlaps and only explains the answer.
        List<ClaimCollision> orchOverlaps;
        try
        {
            orchOverlaps = ClaimConflictView.Find(orchClaims, resolveRoot);
        }
        catch (Exception ex)
        {
            // The verb never throws at the operator: a broken registry costs the explanation,
            // not the report.
            Log($"conflicts: claim comparison failed ({ex.GetType().Name}: {ex.Message}); listing claims only");
            orchOverlaps = new List<ClaimCollision>();
        }
        // I014's third outcome: not colliding, but the same path in a sibling worktree — a
        // merge conflict already booked in. Computed separately and failing separately, so a
        // git hiccup costs the warning and nothing else.
        List<ClaimMergeRisk> mergeRisks;
        try
        {
            mergeRisks = ClaimConflictView.FindMergeRisks(orchClaims, resolveRoot, GitWorktrees.Identify);
        }
        catch (Exception ex)
        {
            Log($"conflicts: merge-risk comparison failed ({ex.GetType().Name}: {ex.Message}); skipping that section");
            mergeRisks = new List<ClaimMergeRisk>();
        }
        var hasOutput = false;

        if (conflicts.Count > 0)
        {
            Console.WriteLine();
            foreach (var conflict in conflicts)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    var sessions = string.Join(" and ", conflict.Value.Select(s => s.Replace('_', ':')));
                    Console.WriteLine($"  ⚠ {sessions} both claim (freeform ledger):");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      {conflict.Key}");
                }
                finally { Console.ResetColor(); }
            }
            hasOutput = true;
        }

        if (orchOverlaps.Count > 0)
        {
            Console.WriteLine();
            foreach (var ov in orchOverlaps)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✖ OVERLAP in orchestrator claims: {ov.A.SessionId} and {ov.B.SessionId}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    foreach (var f in ov.Files)
                    {
                        // Name the physical file first, then how each holder spelled it —
                        // without that, two differently-spelled paths reported as one
                        // conflict read as a bug in huddle rather than as the point (I013).
                        Console.WriteLine($"      {f.ResolvedPath ?? f.SpellingB}");
                        Console.WriteLine($"        {ov.A.SessionId} (batch {ov.A.BatchId}) claims it as {Spelling(ov.A.Repo, f.SpellingA)}");
                        Console.WriteLine($"        {ov.B.SessionId} (batch {ov.B.BatchId}) claims it as {Spelling(ov.B.Repo, f.SpellingB)}");
                        if (f.CrossSpelling && f.ResolvedPath != null)
                            Console.WriteLine("        ↑ two spellings of one path — same file on disk");
                        else if (f.CrossSpelling)
                            Console.WriteLine("        ↑ two spellings, compared by repo name (root not resolved)");
                    }
                }
                finally { Console.ResetColor(); }
            }
            hasOutput = true;
        }

        if (mergeRisks.Count > 0)
        {
            Console.WriteLine();
            foreach (var risk in mergeRisks)
            {
                try
                {
                    // Yellow, not red, and the word "conflict" only ever attached to "merge":
                    // nobody is blocked here, and an operator who reads this as an overlap
                    // would stop work that does not need stopping.
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ⚠ MERGE RISK (not an overlap): {risk.A.SessionId} and {risk.B.SessionId}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    foreach (var f in risk.Files)
                        Console.WriteLine($"      {f}");
                    Console.WriteLine($"      {Where(risk.A)} vs {Where(risk.B)} — different files on disk, " +
                                      "same path on two branches");
                }
                finally { Console.ResetColor(); }
            }
            hasOutput = true;
        }

        // Also list active claims even when not overlapping — useful operator view.
        // Qualified by repo so two repos' same-named files are not shown as one line.
        if (orchClaims.Count > 0)
        {
            var lines = orchClaims
                .SelectMany(c => c.Files.Select(f => (Label: Spelling(c.Repo, f), Holder: $"{c.SessionId} (batch {c.BatchId})")))
                .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Holder, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine();
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Active orchestrator claims:");
                foreach (var (label, holder) in lines)
                    Console.WriteLine($"    {label}  ←  {holder}");
            }
            finally { Console.ResetColor(); }
            hasOutput = true;
        }

        if (staleSessions.Count > 0)
        {
            if (!hasOutput) Console.WriteLine();
            foreach (var stale in staleSessions)
            {
                try
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  {stale.Replace('_', ':')} — (stale ledger, session not running)");
                }
                finally { Console.ResetColor(); }
            }
            hasOutput = true;
        }

        if (!hasOutput)
            Log("No conflicts detected.");
        else
            Console.WriteLine();
    }

    // ---- `census` — the wiring gate (G5) -----------------------------------------
    // Bare: run huddle's own settings census (same rules as WiringCensusTests) plus a
    // cross-check that every exemption's ledger task is still OPEN — a deferral whose
    // owner closed without wiring the key is exactly how transcriptMaxScan rotted.
    // `census <repo>`: run that repo's configured censusCommand in its root.
    private void HandleCensus(string arg)
    {
        var target = arg.Trim();
        if (target.Length > 0)
        {
            var name = _manager.ResolveRepoName(target);
            if (name == null || !_manager.Repos.TryGetValue(name, out var def))
            { Log($"census: unknown repo '{target}'"); return; }
            if (string.IsNullOrWhiteSpace(def.CensusCommand))
            { Log($"census: repo '{name}' has no censusCommand in huddle.json — its census runs in its own test suite"); return; }
            var r = CaptureReplay.RunCommand(def.CensusCommand!, def.Root, Log);
            Log(r.Ran
                ? (r.Failed == 0 ? $"census {name}: CLEAN" : $"census {name}: {r.Failed} finding(s)")
                : $"census {name}: command did not run");
            return;
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(ConfigPath)) ?? ".";
        if (!File.Exists(Path.Combine(root, "src", "Settings.cs")))
        { Log($"census: huddle sources not found beside {ConfigPath} — run from the repo, or use census <repo>"); return; }

        var report = WiringCensus.RunLive(root);
        foreach (var o in report.Orphans) Log($"census: ORPHAN — setting '{o}' has no reader (wire it or exempt it with a ledger task)");
        foreach (var b in report.BadExemptions) Log($"census: BAD EXEMPTION — {b}");
        foreach (var s in report.StaleExemptions) Log($"census: STALE EXEMPTION — {s}");

        // Exemption -> ledger cross-check: the id must still be an OPEN item somewhere.
        var exemptionsPath = Path.Combine(root, "wiring-exemptions.txt");
        var pairs = File.Exists(exemptionsPath)
            ? WiringCensus.ExemptionLedgerIds(File.ReadAllLines(exemptionsPath))
            : Array.Empty<(string, string)>();
        if (pairs.Count > 0)
        {
            var snaps = _manager.Repos.Select(kv => LedgerView.Load(kv.Key, kv.Value.Root)).ToList();
            var openIds = new HashSet<string>(
                LedgerView.OpenByAge(snaps, DateTimeOffset.Now).Select(i => i.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var (key, id) in pairs)
                if (!openIds.Contains(id))
                    Log($"census: DEAD DEFERRAL — exemption '{key}' names ledger task {id}, which is not open: the owner shipped without wiring it");
        }

        var clean = report.Orphans.Count == 0 && report.BadExemptions.Count == 0 && report.StaleExemptions.Count == 0;
        Log(clean
            ? $"census huddle: CLEAN — {SettingsCatalog.All.Count} settings all wired or ledgered ({pairs.Count} exemption(s))"
            : "census huddle: findings above — the same check gates the build in WiringCensusTests");
    }

    private void HandleQueue()
    {
        if (Orchestrator == null) { Log("Orchestrator not active."); return; }
        var all = Orchestrator.Queue.All();
        if (all.Count == 0) { Log("queue: empty."); return; }
        foreach (var (u, state) in all.OrderBy(x => x.state))
        {
            var blocked = state == QueueState.Queued && u.DependsOn.Count > 0
                ? $"  (waits on {string.Join(", ", u.DependsOn)})" : "";
            Console.WriteLine($"  [{state,-7}] {u.Id,-20} {u.Repo}:{u.Persona}{blocked}");
        }
    }

    private void HandleDirect(string arg)
    {
        if (Ipc == null || Orchestrator == null)
        {
            Log("Orchestrator not active. Enable 'ipc' in huddle.json.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            Log("Usage: direct <english task>");
            return;
        }

        const string ArchitectId = "huddle:architect";
        if (!_manager.Instances.TryGetValue(ArchitectId, out var architect) || !architect.IsAlive)
        {
            Log($"architect not running — use 'start myapp architect' first");
            return;
        }

        // Compose body as {"task":"...","autoFire":true}
        var taskJson = System.Text.Json.JsonSerializer.Serialize(arg);
        var body = $"{{\"task\":{taskJson},\"autoFire\":true}}";

        Ipc.Send("_huddle_console", architect.SafePathName, "direct-task", body, "info");
        Log($"direct: task handed to {ArchitectId}");
    }

    private static List<string> ParseFilesSection(string[] lines)
    {
        var files = new List<string>();
        var inFilesSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("## Files"))
            {
                inFilesSection = true;
                continue;
            }
            if (inFilesSection)
            {
                if (trimmed.StartsWith("##"))
                    break;
                if (trimmed.StartsWith("- "))
                    files.Add(trimmed[2..].Trim());
            }
        }
        return files;
    }

    private static string? ParseWorkingOn(string[] lines)
    {
        var inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("## Working On"))
            {
                inSection = true;
                continue;
            }
            if (inSection)
            {
                if (trimmed.StartsWith("##"))
                    break;
                if (!string.IsNullOrWhiteSpace(trimmed))
                    return trimmed;
            }
        }
        return null;
    }

    // Durable orchestrator log. Console output is ephemeral — it scrolls away when
    // the window closes — so this append-only file is the record of what actually
    // happened: every command entered and every shutdown decision, with its reason.
    // An abnormal teardown must never again be unreconstructable.
    private static string? _logFilePath;
    private static readonly object _logFileLock = new();

    // Point the durable log at a file and stamp a session-open marker. Call once at startup.
    public static void SetLogFile(string path)
    {
        _logFilePath = path;
        AppendToLogFile($"===== huddle {BuildInfo.Short} log opened =====");
    }

    // Append one timestamped line to the durable log. Never throws — logging must
    // never be able to take huddle down.
    private static void AppendToLogFile(string message)
    {
        var path = _logFilePath;
        if (path is null) return;
        try
        {
            lock (_logFileLock)
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* durable log is best-effort; a write failure stays silent */ }
    }

    // Record the exact command line the operator entered — file only. The console
    // already echoed the keystrokes, so re-printing them would just be noise.
    public static void LogInput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        AppendToLogFile($"> {line}");
    }

    public static void Log(string message)
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
        }
        finally { Console.ResetColor(); }
        Console.WriteLine(message);
        AppendToLogFile(message);
    }

    public static void LogCrash(string message)
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
        }
        finally { Console.ResetColor(); }
        AppendToLogFile($"CRASH: {message}");
    }

    // Git network activity (pushes/fetches and credential-prompt requests). Cyan
    // by default; yellow with attention:true so a session blocked on a GitHub auth
    // pop-under — which the operator would otherwise never see — stands out.
    public static void LogGit(string message, bool attention = false)
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = attention ? ConsoleColor.Yellow : ConsoleColor.Cyan;
            Console.WriteLine(message);
        }
        finally { Console.ResetColor(); }
        AppendToLogFile(message);
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..].Replace('\\', '/');
        return path.Replace('\\', '/');
    }
}

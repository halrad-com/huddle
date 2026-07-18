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

    private IDocumentSource? _docSource;
    private readonly IDocumentOpener _docOpener = new ShellDocumentOpener();
    private List<DocumentEntry> _lastDocs = new();
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

                    // Surface whether a running session has a console window or is headless.
                    if (instance.Status == SessionStatus.Running)
                    {
                        var headless = IsHeadless(instance);
                        Console.ForegroundColor = headless ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
                        Console.Write(headless ? "  headless" : "  windowed");
                    }

                    Console.WriteLine();
                }
                finally { Console.ResetColor(); }
            }
        }
        Console.WriteLine();
    }

    // A running session is "headless" when its process has no top-level window handle
    // (no console window). MainWindowHandle is also briefly zero right after spawn, before
    // the console is created, so a just-started session can read headless for a moment.
    private static bool IsHeadless(SessionInstance instance)
    {
        var proc = instance.Process;
        if (proc == null) return false;
        try { proc.Refresh(); return proc.MainWindowHandle == IntPtr.Zero; }
        catch { return false; }
    }

    public void PrintHelp()
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Commands:");
            Console.WriteLine("  start <repo> [persona] [prompt]  Launch an instance with optional task");
            Console.WriteLine("  stop <instance|repo>     Stop a specific instance or all instances of a repo");
            Console.WriteLine("  restart <instance>       Restart a specific instance (keeps persona)");
            Console.WriteLine("  repos                    List registered repos and aliases");
            Console.WriteLine("  personas                 List available personas");
            Console.WriteLine("  status                   Show all instance statuses");
            Console.WriteLine("  send <instance> <msg>    Send a message to a session's inbox");
            Console.WriteLine("  say <instance> <text>    Inject a prompt directly into a session's console");
            Console.WriteLine("  shell [<repo>] <data>    Hand <data> to the OS shell (file handler); optional repo sets CWD");
            Console.WriteLine("  broadcast [@repo] <subj> <msg>  Fan out a message to live sessions (optionally only repo's agents)");
            Console.WriteLine("  messages <instance>      List messages in a session's inbox");
            Console.WriteLine("  huddle <group>           Start all sessions in a group");
            Console.WriteLine("  delegate \"desc\" to <inst>  Delegate a task to a session");
            Console.WriteLine("  tasks                    Show tracked tasks");
            Console.WriteLine("  scan                     Re-scan inbox for missed commands");
            Console.WriteLine("  focus <instance|repo>    Bring a session's console window to the foreground (alias: goto)");
            Console.WriteLine("  resume <instance>        Open 'claude --resume <session-id>' for a session in its repo root");
            Console.WriteLine("  progress                 Show last checkpoint per session");
            Console.WriteLine("  conflicts                Report file claim overlaps across sessions");
            Console.WriteLine("  janitor                  Report leaked session resources (resledger, B016)");
            Console.WriteLine("  queue                    Show the work queue — active / queued (blocked on) / done / failed");
            Console.WriteLine("  replay <repo> [host[:port]]  Run the repo's captured regression tests; optional cross-box DUT target");
            Console.WriteLine("  docs [plans|churn] [@repo] [kw] [-1d/-1w]  List docs; @repo, kw=folder/title, -Nd/-Nw=time window");
            Console.WriteLine("  open <n>                 Open the nth document from the last 'docs' listing");
            Console.WriteLine("  history [@repo] [kw] [-1d/-1w]  List past sessions from transcripts; 'history <n>' for detail, 'resume <n>' to reopen");
            Console.WriteLine("  direct <english task>    Hand a task to huddle:architect to plan + dispatch automatically");
            Console.WriteLine("  quit                     Exit huddle, sessions keep running");
            Console.WriteLine("  shutdown                 Stop all sessions and exit");
            Console.WriteLine("  reload [/y]              (advanced) Rebuild huddle + relaunch; child sessions keep running (/y skips prompt)");
            Console.WriteLine("  ver                      Show huddle version (branch, commit, build time)");
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

            case "queue":
                HandleQueue();
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

            case "focus" or "goto":
                HandleFocus(arg);
                break;

            case "quit" or "q" or "exit":
                return CommandResult.Quit;

            case "shutdown":
                return CommandResult.Shutdown;

            case "ver" or "version":
                Console.WriteLine(BuildInfo.Full);
                break;

            case "help" or "h" or "?":
                PrintHelp();
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
            PrintDocsPage();
            return;
        }

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

    private TranscriptStore CreateTranscriptStore()
    {
        var projectsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var roots = _manager.Repos.ToDictionary(kv => kv.Key, kv => kv.Value.Root, StringComparer.OrdinalIgnoreCase);
        return new TranscriptStore(projectsRoot, roots, Log);
    }

    private void HandleHistory(string arg)
    {
        var a = arg.Trim();

        if (a.Equals("more", StringComparison.OrdinalIgnoreCase))
        {
            if (_lastHistory.Count == 0) { Log("No listing to continue — run 'history' first."); return; }
            if (_historyPageOffset >= _lastHistory.Count) { Log("End of list — all sessions shown."); return; }
            PrintHistoryPage();
            return;
        }

        // `history <n>` — detail view for a row of the last listing.
        if (int.TryParse(a, out var idx))
        {
            PrintHistoryDetail(idx);
            return;
        }

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
        if (store.LastListTruncated) summary += $" (newest {TranscriptStore.MaxScan} transcripts scanned)";
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
                // Load into _lastDocs so the existing `open <n>` works unchanged.
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
            Console.WriteLine($"  → 'open <n>' to open a file · 'resume {n}' to reopen this conversation");
        }
        finally { Console.ResetColor(); }
        Console.WriteLine();
    }

    private void HandleHistoryResume(int n)
    {
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

    // OSC 8 hyperlink escape sequence: ESC ]8;;URI ST  text  ESC ]8;; ST
    // Build the file URI with new Uri(...).AbsoluteUri — it percent-encodes spaces,
    // normalizes backslashes, and adds the drive letter. Do NOT string-concat
    // "file:///" + path: that breaks for any artifact path with a space or backslash.
    private static string Hyperlink(string path, string text)
    {
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
    private bool HandleReload(string arg)
    {
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

    private void HandleBroadcast(string arg)
    {
        if (Orchestrator == null || Ipc == null)
        {
            Log("Orchestrator not active. Enable 'ipc' in huddle.json.");
            return;
        }

        var split = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string? repoCsv = null;
        if (split.Length > 0 && split[0].Length > 1 && split[0][0] == '@')
        {
            repoCsv = split[0][1..];
            arg = split.Length > 1 ? split[1] : "";
            split = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        }
        if (split.Length < 2)
        {
            Log("Usage: broadcast [@repo[,repo]] <subject> <message>");
            return;
        }
        var subject = split[0];
        var message = split[1];

        // Synthesize a broadcast command into the orchestrator's inbox so it
        // flows through the same code path as IPC-originated broadcasts.
        var subjJson = System.Text.Json.JsonSerializer.Serialize(subject);
        var msgJson = System.Text.Json.JsonSerializer.Serialize(message);
        var repoJson = repoCsv is null ? "" : $",\"repo\":{System.Text.Json.JsonSerializer.Serialize(repoCsv)}";
        var body = $"{{\"subject\":{subjJson},\"body\":{msgJson},\"type\":\"info\",\"targets\":\"all\"{repoJson}}}";
        Ipc.Send("_huddle_console", Orchestrator.HuddleMailbox, "broadcast", body, "command");
        Log(repoCsv is null ? $"Broadcast queued: {subject}" : $"Broadcast queued to [{repoCsv}]: {subject}");
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

        var task = Orchestrator.Tasks.Create(description, targetId, "_huddle");

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

        // Process.MainWindowHandle is populated once the spawned cmd.exe creates
        // its console window. Refresh() is needed in case we cached an old value.
        instance.Process.Refresh();
        var hWnd = instance.Process.MainWindowHandle;
        if (hWnd == IntPtr.Zero)
        {
            Log($"{instance.InstanceId} has no window handle (headless or not yet created).");
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

    private static string FormatAge(DateTime ts)
    {
        var span = DateTime.Now - ts;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return "now";
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(no subject)" : (s.Length <= max ? s : s[..(max - 1)] + "…");

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
        var claimsFromOrch = new Dictionary<string, List<(string session, string batch)>>();
        var claimsDir = Ipc.ClaimsDir;
        if (Directory.Exists(claimsDir))
        {
            var reader = new WorkLedgerClaims(claimsDir, Log);
            foreach (var claim in reader.ReadAll())
            {
                var sessionSafe = claim.SessionId.Replace(':', '_');
                foreach (var file in claim.Files)
                {
                    if (!claimsFromOrch.ContainsKey(file))
                        claimsFromOrch[file] = new List<(string, string)>();
                    claimsFromOrch[file].Add((sessionSafe, claim.BatchId));
                }
            }
        }

        var conflicts = claimsFromLedger.Where(c => c.Value.Count > 1).ToList();
        var orchOverlaps = claimsFromOrch.Where(c => c.Value.Count > 1).ToList();
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
                    Console.WriteLine($"  ✖ OVERLAP in orchestrator claims on: {ov.Key}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    foreach (var (session, batch) in ov.Value)
                        Console.WriteLine($"      - {session.Replace('_', ':')}  (batch {batch})");
                }
                finally { Console.ResetColor(); }
            }
            hasOutput = true;
        }

        // Also list active claims even when not overlapping — useful operator view
        if (claimsFromOrch.Count > 0)
        {
            Console.WriteLine();
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Active orchestrator claims:");
                foreach (var kv in claimsFromOrch.OrderBy(k => k.Key))
                {
                    var holder = kv.Value[0];
                    Console.WriteLine($"    {kv.Key}  ←  {holder.session.Replace('_', ':')} (batch {holder.batch})");
                }
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
            Log($"architect not running — use 'start seatbelt architect' first");
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

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..].Replace('\\', '/');
        return path.Replace('\\', '/');
    }
}

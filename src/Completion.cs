namespace Huddle;

public interface ICompleter
{
    // Given the full current input line, return ordered candidate completions
    // of the token being edited (best first), each as the WHOLE resulting line.
    // Empty = no suggestion.
    IReadOnlyList<string> Complete(string input);

    // Non-acceptable guidance for the current input — rendered dim like a ghost,
    // but Tab ignores it (Tab consults Complete, which is empty whenever a hint
    // shows). Default: no hints.
    string Hint(string input) => "";
}

public readonly record struct Verb(string Name, string Usage, string Group = "misc");

public static class Verbs
{
    // Single source of truth for console verb names. Convention: one entry per
    // primary verb in ConsoleUI.HandleCommand's switch. VerbCompleterTests pins
    // this list's *count*, so editing the catalog without deliberately bumping
    // that number fails a test — the pin guards the catalog, not the switch;
    // keeping the two in sync remains the convention it enforces a pause on.
    // Usage grammar mirrors ConsoleUI.PrintHelp and the real handlers.
    // Aliases (s, r, p, q, exit, ?, h, msg, unread, goto, rebuild, handoff,
    // version) are deliberately absent — they are accepted at the prompt but
    // are not completion targets.
    public static IReadOnlyList<Verb> Catalog { get; } = new Verb[]
    {
        new("status",    "status                   Show all sessions and their state", "sessions"),
        new("start",     "start <repo> [persona] [prompt]   Launch a session with an optional task", "sessions"),
        new("stop",      "stop <instance|repo>     Stop a session, or every session of a repo", "sessions"),
        new("restart",   "restart <instance>       Restart a session", "sessions"),
        new("resume",    "resume <instance>        Resume a stopped/crashed session", "sessions"),
        new("focus",     "focus <instance|repo>    Raise a session's window", "sessions"),
        new("peek",      "peek                     Thumbnail switcher over every running session", "sessions"),
        new("recover",   "recover [n|all|dismiss n]   List/resume recoverable sessions", "sessions"),
        new("backlog",   "backlog                  Per-session queued + unread mail", "sessions"),
        new("say",       "say <instance> <text>    Inject a prompt into a session's console", "comms"),
        new("send",      "send <instance> <msg>    Queue mail into a session's inbox", "comms"),
        // Matches the live grammar: ConsoleUI.ParseBroadcast takes the whole line
        // after the optional @repo CSV prefix as the message (the subject is
        // derived). Bare `broadcast <message>` reaches ALL live sessions.
        new("broadcast", "broadcast [@repo[,repo]] <message>   Fan out a message to live sessions (bare = all)", "comms"),
        new("direct",    "direct <task>            Auto-fire a task at the architect", "comms"),
        new("messages",  "messages <instance>      List a session's inbox", "comms"),
        new("delegate",  "delegate \"desc\" to <inst>   Delegate a task", "comms"),
        new("handoffs",  "handoffs [@repo] [n]     Recent agent-to-agent handoffs", "comms"),
        new("docs",      "docs [plans|churn] [@repo] [kw] [-Nw]   List doc artifacts", "insight"),
        new("open",      "open <n>                 Open a listed doc/result", "insight"),
        new("find",      "find <kw> [@repo] [-Nw]  Search docs, sessions, notes, mail", "insight"),
        new("history",   "history [@repo] [kw] [-Nw]   Browse past sessions", "insight"),
        new("stats",     "stats [<repo>] [--who] [--since 30d] [html]   Repo activity: movement, commits, who, time, work", "insight"),
        new("projects",  "projects [html [path]]   List projects; 'html' writes the status page", "insight"),
        new("project",   "project <slug>           Show a project's detail", "insight"),
        new("conflicts", "conflicts                Show claim conflicts", "work"),
        new("census",    "census [repo]            Wiring census: settings nothing reads", "work"),
        new("ledger",    "ledger [all|<id>|open [--by-age]|orphans|accept <id>|drop <id> <why>|decline <id> [note]] [--repo <name>] [--owner <instance>]   The feature ledger", "work"),
        new("queue",     "queue                    Show the work queue", "work"),
        new("tasks",     "tasks                    List tracked tasks", "work"),
        new("progress",  "progress                 Session progress + ledger summary", "work"),
        new("replay",    "replay <repo> [host[:port]]   Replay capture suites", "work"),
        new("settings",  "settings [key [value]]   Show or set huddle.json settings", "console"),
        new("reload",    "reload [/y]              Rebuild + relaunch huddle (/y skips the prompt)", "console"),
        new("ver",       "ver                      Show huddle version", "console"),
        new("help",      "help [all|<verb>]        Grouped help; 'all' = full usage, <verb> = one usage", "console"),
        new("quit",      "quit                     Exit huddle (sessions keep running)", "console"),
        new("shutdown",  "shutdown                 Stop all sessions and exit", "console"),
        new("shell",     "shell [<repo>] <data>    Hand data to the OS shell", "misc"),
        new("huddle",    "huddle <group>           Start all sessions in a group", "misc"),
        new("janitor",   "janitor                  Clean stale mail / resources", "misc"),
        new("scan",      "scan                     Scan orchestrator inbox now", "misc"),
        new("personas",  "personas                 List available personas", "misc"),
        new("repos",     "repos                    List registered repos", "misc"),
    };
}

/// <summary>
/// Help rendered FROM the verb catalog — one source, so help can never drift from
/// completion or the dispatch switch the way the hand-maintained PrintHelp list did.
/// Compact = one line per group (names only); Full = grouped usage lines;
/// Verb = one usage line. Spec 2026-08-31-shell-registration-design.md section 4.
/// </summary>
public static class HelpView
{
    // Frequency-ordered: daily groups first, scenario plumbing last.
    public static readonly string[] GroupOrder = { "sessions", "comms", "insight", "work", "console", "misc" };

    public static IReadOnlyList<string> RenderCompact(IReadOnlyList<Verb> catalog)
    {
        var lines = new List<string>();
        foreach (var g in GroupOrder)
        {
            var names = catalog.Where(v => v.Group == g).Select(v => v.Name);
            lines.Add($"{g,-10} {string.Join(" · ", names)}");
        }
        lines.Add("");
        lines.Add("help <verb> for usage · help all for the full list");
        return lines;
    }

    public static IReadOnlyList<string> RenderFull(IReadOnlyList<Verb> catalog)
    {
        var lines = new List<string>();
        foreach (var g in GroupOrder)
        {
            lines.Add($"[{g}]");
            foreach (var v in catalog.Where(v => v.Group == g))
                lines.Add("  " + v.Usage);
            lines.Add("");
        }
        return lines;
    }

    public static IReadOnlyList<string> RenderVerb(IReadOnlyList<Verb> catalog, string name)
    {
        var v = catalog.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        return v.Name != null
            ? new[] { "  " + v.Usage }
            : new[] { $"help: unknown verb '{name}' — 'help all' lists everything" };
    }
}

public sealed class VerbCompleter : ICompleter
{
    private readonly string[] _names;

    public VerbCompleter(IEnumerable<string>? names = null)
        => _names = (names ?? Verbs.Catalog.Select(v => v.Name)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<string> Complete(string input)
    {
        // Only complete the first token: once a space is present, an argument is
        // being typed — ArgCompleter handles that layer.
        if (input.Contains(' ')) return Array.Empty<string>();
        var token = input;
        return _names.Where(n => n.StartsWith(token, StringComparison.Ordinal)).ToArray();
    }
}

// Live-data sources for argument completion, injected as delegates so the
// completer is unit-testable without sessions. Each is consulted fresh per
// keystroke — no caching, matching huddle's read-live-state-per-verb style.
public sealed class ArgProviders
{
    public Func<IReadOnlyList<string>> LiveInstances { get; init; } = () => Array.Empty<string>();
    public Func<IReadOnlyList<string>> StoppedInstances { get; init; } = () => Array.Empty<string>();
    public Func<IReadOnlyList<string>> Repos { get; init; } = () => Array.Empty<string>();
    public Func<IReadOnlyList<string>> Personas { get; init; } = () => Array.Empty<string>();
}

// Argument-aware completer: first token completes verbs (via VerbCompleter),
// later tokens complete from live data where the argument is a knowable name
// (instances, repos, personas), and Hint() supplies the verb's argument grammar
// the moment the operator enters argument territory with nothing typed yet.
public sealed class ArgCompleter : ICompleter
{
    private readonly VerbCompleter _verbs;
    private readonly ArgProviders _p;

    // Verbs whose arguments may carry an @repo[,repo] scope token.
    private static readonly HashSet<string> AtRepoVerbs = new(StringComparer.Ordinal)
        { "broadcast", "docs", "history", "find", "handoffs" };

    public ArgCompleter(ArgProviders providers, IEnumerable<string>? verbNames = null)
    {
        _p = providers;
        _verbs = new VerbCompleter(verbNames);
    }

    public IReadOnlyList<string> Complete(string input)
    {
        // Completion is best-effort UI riding the keystroke path. Providers read
        // live state (session dictionary, process handles, persona dir) that other
        // threads mutate without a lock we can take — a torn enumeration or a
        // disposed Process must cost the operator a ghost, never the console.
        try { return CompleteCore(input); }
        catch { return Array.Empty<string>(); }
    }

    private IReadOnlyList<string> CompleteCore(string input)
    {
        if (!input.Contains(' ')) return _verbs.Complete(input);

        var firstSpace = input.IndexOf(' ');
        var verb = input[..firstSpace];
        var lastSpace = input.LastIndexOf(' ');
        var prefix = input[(lastSpace + 1)..];      // token being edited; "" right after a space
        var baseLine = input[..(lastSpace + 1)];

        // @repo scope token. Only broadcast's parser accepts a CSV list — the
        // others resolve the whole token as ONE repo name — so the comma-aware
        // segmentation applies to broadcast alone; elsewhere a comma just stops
        // matching (correct: the parser would silently match nothing).
        if (prefix.StartsWith('@') && AtRepoVerbs.Contains(verb))
        {
            var lastComma = verb == "broadcast" ? prefix.LastIndexOf(',') : -1;
            var head = prefix[..(lastComma < 0 ? 1 : lastComma + 1)]; // "@" or "@done,"
            var seg = prefix[head.Length..];
            return _p.Repos()
                .Where(r => r.StartsWith(seg, StringComparison.Ordinal))
                .OrderBy(r => r, StringComparer.Ordinal)
                .Select(r => baseLine + head + r)
                .ToArray();
        }

        // Positional providers. Position = index of the token being edited among
        // the arguments (0 = first arg). Tokens before it: everything in baseLine
        // after the verb.
        var argPos = baseLine[(firstSpace + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var pool = PoolFor(verb, argPos);
        return pool
            .Where(c => c.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => baseLine + c)
            .ToArray();
    }

    private IEnumerable<string> PoolFor(string verb, int argPos) => (verb, argPos) switch
    {
        ("say" or "send" or "messages", 0) => _p.LiveInstances(),
        // restart exists chiefly for crashed/stuck sessions — offer both worlds.
        ("restart", 0) => _p.LiveInstances().Concat(_p.StoppedInstances()),
        ("stop" or "focus", 0) => _p.LiveInstances().Concat(_p.Repos()),
        ("resume", 0) => _p.StoppedInstances(),
        ("start", 0) => _p.Repos(),
        ("start", 1) => _p.Personas(),
        ("replay" or "shell", 0) => _p.Repos(),
        ("stats", 0) => _p.Repos(),
        _ => Array.Empty<string>(),
    };

    // Grammar hint: exactly "verb" + trailing whitespace, nothing typed yet →
    // the argument portion of the verb's Usage line. Anything else: no hint
    // (completion, when available, is more actionable and wins in the renderer).
    public string Hint(string input)
    {
        var trimmed = input.TrimEnd(' ');
        if (trimmed.Length == 0 || trimmed.Contains(' ') || input.Length == trimmed.Length)
            return "";
        var verb = Verbs.Catalog.FirstOrDefault(v => v.Name == trimmed);
        if (verb.Name is null || verb.Name.Length == 0) return "";
        // Usage shape: "<name> <arg grammar>   <description>" — the description is
        // separated by a run of 2+ spaces; the grammar is single-spaced. A verb
        // with no arguments has the 2+ space gap IMMEDIATELY after its name, so
        // stripping all leading spaces first would misread its description as
        // grammar.
        var rest = verb.Usage[verb.Name.Length..];
        if (!rest.StartsWith(' ') || rest.StartsWith("  ", StringComparison.Ordinal)) return "";
        rest = rest[1..];
        var gap = rest.IndexOf("  ", StringComparison.Ordinal);
        return (gap < 0 ? rest : rest[..gap]).Trim();
    }
}

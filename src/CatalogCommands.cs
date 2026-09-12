namespace Huddle;

/// <summary>
/// The circulation desk: `--checkout`, `--checkin`, `--catalog`, `--status`.
///
/// These verbs exist to be usable by an agent nobody configured. The claim CLI refuses
/// without <c>HUDDLE_CLAIMS</c> and <c>HUDDLE_INSTANCE</c> in the environment
/// (LedgerCommands.TryContext), and only sessions huddle spawned itself have them — which is
/// precisely why an outsider could not take part. So nothing here requires environment:
///
///   * the ledger is FOUND, by walking up from the working directory for <c>ipc/workledger</c>
///     and then up from the huddle binary's own folder. The second walk is what reaches the one
///     shared ledger from a different repo entirely — an agent working in another checkout runs
///     the same huddle.exe, and that binary lives in the huddle install;
///   * each path is resolved to its repo by its FULL path, choosing the most specific registered
///     repo that contains it. That is the rule the edit gate uses, so a checkout and the gate name
///     a file the same way whichever folder the agent runs from;
///   * identity is whatever the caller says it is.
///
/// Environment variables, when present, are only defaults.
///
/// Exit codes: 0 granted, 1 refused because somebody holds it, 2 usage, 3 failure. The
/// distinct refusal code matters — a wrapper can branch on "busy" without parsing prose.
/// </summary>
public static class CatalogCommands
{
    private const int Ok = 0;
    private const int Busy = 1;
    private const int Usage = 2;
    private const int Failed = 3;

    public static int RunCheckout(string[] args, Func<string, string?> env, Action<string> outLine) =>
        RunCheckout(args, env, outLine, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    public static int RunCheckin(string[] args, Func<string, string?> env, Action<string> outLine) =>
        RunCheckin(args, env, outLine, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    public static int RunStatus(string[] args, Func<string, string?> env, Action<string> outLine) =>
        RunStatus(args, env, outLine, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    public static int RunCatalog(string[] args, Func<string, string?> env, Action<string> outLine) =>
        RunCatalog(args, env, outLine, Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    /// <summary>Testable form: the working directory and the binary's folder are passed in.</summary>
    public static int RunCheckout(string[] args, Func<string, string?> env, Action<string> outLine,
                                    string cwd, string exeDir)
    {
        var o = Options.Parse(args, env, outLine, cwd, exeDir);
        if (o == null) return Usage;
        if (o.Paths.Count == 0)
        {
            outLine("usage: huddle --checkout [--as <name>] [--repo <name>] [--lease <minutes>] <path> [more paths...]");
            return Usage;
        }
        if (!RequireBorrower(o, outLine)) return Usage;

        var cat = new FileCatalog(o.CatalogDir);
        if (!cat.TryCheckOutAll(o.Repo, o.Paths, o.Borrower, out var taken,
                                out var blockedBy, out var blockedPath, o.Lease, o.Root))
        {
            if (blockedBy == null)
            {
                outLine($"huddle --checkout: could not write the catalog at {o.CatalogDir}. Nothing checked out.");
                return Failed;
            }
            var mins = (int)Math.Ceiling((blockedBy.DueAt - DateTime.UtcNow).TotalMinutes);
            outLine($"CHECKED OUT to {blockedBy.Borrower}: {blockedPath}");
            outLine($"  due {blockedBy.DueAt:yyyy-MM-dd HH:mm}Z ({(mins > 0 ? $"{mins} min from now" : "overdue")})");
            outLine("  Nothing was checked out - a set is all or nothing. Work on other files, or wait for the");
            outLine("  due date to lapse and try again. Do NOT edit a file somebody holds.");
            return Busy;
        }

        outLine($"checked out {taken.Count} file(s) in {o.Repo} to {o.Borrower}, due {DateTime.UtcNow.Add(o.Lease):yyyy-MM-dd HH:mm}Z:");
        foreach (var t in taken) outLine($"  {t.RelPath}");
        outLine("Run `huddle --checkout` again on the same paths to renew before the due date, and");
        outLine("`huddle --checkin` when you have committed.");
        return Ok;
    }

    public static int RunCheckin(string[] args, Func<string, string?> env, Action<string> outLine,
                                   string cwd, string exeDir)
    {
        var o = Options.Parse(args, env, outLine, cwd, exeDir);
        if (o == null) return Usage;
        if (!RequireBorrower(o, outLine)) return Usage;

        var cat = new FileCatalog(o.CatalogDir);

        // `--checkin --all` returns everything this borrower holds — in every repo, unless --repo
        // narrows it. A borrower's books are its books: an agent that worked across two repos, or
        // ran from a folder above the repo it edited, must not have to know which repo each file
        // was filed under in order to give it back.
        var targets = o.All
            ? cat.ReadAll()
                 .Where(e => SameBorrower(e.Borrower, o.Borrower) && (!o.RepoWasGiven || SameRepo(e.Repo, o.Repo)))
                 .Select(e => (Repo: e.Repo, Path: e.RelPath, Root: e.Root))
                 .ToList()
            : o.Paths.Select(p => (Repo: o.Repo, Path: p, Root: o.Root)).ToList();

        if (targets.Count == 0)
        {
            if (o.All) { outLine($"{o.Borrower} holds nothing{(o.RepoWasGiven ? $" in {o.Repo}" : "")}."); return Ok; }
            outLine("usage: huddle --checkin [--as <name>] [--repo <name>] (<path> [more paths...] | --all)");
            return Usage;
        }

        int done = 0;
        foreach (var t in targets)
        {
            // Read the entry BEFORE returning it, so the hash recorded at checkout is still
            // there to compare against. Katalog's influence: saying what changed is more use
            // than saying the book came back.
            var mine = cat.Status(t.Repo, t.Path);
            if (cat.CheckIn(t.Repo, t.Path, o.Borrower))
            {
                done++;
                outLine($"  {t.Repo}/{t.Path}{Changed(mine, t.Root)}");
                continue;
            }
            if (mine == null) outLine($"  {t.Repo}/{t.Path} - already available, nothing to return");
            else outLine($"  {t.Repo}/{t.Path} - held by {mine.Borrower}, not you; left alone");
        }
        outLine($"checked in {done} file(s) as {o.Borrower}");
        return Ok;
    }

    /// <summary>
    /// One file: available, or checked out to whom and until when. The single-file query the
    /// filemgr spec had and this did not.
    /// </summary>
    public static int RunStatus(string[] args, Func<string, string?> env, Action<string> outLine,
                                  string cwd, string exeDir)
    {
        var o = Options.Parse(args, env, outLine, cwd, exeDir);
        if (o == null) return Usage;
        if (o.Paths.Count != 1)
        {
            outLine("usage: huddle --status [--repo <name>] <path>");
            return Usage;
        }

        var path = o.Paths[0];
        var held = new FileCatalog(o.CatalogDir).Status(o.Repo, path);
        if (held == null)
        {
            outLine($"{o.Repo}/{FileCatalog.Normalize(path)} - AVAILABLE");
            return Ok;
        }

        var now = DateTime.UtcNow;
        outLine($"{held.Repo}/{held.RelPath} - CHECKED OUT to {held.Borrower}");
        outLine($"  taken {held.CheckedOutAt:yyyy-MM-dd HH:mm}Z, due {held.DueAt:yyyy-MM-dd HH:mm}Z" +
                (held.IsOverdue(now) ? " (OVERDUE - reclaimable)" : ""));
        var changed = Changed(held, o.Root);
        if (changed.Length > 0) outLine($" {changed.TrimStart()}");
        return Ok;
    }

    /// <summary>
    /// " - changed" / " - unchanged since checkout" when both hashes are known, otherwise
    /// nothing. Silence is deliberate: an unknown hash must not be reported as "unchanged".
    /// </summary>
    private static string Changed(CatalogEntry? e, string root)
    {
        if (e == null || string.IsNullOrEmpty(e.Hash) || string.IsNullOrWhiteSpace(root)) return "";
        var now = FileCatalog.HashOf(root, e.RelPath);
        if (now.Length == 0) return " - gone from the working tree since checkout";
        return now == e.Hash ? " - unchanged since checkout" : " - changed since checkout";
    }

    /// <summary>
    /// Print the circulation list: what is out, to whom, until when. Everything not listed is
    /// available. <c>--renew</c> extends every book this borrower holds — the heartbeat that
    /// keeps a long task's checkouts from lapsing under it.
    ///
    /// The filters are the ones a circulation store can answer truthfully. There is
    /// deliberately no `--status available`: that is an INVENTORY question (what files exist),
    /// and this store only knows what is taken. Answering it would mean keeping a row per
    /// available file, which is the drift the presence-as-state design exists to avoid — see
    /// docs/superpowers/specs/2026-09-12-circulation-design.md.
    /// </summary>
    public static int RunCatalog(string[] args, Func<string, string?> env, Action<string> outLine,
                                   string cwd, string exeDir)
    {
        var o = Options.Parse(args, env, outLine, cwd, exeDir);
        if (o == null) return Usage;

        var cat = new FileCatalog(o.CatalogDir);

        // `--mine` names a borrower, so it needs one; a plain listing does not.
        if (o.Mine && !RequireBorrower(o, outLine)) return Usage;

        if (o.Renew)
        {
            if (!RequireBorrower(o, outLine)) return Usage;
            var n = cat.Renew(o.Borrower, o.Lease);
            outLine($"renewed {n} file(s) for {o.Borrower}, due {DateTime.UtcNow.Add(o.Lease):yyyy-MM-dd HH:mm}Z");
            return Ok;
        }

        var now = DateTime.UtcNow;
        // One renderer for the command line and the console (CatalogView), so the two surfaces
        // cannot drift in what they show, how they filter, or how they order it.
        var all = CatalogView.Filter(cat.ReadAll(), now,
                                     repo: o.RepoWasGiven ? o.Repo : null,
                                     overdueOnly: o.Overdue,
                                     borrower: o.Mine ? o.Borrower : null);
        if (all.Count == 0)
        {
            var what = o.Overdue ? "No checkout is overdue" : o.Mine ? $"{o.Borrower} holds nothing" : "Nothing is checked out";
            outLine($"{what}{(o.RepoWasGiven ? $" in {o.Repo}" : "")}.");
            outLine($"catalog: {o.CatalogDir}");
            return Ok;
        }

        foreach (var e in all) outLine(CatalogView.Line(e, now));
        foreach (var line in CatalogView.SummaryLines(CatalogView.Summarize(all, now))) outLine(line);
        return Ok;
    }

    /// <summary>
    /// A checkout or a check-in needs a borrower; reading the list does not. Kept separate from
    /// parsing so that `--catalog` stays anonymous — an agent that cannot yet say who it is can
    /// still see which files are taken.
    /// </summary>
    private static bool RequireBorrower(Options o, Action<string> outLine)
    {
        if (!string.IsNullOrWhiteSpace(o.Borrower)) return true;
        outLine("huddle: no borrower. Say who you are with `--as <name>` (any stable name, e.g.");
        outLine("`--as codex:refactor`). A checkout with no borrower tells the next agent nothing.");
        return false;
    }

    private static bool SameRepo(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool SameBorrower(string a, string b) =>
        a.Replace(':', '_').Equals(b.Replace(':', '_'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parsed invocation. Everything has a discovered or inferred default so that the verbs
    /// work with an empty environment; the only thing that can genuinely stop us is not being
    /// able to find an <c>ipc/workledger</c> to write into.
    /// </summary>
    private sealed class Options
    {
        public string CatalogDir = "";
        public string Repo = "";
        public bool RepoWasGiven;
        public string Borrower = "";
        public string Root = "";
        public TimeSpan Lease = FileCatalog.DefaultLease;
        public bool All;
        public bool Renew;
        public bool Overdue;
        public bool Mine;
        public List<string> Paths = new();

        public static Options? Parse(string[] args, Func<string, string?> env, Action<string> outLine,
                                     string cwd, string exeDir)
        {
            var o = new Options();
            string? repo = null, borrower = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--as" when i + 1 < args.Length: borrower = args[++i].Trim(); break;
                    case "--repo" when i + 1 < args.Length: repo = args[++i].Trim(); break;
                    case "--lease" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], out var m) || m <= 0)
                        {
                            outLine("huddle: --lease takes a positive number of minutes.");
                            return null;
                        }
                        o.Lease = TimeSpan.FromMinutes(m);
                        break;
                    case "--all": o.All = true; break;
                    case "--renew": o.Renew = true; break;
                    case "--overdue": o.Overdue = true; break;
                    case "--mine": o.Mine = true; break;
                    default:
                        if (args[i].StartsWith("--"))
                        {
                            outLine($"huddle: unknown option '{args[i]}'.");
                            return null;
                        }
                        o.Paths.Add(args[i]);
                        break;
                }
            }

            // An absolute path is not portable between agents, and '..' could climb out of the
            // tree a path names, so both are refused.
            foreach (var p in o.Paths)
            {
                if (Path.IsPathRooted(p) || p.Contains(".."))
                {
                    outLine($"huddle: '{p}' must be a path relative to where you are running (no drive, no leading slash, no '..').");
                    return null;
                }
            }

            var found = FindLedgerRoot(env, cwd, exeDir);
            if (found == null)
            {
                outLine("huddle: could not find the shared ledger (an 'ipc/workledger' directory) above this folder");
                outLine("or above the huddle binary, and HUDDLE_CLAIMS is not set. Run the huddle.exe from the huddle");
                outLine("install, or point HUDDLE_CLAIMS at <huddle-root>/ipc/workledger/claims.");
                return null;
            }
            o.CatalogDir = Path.Combine(found.Value.LedgerDir, "catalog");
            var claimsDir = Path.Combine(found.Value.LedgerDir, "claims");

            var envRepo = env("HUDDLE_REPO");
            o.RepoWasGiven = !string.IsNullOrWhiteSpace(repo) || !string.IsNullOrWhiteSpace(envRepo);

            if (!string.IsNullOrWhiteSpace(repo))
            {
                // `--repo netlib`: the paths are relative to netlib' root, and the root recorded
                // on each entry must be netlib' too. Not cosmetic: the content hash is read relative
                // to Root, so a wrong root silently yields no hash and points every reader at the
                // wrong checkout. Falls back to where we are when the name is unknown or unreadable.
                o.Repo = repo!;
                o.Root = LedgerCommands.RootForRepoName(claimsDir, repo!) ?? cwd;
            }
            else if (!string.IsNullOrWhiteSpace(envRepo))
            {
                // A session huddle spawned: it knows its repo and its checkout, and runs from there.
                o.Repo = envRepo!;
                var envRoot = env("HUDDLE_REPO_ROOT");
                o.Root = !string.IsNullOrWhiteSpace(envRoot) ? envRoot!
                       : LedgerCommands.RootForRepoName(claimsDir, envRepo!) ?? cwd;
            }
            else if (!ResolveByPath(o, cwd, claimsDir, found.Value.RepoRoot, outLine))
            {
                return null;
            }

            // Deliberately NOT required here: reading the circulation list is anonymous, so an
            // agent can always see what is out before it knows anything about itself. Only the
            // verbs that take or return a book demand a name (see RequireBorrower).
            o.Borrower = borrower ?? env("HUDDLE_INSTANCE") ?? "";

            return o;
        }

        /// <summary>
        /// No repo named: resolve each path to its repo by FULL path, the way the edit gate does,
        /// and re-express it relative to that repo's root. All paths must land in one repo — a
        /// checkout is all-or-nothing within a repo, so a set spanning two is refused rather than
        /// half-granted. With no paths (a listing, `--checkin --all`), the folder being run from
        /// names the repo, which only ever labels messages.
        ///
        /// Keying by the folder instead is what made an outer checkout invisible: from the outer
        /// repo's root, a file inside a registered nested project would have been filed under the
        /// OUTER repo, while the gate files it under the nested one, and the two never meet.
        /// </summary>
        private static bool ResolveByPath(Options o, string cwd, string claimsDir, string fallbackRoot,
                                          Action<string> outLine)
        {
            string FallbackName() =>
                LedgerCommands.RepoNameForRoot(claimsDir, fallbackRoot) ?? new DirectoryInfo(fallbackRoot).Name;

            if (o.Paths.Count == 0)
            {
                var here = LedgerCommands.RepoForPath(claimsDir, cwd);
                o.Repo = here?.Name ?? FallbackName();
                o.Root = here?.Root ?? fallbackRoot;
                return true;
            }

            var resolved = new List<(string Name, string Root, string Rel)>();
            foreach (var p in o.Paths)
            {
                var full = Path.GetFullPath(Path.Combine(cwd, p));
                var hit = LedgerCommands.RepoForPath(claimsDir, full);
                if (hit != null)
                {
                    resolved.Add((hit.Value.Name, hit.Value.Root,
                                  Path.GetRelativePath(hit.Value.Root, full).Replace('\\', '/')));
                    continue;
                }

                // No readable config, or a file in no registered repo: file it against the tree the
                // ledger was found in, which is the best that can be done without a registry.
                var rel = Path.GetRelativePath(fallbackRoot, full).Replace('\\', '/');
                resolved.Add((FallbackName(), fallbackRoot, rel.StartsWith("..") ? FileCatalog.Normalize(p) : rel));
            }

            var repos = resolved.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (repos.Count > 1)
            {
                outLine($"huddle: these paths are in different repos ({string.Join(", ", repos)}). A checkout is all");
                outLine("or nothing within one repo, so give each repo's files a command of their own.");
                return false;
            }

            o.Repo = resolved[0].Name;
            o.Root = resolved[0].Root;
            o.Paths = resolved.Select(r => r.Rel).ToList();
            return true;
        }
    }

    /// <summary>
    /// Find the ledger without being told where it is: HUDDLE_CLAIMS when the environment has it;
    /// otherwise walk up from the working directory, then up from the huddle binary's own folder.
    /// The first walk serves an agent inside the huddle tree. The second serves an agent in any
    /// other repo, which still runs the one huddle.exe that lives beside the ledger.
    /// </summary>
    private static (string LedgerDir, string RepoRoot)? FindLedgerRoot(Func<string, string?> env, string cwd, string exeDir)
    {
        var claims = env("HUDDLE_CLAIMS");
        if (!string.IsNullOrWhiteSpace(claims))
        {
            var ledger = Path.GetDirectoryName(claims.TrimEnd('/', '\\'));
            if (!string.IsNullOrWhiteSpace(ledger))
            {
                var repoRoot = env("HUDDLE_REPO_ROOT");
                return (ledger, string.IsNullOrWhiteSpace(repoRoot) ? cwd : repoRoot!);
            }
        }

        return WalkUpForLedger(cwd) ?? WalkUpForLedger(exeDir);
    }

    private static (string LedgerDir, string RepoRoot)? WalkUpForLedger(string start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        try
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "ipc", "workledger");
                if (Directory.Exists(candidate)) return (candidate, d.FullName);
            }
        }
        catch { /* unreadable parent: treat as not found */ }
        return null;
    }
}

namespace Huddle;

/// <summary>
/// The circulation desk: `--checkout`, `--checkin`, `--catalog`.
///
/// These verbs exist to be usable by an agent nobody configured. The claim CLI refuses
/// without <c>HUDDLE_CLAIMS</c> and <c>HUDDLE_INSTANCE</c> in the environment
/// (LedgerCommands.TryContext), and only sessions huddle spawned itself have them — which is
/// precisely why an outsider could not take part. So nothing here requires environment:
/// the ledger is FOUND by walking up from the working directory for <c>ipc/workledger</c>,
/// the repo is inferred from the checkout it finds, and identity is whatever the caller says
/// it is. Environment variables, when present, are only defaults.
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

    public static int RunCheckout(string[] args, Func<string, string?> env, Action<string> outLine)
    {
        var o = Options.Parse(args, env, outLine);
        if (o == null) return Usage;
        if (o.Paths.Count == 0)
        {
            outLine("usage: huddle --checkout [--as <name>] [--repo <name>] [--lease <minutes>] <repo-relative-path> [more paths...]");
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

    public static int RunCheckin(string[] args, Func<string, string?> env, Action<string> outLine)
    {
        var o = Options.Parse(args, env, outLine);
        if (o == null) return Usage;
        if (!RequireBorrower(o, outLine)) return Usage;

        var cat = new FileCatalog(o.CatalogDir);

        // `--checkin --all` returns everything this borrower holds. A borrower that is about
        // to exit should not have to remember what it took.
        var paths = o.All
            ? cat.ReadAll().Where(e => SameRepo(e.Repo, o.Repo) && SameBorrower(e.Borrower, o.Borrower))
                 .Select(e => e.RelPath).ToList()
            : o.Paths;

        if (paths.Count == 0)
        {
            if (o.All) { outLine($"{o.Borrower} holds nothing in {o.Repo}."); return Ok; }
            outLine("usage: huddle --checkin [--as <name>] [--repo <name>] (<repo-relative-path> [more paths...] | --all)");
            return Usage;
        }

        int done = 0;
        foreach (var p in paths)
        {
            // Read the entry BEFORE returning it, so the hash recorded at checkout is still
            // there to compare against. Katalog's influence: saying what changed is more use
            // than saying the book came back.
            var mine = cat.Status(o.Repo, p);
            if (cat.CheckIn(o.Repo, p, o.Borrower))
            {
                done++;
                outLine($"  {p}{Changed(mine, o.Root)}");
                continue;
            }
            if (mine == null) outLine($"  {p} - already available, nothing to return");
            else outLine($"  {p} - held by {mine.Borrower}, not you; left alone");
        }
        outLine($"checked in {done} file(s) in {o.Repo} as {o.Borrower}");
        return Ok;
    }

    /// <summary>
    /// One file: available, or checked out to whom and until when. The single-file query the
    /// filemgr spec had and this did not.
    /// </summary>
    public static int RunStatus(string[] args, Func<string, string?> env, Action<string> outLine)
    {
        var o = Options.Parse(args, env, outLine);
        if (o == null) return Usage;
        if (o.Paths.Count != 1)
        {
            outLine("usage: huddle --status [--repo <name>] <repo-relative-path>");
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
    public static int RunCatalog(string[] args, Func<string, string?> env, Action<string> outLine)
    {
        var o = Options.Parse(args, env, outLine);
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

        public static Options? Parse(string[] args, Func<string, string?> env, Action<string> outLine)
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

            // An absolute path would record a checkout nobody else's paths can match.
            foreach (var p in o.Paths)
            {
                if (Path.IsPathRooted(p) || p.Contains(".."))
                {
                    outLine($"huddle: '{p}' must be a repo-relative path (no drive, no leading slash, no '..').");
                    return null;
                }
            }

            var found = FindLedgerRoot(env, Directory.GetCurrentDirectory());
            if (found == null)
            {
                outLine("huddle: could not find an 'ipc/workledger' directory in this checkout or any parent,");
                outLine("and HUDDLE_CLAIMS is not set. Run this from inside the shared working directory, or");
                outLine("point HUDDLE_CLAIMS at <huddle-root>/ipc/workledger/claims.");
                return null;
            }
            o.CatalogDir = Path.Combine(found.Value.LedgerDir, "catalog");
            var claimsDir = Path.Combine(found.Value.LedgerDir, "claims");

            // `--repo netlib` means the paths live in netlib' tree, so the root recorded on
            // the entry must be netlib' root and not the directory we happen to be standing
            // in. Getting this wrong is not cosmetic: the content hash is read relative to Root,
            // so a wrong root silently produces no hash and every reader is pointed at the wrong
            // checkout. Falls back to where we are when the name is unknown or unreadable.
            o.Root = !string.IsNullOrWhiteSpace(repo)
                ? LedgerCommands.RootForRepoName(claimsDir, repo!) ?? found.Value.RepoRoot
                : found.Value.RepoRoot;

            var envRepo = env("HUDDLE_REPO");
            o.RepoWasGiven = !string.IsNullOrWhiteSpace(repo) || !string.IsNullOrWhiteSpace(envRepo);
            o.Repo = !string.IsNullOrWhiteSpace(repo) ? repo!
                   : !string.IsNullOrWhiteSpace(envRepo) ? envRepo!
                   // The REGISTERED name for this checkout, not its directory name: this repo
                   // lives in `myapp` and is registered as `huddle`, so keying on the folder
                   // would let one physical file be held twice under two spellings (I013).
                   // Only when no config can be read does the directory name stand in.
                   : LedgerCommands.RepoNameForRoot(claimsDir, found.Value.RepoRoot)
                     ?? new DirectoryInfo(found.Value.RepoRoot).Name;

            // Deliberately NOT required here: reading the circulation list is anonymous, so an
            // agent can always see what is out before it knows anything about itself. Only the
            // verbs that take or return a book demand a name (see RequireBorrower).
            o.Borrower = borrower ?? env("HUDDLE_INSTANCE") ?? "";

            return o;
        }
    }

    /// <summary>
    /// Find the ledger without being told where it is: HUDDLE_CLAIMS when the environment has
    /// it, otherwise walk up from the working directory looking for <c>ipc/workledger</c>.
    /// The walk is what lets an agent that was never configured take part — it only has to be
    /// running somewhere inside the shared tree.
    /// </summary>
    private static (string LedgerDir, string RepoRoot)? FindLedgerRoot(Func<string, string?> env, string cwd)
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

        try
        {
            for (var d = new DirectoryInfo(cwd); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "ipc", "workledger");
                if (Directory.Exists(candidate)) return (candidate, d.FullName);
            }
        }
        catch { /* unreadable parent: fall through to the not-found message */ }

        return null;
    }
}

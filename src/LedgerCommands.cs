namespace Huddle;

/// <summary>
/// Argument-mode entry points for direct ledger access: `huddle --claim`,
/// `--release`, `--ledger`. These run the BINARY; they never contact a running
/// huddle, which is what makes a claim survive the orchestrator being down.
/// Identity and ledger location arrive as environment variables set at spawn, so
/// an agent types only the file paths.
/// </summary>
public static class LedgerCommands
{
    private const int Ok = 0;
    private const int Usage = 2;
    // Distinct from Usage: "you typed it wrong" and "it broke" call for different
    // reactions, and a caller that cannot tell them apart will retry the wrong one.
    private const int Failed = 3;

    /// <summary>
    /// Claim ids are "A-" (agent) plus a UTC stamp plus a uniquifier, because the
    /// claim FILENAME is derived from the id and two claims by one session in the
    /// same second would otherwise overwrite each other.
    /// </summary>
    public static string NewBatchId(DateTime utcNow, string uniquifier)
        => $"A-{utcNow:yyyyMMdd-HHmmss}-{uniquifier}";

    // Every entry point is wrapped: an unhandled exception here is a stack trace on
    // stderr, no claim on disk, and an agent with no protocol for what to do next and
    // — by design — no arbiter to fall back on. That is a silent refusal, which is the
    // exact failure the ledger exists to remove. Reachable from a bad HUDDLE_CLAIMS, a
    // full disk, or a transient AV lock on a claim file.
    /// <summary>
    /// Repo name (or alias) → absolute root, built by reading huddle.json off disk — the
    /// CLI half of I013. Without it a claim made here is compared on repo NAMES, so a
    /// session holding the SAME physical file under a nested registration (`LIB-root` and
    /// `myapp` are the same tree) is invisible to the claimant: a false all-clear,
    /// the one outcome the ledger exists to prevent.
    ///
    /// It reads the config rather than taking a spawn-time variable because detecting a
    /// collision needs the OTHER claim's root too, not just the caller's — one exported
    /// root would only ever resolve half of each pair, and <see cref="WorkLedgerClaims"/>
    /// deliberately falls back to names unless BOTH resolve. Reading a file needs no
    /// running huddle, so the "works when the orchestrator is down" property is intact,
    /// and it works for the hand-typed `HUDDLE_CLAIMS=... huddle.exe --claim` form too.
    ///
    /// Location is DERIVED, not configured: IpcManager builds the claims dir as
    /// &lt;configDir&gt;/ipc/workledger/claims, so the config sits three levels up.
    ///
    /// Returns null — never throws, never logs — when the config is missing, unreadable,
    /// or malformed. Absence is normal (the CLI exists for when huddle never started) and
    /// a config problem must never cost an agent their claim; null simply keeps the
    /// pre-I013 name comparison, which over-reports rather than under-protects.
    /// </summary>
    public static Func<string, string?>? BuildRepoResolver(string claimsDir)
    {
        try
        {
            var configPath = FindConfig(claimsDir);
            if (configPath == null) return null;

            var roots = RepoRoots(HuddleConfig.Load(configPath));
            if (roots.Count == 0) return null;
            return name => name != null && roots.TryGetValue(name.Trim(), out var root) ? root : null;
        }
        catch
        {
            // Unparseable JSON, a locked file, a path the OS rejects — all the same answer.
            return null;
        }
    }

    /// <summary>
    /// huddle.json (or the legacy myapp.json Program.cs still accepts) beside the ipc
    /// directory that holds this claims dir, or null if there is nothing there to read.
    /// A HUDDLE_CLAIMS pointed somewhere else entirely simply finds no config and degrades.
    /// </summary>
    private static string? FindConfig(string claimsDir)
    {
        var dir = Path.GetDirectoryName(                       // <configDir>
            Path.GetDirectoryName(                             // ipc
                Path.GetDirectoryName(                         // workledger
                    Path.GetFullPath(claimsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
        if (string.IsNullOrEmpty(dir)) return null;

        var huddle = Path.Combine(dir, "huddle.json");
        if (File.Exists(huddle)) return huddle;
        var myapp = Path.Combine(dir, "myapp.json");
        return File.Exists(myapp) ? myapp : null;
    }

    /// <summary>
    /// Flatten the config's sessions into the same lookup <c>SessionManager.Register</c> +
    /// <c>ResolveRepoName</c> produce: repo names win over aliases, first registration of a
    /// name wins, and matching is case-insensitive. Divergence here would be its own defect —
    /// the CLI and the orchestrator would disagree about what a repo name means.
    /// </summary>
    private static Dictionary<string, string> RepoRoots(HuddleConfig config)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in config.Sessions)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            if (!roots.ContainsKey(s.Name)) roots[s.Name] = s.Root;
        }
        // Aliases second so a real repo name always shadows an alias of the same spelling,
        // which is what ResolveRepoName does (it checks _repos before _aliases).
        foreach (var s in config.Sessions)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || s.Aliases == null) continue;
            foreach (var alias in s.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || roots.ContainsKey(alias)) continue;
                roots[alias] = s.Root;
            }
        }
        return roots;
    }

    /// <summary>
    /// The checkout this claim's paths are relative to (ISSUES.md I014), most trustworthy
    /// first: <c>HUDDLE_REPO_ROOT</c>, exported at spawn from the session's own
    /// <c>SessionInstance.Root</c>; else the current directory, but ONLY when it is itself a
    /// checkout top.
    ///
    /// The fallback deliberately does NOT walk up to find a `.git`. A registered repo root
    /// can be a SUBDIR of its git top (`LIB/myapp` inside the LIB repo), and claim paths
    /// are relative to the registered root — so rebasing them onto the git top would produce
    /// a confidently WRONG absolute path, which is worse than no root at all. An unstamped
    /// claim degrades to the repo registry, which is exactly the pre-I014 behaviour.
    ///
    /// Returns "" when nothing can be established. Never throws.
    /// </summary>
    public static string ResolveClaimRoot(Func<string, string?> env, Func<string> currentDir)
    {
        try
        {
            var exported = env("HUDDLE_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(exported)) return Path.GetFullPath(exported.Trim());

            var cwd = currentDir();
            if (string.IsNullOrWhiteSpace(cwd)) return "";
            var full = Path.GetFullPath(cwd);
            // A worktree's `.git` is a FILE pointing at the real gitdir, not a directory —
            // both forms count as "this directory is a checkout top".
            var dotGit = Path.Combine(full, ".git");
            return Directory.Exists(dotGit) || File.Exists(dotGit) ? full : "";
        }
        catch
        {
            return "";
        }
    }

    public static int RunClaim(string[] rest, Func<string, string?> env, Action<string> outLine)
    {
        try { return ClaimCore(rest, env, outLine); }
        catch (Exception ex) { return Fail(outLine, "claim", ex); }
    }

    public static int RunRelease(string[] rest, Func<string, string?> env, Action<string> outLine)
    {
        try { return ReleaseCore(rest, env, outLine); }
        catch (Exception ex) { return Fail(outLine, "release", ex); }
    }

    public static int RunLedger(string[] rest, Func<string, string?> env, Action<string> outLine)
    {
        try { return LedgerCore(rest, env, outLine); }
        catch (Exception ex) { return Fail(outLine, "ledger", ex); }
    }

    /// <summary>
    /// One clear, actionable line (plus what to do about it) and a distinct exit code.
    /// Never swallow: the caller has to know the claim did NOT land.
    /// </summary>
    private static int Fail(Action<string> outLine, string verb, Exception ex)
    {
        outLine($"huddle --{verb} FAILED: {ex.GetType().Name}: {ex.Message}");
        // ASCII hyphens, not em dashes: these modes run in a bare cmd whose output
        // encoding is the OEM codepage, where a non-ASCII dash degrades to '?'.
        outLine("Nothing was recorded. Check HUDDLE_CLAIMS names a writable directory and retry. " +
                "If it fails again, mail the operator and do NOT start editing - with no arbiter " +
                "in this path, an unrecorded claim is invisible to everyone else.");
        return Failed;
    }

    private static int ClaimCore(string[] rest, Func<string, string?> env, Action<string> outLine)
    {
        if (rest.Length == 0)
        {
            outLine("usage: huddle --claim <repo-relative-path> [more paths...]");
            return Usage;
        }
        if (!AllRelative(rest, "claim", outLine)) return Usage;
        if (!TryContext(env, outLine, out var claimsDir, out var instance, out var repo, out var guid))
            return Usage;
        if (string.IsNullOrEmpty(repo))
        {
            // Warn, don't fail: a repo-less claim collides with EVERY repo (see
            // WorkLedgerClaims.ReposCollide), which is the fail-safe direction — it
            // over-reports rather than under-protects. But it must be said, because the
            // success line below would otherwise read "claimed 1 file(s) in  as ..." with
            // a blank where the repo goes, and the claim will shout at unrelated repos
            // until it is released.
            outLine("WARNING: HUDDLE_REPO is not set - this claim is recorded with no repo, so it " +
                    "will be reported as overlapping the same path in EVERY repo.");
        }

        // The ledger's own log goes to the agent's stdout rather than being swallowed.
        // Two things it reports matter to a claimant and are otherwise invisible: a claim
        // file that failed to parse (so it is NOT being counted as a conflict), and a
        // mutex timeout (so the overlap report may be incomplete). Silence on either
        // would recreate the class of failure this whole feature exists to remove.
        // The resolver is what lets an overlap be decided on the physical file rather than
        // the repo NAME it was spelled with (I013). Null when no config can be read, which
        // is the pre-I013 behaviour and never a reason to refuse a claim.
        // The checkout this session is actually in, recorded on the claim so no reader has to
        // infer it from the repo NAME — the name is wrong for every worktree session (I014).
        var claimRoot = ResolveClaimRoot(env, Directory.GetCurrentDirectory);
        // Informational only: it makes "same file, other branch" legible in the report. A
        // failure here is silence, never an error — nothing about a claim depends on it.
        var branch = string.IsNullOrEmpty(claimRoot) ? null : GitHelper.CurrentBranch(claimRoot);

        var claims = new WorkLedgerClaims(
            claimsDir, outLine, BuildRepoResolver(claimsDir), GitWorktrees.Identify);
        // ONE timestamp: the filename stamp and the recorded ClaimedAt must agree, and two
        // reads of UtcNow can straddle a second boundary. The uniquifier is 8 hex digits,
        // not 4 — the claim FILENAME derives from the batch id and File.WriteAllText
        // overwrites, so a collision would destroy an earlier claim's file list while this
        // prints success, and same-second repeat claims are what the protocol encourages.
        var now = DateTime.UtcNow;
        var claim = new WorkLedgerClaim(
            instance, repo,
            NewBatchId(now, Guid.NewGuid().ToString("N")[..8]),
            now, "", rest, guid, Project: "", Root: claimRoot, Branch: branch ?? "");

        var result = LedgerCli.Claim(claims, claim);
        outLine($"claimed {rest.Length} file(s) in {repo} as {instance}");

        foreach (var overlap in result.Overlaps)
        {
            // The claim time is shown because the holder may be a session that has since
            // died: with no live roster there is no way to tell from the ledger alone, and
            // an agent told to "go mail them" needs to judge for itself whether a holder
            // from days ago is worth waiting on. UTC to match every other rendering of a
            // claim time (LedgerCli.Describe, Orchestrator.HandleClaim) — a claim parsed
            // from disk is UTC, but one held in memory need not be.
            outLine($"ALSO HELD BY {overlap.B.SessionId} since {overlap.B.ClaimedAt.ToUniversalTime():yyyy-MM-dd HH:mm}Z: " +
                    $"{string.Join(", ", overlap.SharedFiles)}");
            outLine($"  -> mail {overlap.B.SessionId} and agree who goes first. Do not edit until you have.");
        }

        foreach (var warning in result.MergeWarnings)
        {
            // NOT a conflict and deliberately worded so it cannot be mistaken for one: the two
            // files are in different checkouts, so neither session can overwrite the other and
            // neither has to stop. What they will hit is a merge, and until I014 the ledger
            // reported that as silence. ASCII only - these modes run in a bare cmd on the OEM
            // codepage, where a non-ASCII dash degrades to '?'.
            var where = string.IsNullOrEmpty(warning.B.Branch)
                ? "another checkout"
                : $"branch {warning.B.Branch}";
            outLine($"MERGE RISK: {warning.B.SessionId} holds the same path in {where}: " +
                    $"{string.Join(", ", warning.SharedFiles)}");
            outLine("  -> not a conflict, nobody is blocked - different files on disk. " +
                    "Expect a merge conflict later; tell them what you are changing.");
        }
        return Ok;
    }

    private static int ReleaseCore(string[] rest, Func<string, string?> env, Action<string> outLine)
    {
        if (rest.Length == 0)
        {
            outLine("usage: huddle --release <repo-relative-path> [more paths...]");
            return Usage;
        }
        if (!AllRelative(rest, "release", outLine)) return Usage;
        if (!TryContext(env, outLine, out var claimsDir, out var instance, out _, out _))
            return Usage;

        var released = LedgerCli.Release(new WorkLedgerClaims(claimsDir, outLine), instance, rest);
        outLine($"released {released} file(s)");
        return Ok;
    }

    private static int LedgerCore(string[] rest, Func<string, string?> env, Action<string> outLine)
    {
        var claimsDir = env("HUDDLE_CLAIMS");
        if (string.IsNullOrEmpty(claimsDir))
        {
            outLine("HUDDLE_CLAIMS is not set - cannot locate the ledger.");
            return Usage;
        }
        var repoFilter = rest.Length > 0 ? rest[0] : null;
        var text = LedgerCli.Describe(new WorkLedgerClaims(claimsDir, outLine).ReadAll(), repoFilter);
        foreach (var line in text.TrimEnd().Split('\n'))
            outLine(line.TrimEnd());
        return Ok;
    }

    /// <summary>
    /// Claims are matched on repo-relative paths, so an absolute one records a claim that
    /// can never collide with another session's `src/a.cs` — it prints success and protects
    /// nobody. Agents are actively primed to type absolute paths (the standing house rule
    /// is to name files in full), so this is a usage error worth being loud about rather
    /// than something to silently rewrite: huddle cannot know which repo root to strip.
    /// </summary>
    private static bool AllRelative(string[] paths, string verb, Action<string> outLine)
    {
        foreach (var p in paths)
        {
            if (!Path.IsPathRooted(p.Trim())) continue;
            outLine($"usage: huddle --{verb} takes REPO-RELATIVE paths (e.g. src/Foo.cs); '{p}' is absolute.");
            outLine("An absolute path never matches another session's claim on the same file, so the " +
                    "claim would be invisible to everyone. Drop the repo root and try again.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Resolve identity and ledger location. Missing context is a hard failure: writing
    /// a claim to the wrong place is worse than not writing one, because it looks like
    /// success while staying invisible to everyone else.
    /// </summary>
    private static bool TryContext(
        Func<string, string?> env, Action<string> outLine,
        out string claimsDir, out string instance, out string repo, out string guid)
    {
        claimsDir = env("HUDDLE_CLAIMS") ?? "";
        instance = env("HUDDLE_INSTANCE") ?? "";
        repo = env("HUDDLE_REPO") ?? "";
        guid = env("HUDDLE_GUID") ?? "";

        if (string.IsNullOrEmpty(claimsDir))
        {
            outLine("HUDDLE_CLAIMS is not set - cannot locate the ledger.");
            return false;
        }
        if (string.IsNullOrEmpty(instance))
        {
            outLine("HUDDLE_INSTANCE is not set - a claim with no owner is useless.");
            return false;
        }
        return true;
    }
}

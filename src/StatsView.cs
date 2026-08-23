using System.Text;

namespace Huddle;

/// <summary>
/// Renders <see cref="RepoStatsSnapshot"/> for the console and (Task 7) for a
/// self-contained HTML page.
///
/// One rule governs every line here: an inferred attribution is NEVER rendered without
/// the word "inferred" beside it. The whole point of the two grades is that the operator
/// can tell a fact from a candidate at a glance; a layout that drops the grade column
/// turns roster overlap into a false accusation.
/// </summary>
public static class StatsView
{
    /// <summary>"30d" / "12h" / bare "7" (days). False for anything else, so the verb
    /// can complain rather than silently using a default window the operator didn't ask for.</summary>
    public static bool TryParseSince(string token, DateTimeOffset now, out DateTimeOffset since)
    {
        since = default;
        token = token.Trim().ToLowerInvariant();
        if (token.Length == 0) return false;
        var unit = token[^1];
        var num = char.IsDigit(unit) ? token : token[..^1];
        if (!int.TryParse(num, out var n) || n <= 0) return false;
        since = unit == 'h' ? now.AddHours(-n) : now.AddDays(-n);
        return true;
    }

    public static string RenderAll(IReadOnlyList<RepoStatsSnapshot> snaps, DateTimeOffset since, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"REPO ACTIVITY — since {since:yyyy-MM-dd HH:mm} ({LedgerView.Age(now - since)})");
        foreach (var s in snaps) { sb.AppendLine(); sb.Append(RenderRepo(s, now)); }
        return sb.ToString();
    }

    public static string RenderRepo(RepoStatsSnapshot s, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{s.Repo,-12} {s.Root}");
        if (!s.IsGit)
            sb.AppendLine("  remotes    not a git repo");
        else
        {
            sb.AppendLine("  remotes    " + (s.Remotes.Count == 0 ? "none" : string.Join("     ", s.Remotes.Select(kv => $"{kv.Value} ({kv.Key})"))));
            var pushes = s.Movements.Count(m => m.Verb == "push"); var fetches = s.Movements.Count(m => m.Verb is "fetch" or "pull");
            var lastPush = s.Movements.Where(m => m.Verb == "push").OrderByDescending(m => m.Ts).FirstOrDefault();
            sb.AppendLine($"  movement   {pushes} pushes, {fetches} fetches" +
                (lastPush != null ? $"      last push {lastPush.Ts:HH:mm} → {lastPush.Identity ?? lastPush.Remote} ({lastPush.Sha})" : ""));
            if (s.Commits != null)
                sb.AppendLine($"  commits    {s.Commits.Commits} local, {s.Commits.Unpushed} unpushed     +{s.Commits.Added} / -{s.Commits.Deleted} lines" +
                    (s.Commits.Last.HasValue ? $"   last {s.Commits.Last.Value.ToLocalTime():MM-dd HH:mm}" : ""));
            sb.AppendLine($"  churn      {s.Dirty} dirty files");
        }
        // Label sits on the first row and later rows hang under it, so a repo with
        // several candidates reads as one block rather than a stray empty header.
        if (s.Who.Count == 0) sb.AppendLine("  who        nobody attributable");
        else
            for (int i = 0; i < s.Who.Count; i++)
            {
                var a = s.Who[i];
                sb.AppendLine((i == 0 ? "  who        " : "             ")
                    + $"{a.Instance,-26}{(a.Grade == AttributionGrade.Exact ? "exact" : "inferred"),-9}({string.Join(", ", a.Evidence.Take(3))})");
            }
        sb.AppendLine($"  time       {s.Sessions} sessions · {s.SessionHours} session-hours" + (s.IdleGap.HasValue ? $" · idle gap {LedgerView.Age(s.IdleGap.Value)}" : ""));
        sb.AppendLine($"  work       units {s.Units} · mail {s.Mail} · handoffs {s.Handoffs} · open claims {s.OpenClaims}");
        if (s.Health.Count == 0) sb.AppendLine("  health     ok");
        else
            for (int i = 0; i < s.Health.Count; i++)
                sb.AppendLine((i == 0 ? "  health     " : "             ") + s.Health[i]);
        return sb.ToString();
    }

    /// <summary>The same facts pivoted by session instead of by repo — "who has been
    /// doing what", which is the half of the operator's question the per-repo view buries.</summary>
    public static string RenderWho(IReadOnlyList<RepoStatsSnapshot> snaps)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  {"session",-28}{"repo",-14}{"grade",-10}evidence");
        foreach (var s in snaps)
            foreach (var a in s.Who.OrderBy(a => a.Grade))
                sb.AppendLine($"  {a.Instance,-28}{s.Repo,-14}{(a.Grade == AttributionGrade.Exact ? "exact" : "inferred"),-10}{string.Join(", ", a.Evidence.Take(3))}");
        return sb.ToString();
    }

    /// <summary>
    /// A contribution graph computed from the LOCAL clone. Azure DevOps has no equivalent
    /// of GitHub's graph and two of the operator's roots are not hosted at all, so this is
    /// the only view that is uniform across the fleet — and it counts commits that were
    /// never pushed, which is where nearly all agent activity lives.
    ///
    /// Inline SVG, no script and no CDN: the page has to open offline.
    /// </summary>
    public static string HeatmapSvg(IReadOnlyList<DateTimeOffset> commitTimes, DateTimeOffset now, int weeks = 52)
    {
        var counts = new Dictionary<DateOnly, int>();
        foreach (var t in commitTimes) { var d = DateOnly.FromDateTime(t.ToLocalTime().Date); counts[d] = counts.GetValueOrDefault(d) + 1; }
        var end = DateOnly.FromDateTime(now.ToLocalTime().Date);
        var start = end.AddDays(-(weeks * 7 - 1));
        // align start to Sunday
        while (start.DayOfWeek != DayOfWeek.Sunday) start = start.AddDays(-1);
        int cell = 11, gap = 2, step = cell + gap;
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"heat\" width=\"{weeks * step + 20}\" height=\"{7 * step + 20}\" viewBox=\"0 0 {weeks * step + 20} {7 * step + 20}\" role=\"img\" aria-label=\"commits per day\">");
        for (int w = 0; w < weeks; w++)
            for (int d = 0; d < 7; d++)
            {
                var day = start.AddDays(w * 7 + d);
                if (day > end) continue;
                var n = counts.GetValueOrDefault(day);
                var c = n == 0 ? 0 : n == 1 ? 1 : n <= 3 ? 2 : n <= 6 ? 3 : 4;
                sb.Append($"<rect x=\"{10 + w * step}\" y=\"{10 + d * step}\" width=\"{cell}\" height=\"{cell}\" class=\"c{c}\" data-count=\"{n}\"><title>{day:yyyy-MM-dd}: {n}</title></rect>");
            }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string RenderHtml(IReadOnlyList<RepoStatsSnapshot> snaps, DateTimeOffset since, DateTimeOffset now, string generatedBy)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>huddle — repo stats</title>
            <style>
            body{font:14px/1.4 system-ui,Segoe UI,sans-serif;margin:24px;color:#222;background:#fff}
            h1{font-size:20px;margin:0 0 4px} .sub{color:#666;margin-bottom:20px}
            section{border:1px solid #ddd;border-radius:6px;padding:12px 16px;margin-bottom:16px}
            h2{font-size:16px;margin:0 0 8px} h2 small{font-weight:400;color:#666}
            dl{display:grid;grid-template-columns:110px 1fr;gap:2px 12px;margin:0}
            dt{color:#666} dd{margin:0} .inferred{color:#a60;font-style:italic} .exact{color:#063}
            /* The heatmap can be wider than a narrow viewport; scroll it, never the page. */
            .heatwrap{overflow-x:auto;margin-top:12px}
            .heat rect.c0{fill:#ebedf0}.heat rect.c1{fill:#9be9a8}.heat rect.c2{fill:#40c463}.heat rect.c3{fill:#30a14e}.heat rect.c4{fill:#216e39}
            @media (prefers-color-scheme:dark){
              body{background:#111;color:#ddd}section{border-color:#333}.sub,dt,h2 small{color:#999}
              .inferred{color:#e0a458}.exact{color:#6cc08b}
              .heat rect.c0{fill:#222}.heat rect.c1{fill:#0e4429}.heat rect.c2{fill:#006d32}.heat rect.c3{fill:#26a641}.heat rect.c4{fill:#39d353}
            }
            </style></head><body>
            """);
        sb.Append($"<h1>Repo stats</h1><div class=\"sub\">since {ProjectReport.Esc(since.ToString("yyyy-MM-dd HH:mm"))} — generated {ProjectReport.Esc(now.ToString("yyyy-MM-dd HH:mm"))} by {ProjectReport.Esc(generatedBy)} — reproducible via <code>stats html</code></div>\n");
        foreach (var s in snaps)
        {
            sb.Append($"<section><h2>{ProjectReport.Esc(s.Repo)} <small>{ProjectReport.Esc(s.Root)}</small></h2><dl>");
            sb.Append($"<dt>remotes</dt><dd>{(s.IsGit ? string.Join(" · ", s.Remotes.Select(kv => ProjectReport.Esc($"{kv.Value} ({kv.Key})"))) : "not a git repo")}</dd>");
            sb.Append($"<dt>movement</dt><dd>{s.Movements.Count(m => m.Verb == "push")} pushes, {s.Movements.Count(m => m.Verb is "fetch" or "pull")} fetches</dd>");
            if (s.Commits != null) sb.Append($"<dt>commits</dt><dd>{s.Commits.Commits} local, {s.Commits.Unpushed} unpushed, +{s.Commits.Added}/−{s.Commits.Deleted}</dd>");
            sb.Append($"<dt>churn</dt><dd>{s.Dirty} dirty files</dd>");
            sb.Append("<dt>who</dt><dd>" + (s.Who.Count == 0 ? "nobody attributable" :
                string.Join("<br>", s.Who.Select(a => $"<span class=\"{(a.Grade == AttributionGrade.Exact ? "exact" : "inferred")}\">{ProjectReport.Esc(a.Instance)} — {(a.Grade == AttributionGrade.Exact ? "exact" : "inferred")}</span> <small>{ProjectReport.Esc(string.Join(", ", a.Evidence.Take(3)))}</small>"))) + "</dd>");
            sb.Append($"<dt>time</dt><dd>{s.Sessions} sessions · {s.SessionHours} session-hours</dd>");
            sb.Append($"<dt>work</dt><dd>units {s.Units} · mail {s.Mail} · handoffs {s.Handoffs} · open claims {s.OpenClaims}</dd>");
            sb.Append("<dt>health</dt><dd>" + (s.Health.Count == 0 ? "ok" : string.Join("<br>", s.Health.Select(ProjectReport.Esc))) + "</dd></dl>");
            if (s.Commits != null) sb.Append($"<div class=\"heatwrap\">{HeatmapSvg(s.Commits.CommitTimes, now)}</div>");
            sb.Append("</section>\n");
        }
        sb.Append("</body></html>\n");
        return sb.ToString();
    }
}

using System.Text;

namespace Huddle;

/// <summary>
/// One agent that works/worked on a project: the "usual suspects" row.
/// State is "live", "recoverable", or "past" (from the state archive).
/// </summary>
public sealed record ProjectAgent(
    string InstanceId, string? Persona, string? LastFocus, string State, DateTime? LastSeen);

/// <summary>Everything the report shows for one project.</summary>
public sealed record ProjectReportEntry(
    ProjectInfo Project,
    IReadOnlyList<ProjectAgent> Agents,
    IReadOnlyList<WorkLedgerClaim> Claims);

/// <summary>
/// Renders the projects lens to a self-contained HTML page (inline CSS, system
/// fonts, file:// artifact links — offline by construction, no external anything).
/// Pure string builder over already-gathered data, so the page is reproducible:
/// same inputs, same page. This is the output-demo north star for the projects
/// feature: `projects html` regenerates it from live data on every run.
/// </summary>
public static class ProjectReport
{
    public static string Render(IReadOnlyList<ProjectReportEntry> entries, string generatedBy)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>huddle — projects</title>
            <style>
              :root {
                --bg: #f6f7f9; --card: #ffffff; --ink: #1c2330; --muted: #66707f;
                --line: #e3e7ee; --accent: #2563eb; --ok: #16803c; --warn: #b45309;
                --pill-active: #dcfce7; --pill-active-ink: #166534;
                --pill-paused: #fef3c7; --pill-paused-ink: #92400e;
                --pill-done: #e2e8f0; --pill-done-ink: #334155;
              }
              @media (prefers-color-scheme: dark) {
                :root {
                  --bg: #12151b; --card: #1a1f27; --ink: #e5e9f0; --muted: #8b95a5;
                  --line: #2a313c; --accent: #60a5fa; --ok: #4ade80; --warn: #fbbf24;
                  --pill-active: #14532d; --pill-active-ink: #bbf7d0;
                  --pill-paused: #451a03; --pill-paused-ink: #fde68a;
                  --pill-done: #1e293b; --pill-done-ink: #cbd5e1;
                }
              }
              * { box-sizing: border-box; }
              body { margin: 0; background: var(--bg); color: var(--ink);
                     font: 15px/1.55 "Segoe UI", system-ui, sans-serif; }
              .wrap { max-width: 980px; margin: 0 auto; padding: 32px 20px 64px; }
              header h1 { font-size: 26px; margin: 0 0 2px; letter-spacing: -0.02em; }
              header .sub { color: var(--muted); font-size: 13px; margin-bottom: 28px; }
              .card { background: var(--card); border: 1px solid var(--line);
                      border-radius: 10px; padding: 20px 24px; margin-bottom: 18px; }
              .head { display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap; }
              .slug { font-size: 19px; font-weight: 650; }
              .title { color: var(--muted); }
              .pill { font-size: 11.5px; font-weight: 600; padding: 2px 10px;
                      border-radius: 999px; text-transform: uppercase; letter-spacing: .04em; }
              .pill.active { background: var(--pill-active); color: var(--pill-active-ink); }
              .pill.paused { background: var(--pill-paused); color: var(--pill-paused-ink); }
              .pill.other  { background: var(--pill-done);   color: var(--pill-done-ink); }
              .sprint { font-size: 12.5px; color: var(--accent); font-weight: 600; }
              .goal { margin: 8px 0 2px; }
              .meta { color: var(--muted); font-size: 13px; margin: 2px 0 10px; }
              .warn { color: var(--warn); font-size: 13px; margin: 6px 0; }
              h3 { font-size: 12px; text-transform: uppercase; letter-spacing: .07em;
                   color: var(--muted); margin: 16px 0 6px; }
              ul { margin: 0; padding-left: 20px; }
              li { margin: 2px 0; }
              a { color: var(--accent); text-decoration: none; }
              a:hover { text-decoration: underline; }
              table { border-collapse: collapse; width: 100%; font-size: 13.5px; }
              td, th { text-align: left; padding: 4px 12px 4px 0; vertical-align: top; }
              th { color: var(--muted); font-weight: 600; font-size: 12px;
                   text-transform: uppercase; letter-spacing: .05em; }
              .state { font-size: 11.5px; font-weight: 600; }
              .state.live { color: var(--ok); }
              .state.recoverable { color: var(--warn); }
              .state.past { color: var(--muted); }
              .focus { color: var(--muted); }
              .mapnote { font-size: 13px; border-left: 3px solid var(--line);
                         padding-left: 10px; color: var(--muted); margin: 8px 0; }
              footer { color: var(--muted); font-size: 12px; margin-top: 26px; }
            </style>
            </head>
            <body>
            <div class="wrap">
            """);

        sb.Append($"<header><h1>Projects</h1><div class=\"sub\">generated {Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))} by {Esc(generatedBy)} — reproducible via <code>projects html</code></div></header>\n");

        if (entries.Count == 0)
            sb.Append("<div class=\"card\">No projects discovered. A project = <code>docs/projects/&lt;slug&gt;/project.md</code> in a registered repo.</div>\n");

        foreach (var e in entries)
        {
            var p = e.Project;
            sb.Append("<div class=\"card\">\n");

            var pillClass = p.Status.ToLowerInvariant() switch
            {
                "active" => "active",
                "paused" => "paused",
                _ => "other"
            };
            sb.Append($"<div class=\"head\"><span class=\"slug\">{Esc(p.Slug)}</span>");
            sb.Append($"<span class=\"title\">{Esc(p.Title)}</span>");
            if (!string.IsNullOrEmpty(p.Status))
                sb.Append($"<span class=\"pill {pillClass}\">{Esc(p.Status)}</span>");
            if (p.MapOnly)
                sb.Append("<span class=\"pill other\">map-only</span>");
            if (p.SprintId != null)
                sb.Append($"<span class=\"sprint\">sprint {Esc(p.SprintId)}{(p.SprintVersion != null ? $" · {Esc(p.SprintVersion)}" : "")}</span>");
            sb.Append("</div>\n");

            if (!string.IsNullOrEmpty(p.Goal))
                sb.Append($"<div class=\"goal\">{Esc(p.Goal)}</div>\n");
            if (!p.MapOnly)
                sb.Append($"<div class=\"meta\">home: {Esc(p.HomeRepo)}{(p.Repos.Count > 1 ? " · repos: " + Esc(string.Join(", ", p.Repos)) : "")}</div>\n");
            if (p.Warning != null)
                sb.Append($"<div class=\"warn\">&#9888; {Esc(p.Warning)}</div>\n");
            if (p.MapNotes != null)
                sb.Append($"<div class=\"mapnote\">{Esc(p.MapNotes)}</div>\n");

            // Artifacts: project.md + typed files as file:// links.
            if (!p.MapOnly)
            {
                sb.Append("<h3>Artifacts</h3>\n<ul>\n");
                sb.Append(ArtifactLi(Path.Combine(p.Dir, "project.md"), "project.md"));
                foreach (var t in p.TypedArtifacts)
                    sb.Append(ArtifactLi(Path.Combine(p.Dir, t), t));
                sb.Append("</ul>\n");
            }
            foreach (var link in p.MapLinks)
                sb.Append($"<div class=\"meta\">link: <a href=\"{Esc(link)}\">{Esc(link)}</a></div>\n");

            // Usual suspects: who works / worked here, with their last task-focus.
            if (e.Agents.Count > 0)
            {
                sb.Append("<h3>Usual suspects</h3>\n<table>\n<tr><th>Agent</th><th>State</th><th>Last task focus</th><th>Last seen</th></tr>\n");
                foreach (var a in e.Agents)
                {
                    var focus = a.LastFocus ?? "";
                    if (focus.Length > 140) focus = focus[..140] + "…";
                    sb.Append("<tr>");
                    sb.Append($"<td>{Esc(a.InstanceId)}{(a.Persona != null ? $" <span class=\"focus\">[{Esc(a.Persona)}]</span>" : "")}</td>");
                    sb.Append($"<td><span class=\"state {Esc(a.State)}\">{Esc(a.State)}</span></td>");
                    sb.Append($"<td class=\"focus\">{Esc(focus)}</td>");
                    sb.Append($"<td class=\"focus\">{(a.LastSeen != null ? Esc(a.LastSeen.Value.ToString("MM-dd HH:mm")) : "")}</td>");
                    sb.Append("</tr>\n");
                }
                sb.Append("</table>\n");
            }

            if (e.Claims.Count > 0)
            {
                sb.Append("<h3>Open claims</h3>\n<ul>\n");
                foreach (var c in e.Claims)
                    sb.Append($"<li>{Esc(c.SessionId)} holds {c.Files.Count} file(s) <span class=\"focus\">({Esc(c.BatchId)})</span></li>\n");
                sb.Append("</ul>\n");
            }

            sb.Append("</div>\n");
        }

        sb.Append("<footer>huddle projects lens — repo layer is standalone truth; this page is derived output and safe to delete.</footer>\n");
        sb.Append("</div>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string ArtifactLi(string path, string name)
    {
        var uri = "file:///" + path.Replace('\\', '/');
        return $"<li><a href=\"{Esc(uri)}\">{Esc(name)}</a> <span class=\"focus\">{Esc(path)}</span></li>\n";
    }

    /// <summary>Minimal HTML escape — operator/agent text must never become markup.</summary>
    public static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}

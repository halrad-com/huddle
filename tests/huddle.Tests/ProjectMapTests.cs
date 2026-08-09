using Huddle;
using Xunit;

namespace HuddleTests;

// Projects phase 1 (spec 2026-08-09): repo layer is standalone truth, huddle map is
// overlay. Discovery must be tolerant — malformed docs warn, never throw — and the
// overlay can annotate or stand alone (MapOnly) but never owns identity.
public class ProjectMapTests
{
    // ---- frontmatter -------------------------------------------------------

    [Fact]
    public void ParseFrontmatter_Roundtrip()
    {
        var text = """
            ---
            slug: casting
            title: Casting / HUBCast
            goal: One place for the cast lane
            status: active
            repos: [myapp, labs, castlib]
            ---
            # Casting
            body text
            """;
        var fm = ProjectMap.ParseFrontmatter(text);
        Assert.Equal("casting", fm["slug"]);
        Assert.Equal("Casting / HUBCast", fm["title"]);
        Assert.Equal("[myapp, labs, castlib]", fm["repos"]);
    }

    [Fact]
    public void ParseFrontmatter_NoFences_ReturnsEmpty()
    {
        Assert.Empty(ProjectMap.ParseFrontmatter("# Just a doc\nno fences here"));
    }

    [Fact]
    public void ParseFrontmatter_UnterminatedFence_ReturnsEmpty()
    {
        Assert.Empty(ProjectMap.ParseFrontmatter("---\nslug: x\nno closing fence"));
    }

    // ---- discovery ---------------------------------------------------------

    private static string MakeRepo(string root, string slug, string? sprintFm = null, params string[] typed)
    {
        var dir = Path.Combine(root, "docs", "projects", slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.md"), $"""
            ---
            slug: {slug}
            title: {slug} title
            goal: {slug} goal
            status: active
            repos: [{slug}-extra]
            ---
            notes
            """);
        foreach (var t in typed)
            File.WriteAllText(Path.Combine(dir, t), t == "SPRINT.md" && sprintFm != null
                ? sprintFm
                : $"# {t}");
        return dir;
    }

    [Fact]
    public void Discover_FindsProject_TypedArtifacts_AndSprintId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
        try
        {
            MakeRepo(root, "casting",
                sprintFm: "---\nsprint: 2608-1\nversion: v0.5.4.7\n---\nin flight",
                "ROADMAP.md", "SPRINT.md", "ISSUES.md");

            var list = ProjectMap.Discover(new[] { ("app", root) }, null, _ => { });

            var p = Assert.Single(list);
            Assert.Equal("casting", p.Slug);
            Assert.Equal("app", p.HomeRepo);
            Assert.Contains("app", p.Repos);            // home repo always a member
            Assert.Contains("casting-extra", p.Repos); // declared membership kept
            Assert.Equal("2608-1", p.SprintId);
            Assert.Equal("v0.5.4.7", p.SprintVersion);
            Assert.Equal(new[] { "ROADMAP.md", "SPRINT.md", "ISSUES.md" }.OrderBy(x => x),
                         p.TypedArtifacts.OrderBy(x => x));
            Assert.False(p.MapOnly);
            Assert.Null(p.Warning);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_MissingSlug_SkipsWithLog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(root, "docs", "projects", "broken");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "project.md"), "---\ntitle: no slug\n---\n");

            var logged = new List<string>();
            var list = ProjectMap.Discover(new[] { ("app", root) }, null, logged.Add);

            Assert.Empty(list);
            Assert.Contains(logged, m => m.Contains("broken"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_SlugConflict_FirstRepoWins_WithWarning()
    {
        var rootA = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
        var rootB = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
        try
        {
            MakeRepo(rootA, "casting");
            MakeRepo(rootB, "casting");

            var list = ProjectMap.Discover(new[] { ("first", rootA), ("second", rootB) }, null, _ => { });

            var p = Assert.Single(list);
            Assert.Equal("first", p.HomeRepo);
            Assert.NotNull(p.Warning);
            Assert.Contains("second", p.Warning);
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    // ---- overlay -----------------------------------------------------------

    [Fact]
    public void Discover_OverlayMerges_NotesAndLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
        try
        {
            MakeRepo(root, "casting");
            var map = """{"casting":{"notes":"operator context","links":["https://x"]}}""";

            var p = Assert.Single(ProjectMap.Discover(new[] { ("app", root) }, map, _ => { }));
            Assert.Equal("operator context", p.MapNotes);
            Assert.Equal("https://x", Assert.Single(p.MapLinks));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_MapOnlySlug_ListedAsMapOnly()
    {
        var map = """{"ghost":{"notes":"not yet written"}}""";
        var list = ProjectMap.Discover(Array.Empty<(string, string)>(), map, _ => { });

        var p = Assert.Single(list);
        Assert.Equal("ghost", p.Slug);
        Assert.True(p.MapOnly);
        Assert.Equal("not yet written", p.MapNotes);
    }

    [Fact]
    public void Discover_MalformedMapJson_IgnoredWithLog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
        try
        {
            MakeRepo(root, "casting");
            var logged = new List<string>();

            var p = Assert.Single(ProjectMap.Discover(new[] { ("app", root) }, "{broken", logged.Add));
            Assert.Null(p.MapNotes);
            Assert.Contains(logged, m => m.Contains("map", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Discover_RepoWithoutProjectsDir_IsFine()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            Assert.Empty(ProjectMap.Discover(new[] { ("app", root) }, null, _ => { }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

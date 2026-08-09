using System.Text.Json.Nodes;
using Huddle;
using Xunit;

namespace HuddleTests;

// I010 F4: the Bash(*) allowlist decision (2026-08-09) made durable. Merge-only —
// existing entries and unknown keys survive; unparseable files are never touched.
public class PermissionSeederTests
{
    [Fact]
    public void Merge_AppendsMissingSeeds_PreservesExisting()
    {
        var existing = """{"permissions":{"allow":["Bash(git status:*)","WebSearch"],"deny":[]}}""";
        var (json, changed) = PermissionSeeder.Merge(existing);

        Assert.True(changed);
        var node = JsonNode.Parse(json)!;
        var allow = node["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("Bash(git status:*)", allow);   // preserved
        Assert.Contains("WebSearch", allow);            // preserved
        foreach (var seed in PermissionSeeder.SeedEntries)
            Assert.Contains(seed, allow);
        Assert.NotNull(node["permissions"]!["deny"]);   // unknown/other keys survive
        // Existing entries keep their position at the front.
        Assert.Equal("Bash(git status:*)", allow[0]);
    }

    [Fact]
    public void Merge_AlreadySeeded_ReportsUnchanged()
    {
        var seeded = PermissionSeeder.Merge("""{"permissions":{"allow":[]}}""").Json;
        var (again, changed) = PermissionSeeder.Merge(seeded);
        Assert.False(changed);
        Assert.Equal(seeded, again);
    }

    [Fact]
    public void Merge_UnparseableInput_Throws()
    {
        Assert.ThrowsAny<Exception>(() => PermissionSeeder.Merge("{not json"));
    }

    [Fact]
    public void SeedRepo_AbsentFile_CreatesSeedSet()
    {
        var root = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var wrote = PermissionSeeder.SeedRepo(root, _ => { });
            Assert.True(wrote);

            var path = Path.Combine(root, ".claude", "settings.local.json");
            Assert.True(File.Exists(path));
            var allow = JsonNode.Parse(File.ReadAllText(path))!["permissions"]!["allow"]!.AsArray()
                .Select(n => n!.GetValue<string>()).ToList();
            Assert.Equal(PermissionSeeder.SeedEntries.Length, allow.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SeedRepo_UnparseableFile_LeftUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(root, ".claude");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.local.json");
            File.WriteAllText(path, "{broken");

            var logged = new List<string>();
            var wrote = PermissionSeeder.SeedRepo(root, logged.Add);

            Assert.False(wrote);
            Assert.Equal("{broken", File.ReadAllText(path)); // byte-identical
            Assert.Contains(logged, m => m.Contains("unparseable", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SeedRepo_ModifyWritesBackup_SecondRunSameDayKeepsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(root, ".claude");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.local.json");
            var original = """{"permissions":{"allow":["WebSearch"]}}""";
            File.WriteAllText(path, original);

            Assert.True(PermissionSeeder.SeedRepo(root, _ => { }));

            var backup = Path.Combine(dir, $"settings.local.json.bak-{DateTime.Now:yyyyMMdd}");
            Assert.True(File.Exists(backup));
            Assert.Equal(original, File.ReadAllText(backup)); // pre-modify content

            // Second run: already seeded → no write, and the backup keeps the
            // ORIGINAL content (not overwritten with the seeded version).
            Assert.False(PermissionSeeder.SeedRepo(root, _ => { }));
            Assert.Equal(original, File.ReadAllText(backup));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SeedRepo_MissingRepoRoot_NoOp()
    {
        var logged = new List<string>();
        Assert.False(PermissionSeeder.SeedRepo(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"), logged.Add));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Huddle;

/// <summary>
/// Seeds each registered repo's .claude/settings.local.json with the standing
/// permission allow-set (I010 F4). The 2026-08-09 decision: prefix allowlists can
/// never match compound shell commands, so every "repair" regressed into prompt
/// spam — Bash(*) plus the dedicated-tool wildcards is the standing state, and
/// huddle makes it durable across new repos and future resets.
///
/// Merge-only discipline: existing entries, their order, and unknown keys are
/// preserved; an unparseable file is NEVER touched (loud log instead — destroying
/// an operator's hand-edited file is worse than a prompt); the pre-modify content
/// is backed up once per day before the first write.
/// </summary>
public static class PermissionSeeder
{
    public static readonly string[] SeedEntries =
    {
        "Bash(*)", "Read(*)", "Edit(*)", "Write(*)",
        "Glob(*)", "Grep(*)", "WebFetch(*)", "Skill(*)"
    };

    // JsonNode.ToJsonString requires an explicit resolver on custom options (unlike
    // JsonSerializer.Serialize, which attaches the reflection resolver lazily).
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Pure merge: append missing seed entries to permissions.allow, creating the
    /// permissions/allow structure when absent. Throws on unparseable input — the
    /// caller decides what "leave it alone" looks like.
    /// </summary>
    public static (string Json, bool Changed) Merge(string existingJson)
    {
        var root = JsonNode.Parse(existingJson) as JsonObject
            ?? throw new JsonException("settings root is not an object");

        if (root["permissions"] is not JsonObject permissions)
        {
            permissions = new JsonObject();
            root["permissions"] = permissions;
        }
        if (permissions["allow"] is not JsonArray allow)
        {
            allow = new JsonArray();
            permissions["allow"] = allow;
        }

        var present = allow.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => s != null)
            .ToHashSet(StringComparer.Ordinal);

        var changed = false;
        foreach (var seed in SeedEntries)
        {
            if (present.Contains(seed)) continue;
            allow.Add(seed);
            changed = true;
        }

        return (root.ToJsonString(WriteOptions), changed);
    }

    /// <summary>
    /// Create-or-merge the repo's settings.local.json. Returns true when it wrote.
    /// </summary>
    public static bool SeedRepo(string repoRoot, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(repoRoot))
                return false;

            var dir = Path.Combine(repoRoot, ".claude");
            var path = Path.Combine(dir, "settings.local.json");

            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                var fresh = new JsonObject
                {
                    ["permissions"] = new JsonObject
                    {
                        ["allow"] = new JsonArray(SeedEntries.Select(s => (JsonNode)s).ToArray())
                    }
                };
                File.WriteAllText(path, fresh.ToJsonString(WriteOptions));
                return true;
            }

            var existing = File.ReadAllText(path);
            string merged;
            bool changed;
            try
            {
                (merged, changed) = Merge(existing);
            }
            catch (Exception ex)
            {
                log($"seed: {path} is unparseable ({ex.Message}) — left untouched; fix or delete it by hand.");
                return false;
            }

            if (!changed)
                return false;

            // Backup the PRE-modify content, once per day (an already-present backup
            // for today is earlier state — keep it).
            var backup = path + $".bak-{DateTime.Now:yyyyMMdd}";
            if (!File.Exists(backup))
                File.Copy(path, backup);

            File.WriteAllText(path, merged);
            return true;
        }
        catch (Exception ex)
        {
            log($"seed: failed for {repoRoot}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Seed every repo root; one log line per repo actually written.</summary>
    public static void SeedAll(IEnumerable<(string Name, string Root)> repos, bool enabled, Action<string> log)
    {
        if (!enabled) return;
        foreach (var (name, root) in repos)
        {
            if (SeedRepo(root, log))
                log($"Seeded permissions: {name}");
        }
    }
}

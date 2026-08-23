using Huddle;
namespace Huddle.Tests;

// S4 (review 2026-08-22): the loader used two parses with two option sets — a strict
// Deserialize plus a lenient JsonDocument parse — and the writer was lenient too. So
// `--set` could read and rewrite a commented file that the loader then refused with a raw
// JsonException. One option set now, lenient everywhere: comments and trailing commas load.
public class SettingsLenientParseTests
{
    static string Tmp(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void S4_a_config_with_comments_loads()
    {
        var p = Tmp("""
        {
          // which sessions huddle knows about
          "sessions": [],
          "settings": { "taskAckMinutes": 10 }
        }
        """);
        var cfg = HuddleConfig.Load(p);
        Assert.Equal(10, cfg.Settings.Int("taskAckMinutes"));
        File.Delete(p);
    }

    [Fact]
    public void S4_a_config_with_a_trailing_comma_loads()
    {
        var p = Tmp("""{"sessions":[],"settings":{"taskAckMinutes":10,}}""");
        Assert.Equal(10, HuddleConfig.Load(p).Settings.Int("taskAckMinutes"));
        File.Delete(p);
    }

    // The invariant that matters: whatever the writer accepts, the loader must accept.
    [Fact]
    public void S4_set_on_a_commented_file_produces_a_file_the_loader_accepts()
    {
        var p = Tmp("""
        {
          // a comment the writer will strip
          "sessions": [],
          "ipc": true,
        }
        """);
        Assert.True(SettingsWriter.TrySet(p, "taskAckMinutes", "10", out var err, out _), err);
        var cfg = HuddleConfig.Load(p);          // must not throw
        Assert.Equal(10, cfg.Settings.Int("taskAckMinutes"));
        Assert.True(cfg.Settings.Bool("ipc"));
        File.Delete(p);
    }

    // Genuinely broken JSON must still be refused — leniency is for comments and trailing
    // commas, not for anything that parses badly.
    [Fact]
    public void S4_actually_malformed_json_is_still_refused()
    {
        var p = Tmp("""{"sessions":[],""");
        Assert.ThrowsAny<Exception>(() => HuddleConfig.Load(p));
        File.Delete(p);
    }

    [Fact]
    public void S4_writer_refuses_malformed_json_rather_than_overwriting_it()
    {
        var p = Tmp("""{"sessions":[],""");
        var before = File.ReadAllText(p);
        Assert.False(SettingsWriter.TrySet(p, "taskAckMinutes", "10", out var err, out _));
        Assert.Contains("not valid JSON", err);
        Assert.Equal(before, File.ReadAllText(p));
        File.Delete(p);
    }
}

using System.Text.Json;
using Huddle;
namespace Huddle.Tests;

public class SettingsWriteTests
{
    static string Tmp(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void Set_preserves_other_keys_and_reloads_clean()
    {
        var p = Tmp("""{"sessions":[{"name":"x","root":"C:\\x","purpose":"p"}],"ipc":true}""");
        Assert.True(SettingsWriter.TrySet(p, "taskAckMinutes", "10", out var err, out _), err);
        var cfg = HuddleConfig.Load(p);
        Assert.Equal(10, cfg.Settings.Int("taskAckMinutes"));
        Assert.Single(cfg.Sessions);
        Assert.True(cfg.Ipc);
        File.Delete(p);
    }

    [Fact]
    public void Set_refuses_unknown_key()
    {
        var p = Tmp("""{"sessions":[]}""");
        Assert.False(SettingsWriter.TrySet(p, "nope", "1", out var err, out _));
        Assert.Contains("unknown setting \"nope\"", err);
        File.Delete(p);
    }

    [Fact]
    public void Set_refuses_bad_value_with_range()
    {
        var p = Tmp("""{"sessions":[]}""");
        Assert.False(SettingsWriter.TrySet(p, "gitPollSeconds", "999", out var err, out _));
        Assert.Contains("1..300", err);
        File.Delete(p);
    }

    [Fact]
    public void Set_refuses_to_overwrite_a_file_that_does_not_load()
    {
        var p = Tmp("""{"sessions":[],"settings":{"bogus":1}}""");
        var before = File.ReadAllText(p);
        Assert.False(SettingsWriter.TrySet(p, "taskAckMinutes", "10", out var err, out _));
        Assert.Contains("bogus", err);
        Assert.Equal(before, File.ReadAllText(p));
        File.Delete(p);
    }

    [Fact]
    public void Unset_removes_only_the_named_key()
    {
        var p = Tmp("""{"sessions":[],"settings":{"taskAckMinutes":10,"statsSinceDays":30}}""");
        Assert.True(SettingsWriter.TryUnset(p, "taskAckMinutes", out var err), err);
        var cfg = HuddleConfig.Load(p);
        Assert.Equal(15, cfg.Settings.Int("taskAckMinutes"));
        Assert.Equal(30, cfg.Settings.Int("statsSinceDays"));
        File.Delete(p);
    }

    [Fact]
    public void Set_accepts_on_off_for_bool()
    {
        var p = Tmp("""{"sessions":[]}""");
        Assert.True(SettingsWriter.TrySet(p, "gitActivityLog", "off", out var err, out _), err);
        Assert.False(HuddleConfig.Load(p).Settings.Bool("gitActivityLog"));
        File.Delete(p);
    }
}

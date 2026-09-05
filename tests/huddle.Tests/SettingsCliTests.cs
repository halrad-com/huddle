using Huddle;
namespace Huddle.Tests;

public class SettingsCliTests
{
    static string Tmp(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void Config_path_is_resolved_from_flag_anywhere_in_args()
    {
        Assert.Equal("x.json", SettingsCli.ResolveConfigPath(["--set", "k", "v", "--config", "x.json"]));
        Assert.Equal("y.json", SettingsCli.ResolveConfigPath(["-c", "y.json", "--settings"]));
        Assert.Equal("huddle.json", SettingsCli.ResolveConfigPath(["--settings"]));
    }

    [Fact]
    public void Set_writes_and_exits_zero()
    {
        var p = Tmp("""{"sessions":[]}""");
        var lines = new List<string>();
        var rc = SettingsCli.Run(["--set", "taskAckMinutes", "10", "--config", p], lines.Add);
        Assert.Equal(0, rc);
        Assert.Contains(lines, l => l.Contains("taskAckMinutes = 10"));
        Assert.Equal(10, HuddleConfig.Load(p).Settings.Int("taskAckMinutes"));
        File.Delete(p);
    }

    [Fact]
    public void Set_of_startup_key_says_so()
    {
        var p = Tmp("""{"sessions":[]}""");
        var lines = new List<string>();
        SettingsCli.Run(["--set", "gitPollSeconds", "9", "--config", p], lines.Add);
        Assert.Contains(lines, l => l.Contains("takes effect on reload"));
        File.Delete(p);
    }

    // --set runs BEFORE the console starts, holds no hotkey switch and cannot reach a
    // running huddle, so `Applies == Live` does not make the write live out here. When
    // peekHotkey moved from startup to live, this path fell into the branch that printed
    // nothing at all, and the operator lost the only line saying the chord had not changed
    // on the instance they were looking at.
    [Fact]
    public void Set_of_a_live_key_still_says_it_lands_on_reload()
    {
        var p = Tmp("""{"sessions":[]}""");
        var lines = new List<string>();
        SettingsCli.Run(["--set", "taskAckMinutes", "10", "--config", p], lines.Add);
        Assert.Contains(lines, l => l.Contains("takes effect on reload"));
        File.Delete(p);
    }

    [Fact]
    public void Set_of_peek_hotkey_names_the_verb_that_applies_it_immediately()
    {
        var p = Tmp("""{"sessions":[]}""");
        var lines = new List<string>();
        SettingsCli.Run(["--set", "peekHotkey", "Ctrl+Alt+J", "--config", p], lines.Add);
        Assert.Contains(lines, l => l ==
            "set — peekHotkey = Ctrl+Alt+J (takes effect on reload, or immediately from "
            + "`settings peekHotkey <chord>` inside a running huddle)");
        File.Delete(p);
    }

    [Fact]
    public void Unset_of_peek_hotkey_says_it_goes_back_to_the_candidate_chords()
    {
        var p = Tmp("""{"sessions":[],"settings":{"peekHotkey":"Ctrl+Alt+J"}}""");
        var lines = new List<string>();
        Assert.Equal(0, SettingsCli.Run(["--unset", "peekHotkey", "--config", p], lines.Add));
        Assert.Contains(lines, l => l.Contains("reverts to the built-in candidate chords"));
        File.Delete(p);
    }

    [Fact]
    public void Set_refusal_exits_one_and_names_key()
    {
        var p = Tmp("""{"sessions":[]}""");
        var lines = new List<string>();
        var rc = SettingsCli.Run(["--set", "taskAckMinutes", "zzz", "--config", p], lines.Add);
        Assert.Equal(1, rc);
        Assert.Contains(lines, l => l.Contains("taskAckMinutes"));
        File.Delete(p);
    }

    [Fact]
    public void Set_missing_args_exits_one_with_usage()
    {
        var lines = new List<string>();
        Assert.Equal(1, SettingsCli.Run(["--set", "onlykey"], lines.Add));
        Assert.Contains(lines, l => l.Contains("usage"));
    }

    [Fact]
    public void List_shows_every_key_with_source_and_applies()
    {
        var p = Tmp("""{"sessions":[],"ipc":false,"settings":{"statsSinceDays":30}}""");
        var lines = new List<string>();
        var rc = SettingsCli.Run(["--settings", "--config", p], lines.Add);
        Assert.Equal(0, rc);
        var text = string.Join("\n", lines);
        Assert.Contains("statsSinceDays", text);
        Assert.Contains("settings", text);
        Assert.Contains("top-level (legacy)", text);
        Assert.Contains("default", text);
        Assert.Contains("startup", text);
        Assert.Contains("live", text);
        foreach (var d in SettingsCatalog.All) Assert.Contains(d.Key, text);
        File.Delete(p);
    }

    [Fact]
    public void List_on_broken_config_exits_one_listing_errors()
    {
        var p = Tmp("""{"sessions":[],"settings":{"bogus":1}}""");
        var lines = new List<string>();
        Assert.Equal(1, SettingsCli.Run(["--settings", "--config", p], lines.Add));
        Assert.Contains(lines, l => l.Contains("bogus"));
        File.Delete(p);
    }
}

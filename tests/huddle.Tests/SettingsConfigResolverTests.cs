using Huddle;
namespace Huddle.Tests;

// S6 (review 2026-08-22): there were three copies of the --config scan and the CLI's copy
// was missing the myapp.json fallback, so the CLI and the console could disagree about
// which config file exists. One resolver now, shared.
//
// S3 (review 2026-08-22): `huddle --config x.json --set k v` was not dispatched at all,
// because dispatch required args[0] to be the verb. It fell through and silently booted a
// second orchestrator — while being the exact form documented at docs/settings.md.
public class SettingsConfigResolverTests
{
    static string TmpDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"huddle-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    // --- S6: one resolver -------------------------------------------------

    [Fact]
    public void S6_flag_is_found_anywhere_in_args_in_either_spelling()
    {
        Assert.Equal("x.json", ConfigPathResolver.Resolve(["--set", "k", "v", "--config", "x.json"], "."));
        Assert.Equal("y.json", ConfigPathResolver.Resolve(["-c", "y.json", "--settings"], "."));
    }

    [Fact]
    public void S6_defaults_to_huddle_json_when_no_flag_is_given()
    {
        Assert.Equal("huddle.json", ConfigPathResolver.Resolve(["--settings"], TmpDir()));
    }

    [Fact]
    public void S6_falls_back_to_seatbelt_json_when_huddle_json_is_absent()
    {
        var d = TmpDir();
        File.WriteAllText(Path.Combine(d, "myapp.json"), "{}");
        Assert.Equal(Path.Combine(d, "myapp.json"), ConfigPathResolver.Resolve([], d));
        Directory.Delete(d, true);
    }

    [Fact]
    public void S6_prefers_huddle_json_when_both_exist()
    {
        var d = TmpDir();
        File.WriteAllText(Path.Combine(d, "huddle.json"), "{}");
        File.WriteAllText(Path.Combine(d, "myapp.json"), "{}");
        Assert.Equal("huddle.json", ConfigPathResolver.Resolve([], d));
        Directory.Delete(d, true);
    }

    // An explicit --config is the operator's word; the legacy fallback must not override it.
    [Fact]
    public void S6_explicit_flag_is_never_second_guessed_by_the_fallback()
    {
        var d = TmpDir();
        File.WriteAllText(Path.Combine(d, "myapp.json"), "{}");
        Assert.Equal("nope.json", ConfigPathResolver.Resolve(["--config", "nope.json"], d));
        Directory.Delete(d, true);
    }

    [Fact]
    public void S6_a_trailing_config_flag_with_no_value_is_ignored_not_crashed()
    {
        Assert.Equal("huddle.json", ConfigPathResolver.Resolve(["--settings", "--config"], TmpDir()));
    }

    [Fact]
    public void S6_settings_cli_uses_the_shared_resolver()
    {
        Assert.Equal("x.json", SettingsCli.ResolveConfigPath(["--set", "k", "v", "--config", "x.json"]));
        Assert.Equal("y.json", SettingsCli.ResolveConfigPath(["-c", "y.json", "--settings"]));
    }

    // --- S3: dispatch on position-independent flags -----------------------

    [Fact]
    public void S3_settings_verb_is_recognised_anywhere_in_args()
    {
        Assert.Equal("--set", SettingsCli.FindVerb(["--config", "x.json", "--set", "k", "v"]));
        Assert.Equal("--settings", SettingsCli.FindVerb(["--config", "x.json", "--settings"]));
        Assert.Equal("--unset", SettingsCli.FindVerb(["-c", "x.json", "--unset", "k"]));
        Assert.Equal("--set", SettingsCli.FindVerb(["--set", "k", "v"]));
    }

    [Fact]
    public void S3_no_settings_verb_means_no_dispatch()
    {
        Assert.Null(SettingsCli.FindVerb(["--config", "x.json"]));
        Assert.Null(SettingsCli.FindVerb([]));
    }

    // A value that happens to look like a verb must not be mistaken for one.
    [Fact]
    public void S3_a_config_path_named_like_a_verb_is_not_treated_as_the_verb()
    {
        Assert.Null(SettingsCli.FindVerb(["--config", "--settings"]));
    }

    [Fact]
    public void S3_leading_config_flag_form_writes_the_setting()
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, """{"sessions":[]}""");
        var lines = new List<string>();
        var rc = SettingsCli.Run(["--config", p, "--set", "taskAckMinutes", "10"], lines.Add);
        Assert.Equal(0, rc);
        Assert.Equal(10, HuddleConfig.Load(p).Settings.Int("taskAckMinutes"));
        File.Delete(p);
    }

    [Fact]
    public void S3_leading_config_flag_form_lists_settings()
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, """{"sessions":[]}""");
        var lines = new List<string>();
        Assert.Equal(0, SettingsCli.Run(["--config", p, "--settings"], lines.Add));
        Assert.Contains(lines, l => l.Contains("taskAckMinutes"));
        File.Delete(p);
    }

    [Fact]
    public void S3_leading_config_flag_form_unsets()
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, """{"sessions":[],"settings":{"taskAckMinutes":10}}""");
        var lines = new List<string>();
        Assert.Equal(0, SettingsCli.Run(["--config", p, "--unset", "taskAckMinutes"], lines.Add));
        Assert.Equal(15, HuddleConfig.Load(p).Settings.Int("taskAckMinutes"));
        File.Delete(p);
    }
}

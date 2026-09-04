using Huddle;
namespace Huddle.Tests;

// The `settings` console verb and `reload`'s pre-validation, driven through
// ConsoleUI.HandleCommand. SessionManager's constructor only stores its arguments, so
// building one spawns nothing — no orchestrator, no ipc/ tree, no sessions.
//
// S5 (review 2026-08-22): `settings k v` split on space with max 3, so
// `settings backoffSeconds 2, 5, 15` silently wrote "2,". Bare `settings unset` reported
// unknown setting "unset".
// S2 (review 2026-08-22): reload pre-validation caught only SettingsException, so a
// trailing comma raised JsonException and killed huddle with children attached.
/// <summary>
/// Console.Out is PROCESS-GLOBAL and xUnit runs test classes in parallel, so two
/// classes that both Console.SetOut race: each captures the other's output and one
/// gets nothing. That produced a flake from 2026-08-28 on, failing a different test
/// each run and passing on re-run — the signature of a shared-state race, not a bug
/// in either test. Every class that redirects Console.Out joins this collection;
/// xUnit never runs two classes of one collection concurrently.
/// </summary>
[CollectionDefinition(ConsoleOutCollection.Name, DisableParallelization = true)]
public sealed class ConsoleOutCollection
{
    public const string Name = "console-out";
}

[Collection(ConsoleOutCollection.Name)]
public class SettingsVerbTests
{
    static (ConsoleUI ui, string path) Make(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-verb-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        var cfg = HuddleConfig.Load(p);
        var mgr = new SessionManager(cfg, "claude.exe", Path.GetTempPath(), Path.GetTempPath(), null, _ => { });
        return (new ConsoleUI(mgr) { ConfigPath = p }, p);
    }

    static string Capture(ConsoleUI ui, string command)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { ui.HandleCommand(command); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

    // --- the display surface ---------------------------------------------

    [Fact]
    public void Bare_settings_lists_every_key_with_source_and_applies()
    {
        var (ui, p) = Make("""{"sessions":[],"ipc":false,"settings":{"statsSinceDays":30}}""");
        var text = Capture(ui, "settings");
        foreach (var d in SettingsCatalog.All) Assert.Contains(d.Key, text);
        Assert.Contains("top-level (legacy)", text);
        Assert.Contains("startup", text);
        Assert.Contains("live", text);
        File.Delete(p);
    }

    [Fact]
    public void Settings_key_shows_one_key_in_detail()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        var text = Capture(ui, "settings taskAckMinutes");
        Assert.Contains("taskAckMinutes = 15", text);
        Assert.Contains("1..1440", text);
        File.Delete(p);
    }

    [Fact]
    public void Settings_refuses_unknown_key_and_bad_value()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        Assert.Contains("unknown setting", Capture(ui, "settings bogus"));
        Assert.Contains("refused", Capture(ui, "settings gitPollSeconds 999"));
        File.Delete(p);
    }

    // --- S5: the rest of the line is the value ---------------------------

    [Fact]
    public void S5_a_value_containing_spaces_is_taken_whole_not_truncated_at_the_first_space()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        var text = Capture(ui, "settings backoffSeconds 2, 5, 15");
        Assert.DoesNotContain("refused", text);
        // Stored canonically, and critically NOT the "2," the old max-3 split produced.
        Assert.Equal("2,5,15", HuddleConfig.Load(p).Settings.Text("backoffSeconds"));
        Assert.Equal(new[] { 2, 5, 15 }, HuddleConfig.Load(p).Settings.IntList("backoffSeconds"));
        File.Delete(p);
    }

    [Fact]
    public void S5_a_spaced_value_that_is_genuinely_invalid_is_still_refused()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        Assert.Contains("refused", Capture(ui, "settings backoffSeconds 2, x, 15"));
        File.Delete(p);
    }

    [Fact]
    public void S5_bare_unset_prints_usage_rather_than_unknown_setting_unset()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        var text = Capture(ui, "settings unset");
        Assert.Contains("usage", text);
        Assert.DoesNotContain("unknown setting \"unset\"", text);
        File.Delete(p);
    }

    [Fact]
    public void S5_unset_with_a_key_reverts_only_that_key()
    {
        var (ui, p) = Make("""{"sessions":[],"settings":{"taskAckMinutes":10,"statsSinceDays":30}}""");
        Assert.Contains("unset taskAckMinutes", Capture(ui, "settings unset taskAckMinutes"));
        var cfg = HuddleConfig.Load(p);
        Assert.Equal(15, cfg.Settings.Int("taskAckMinutes"));
        Assert.Equal(30, cfg.Settings.Int("statsSinceDays"));
        File.Delete(p);
    }

    [Fact]
    public void S5_setting_a_single_word_value_still_works()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        Assert.Contains("set taskAckMinutes = 10", Capture(ui, "settings taskAckMinutes 10"));
        Assert.Equal(10, HuddleConfig.Load(p).Settings.Int("taskAckMinutes"));
        File.Delete(p);
    }

    [Fact]
    public void Settings_startup_key_says_takes_effect_on_reload()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        Assert.Contains("takes effect on reload", Capture(ui, "settings gitPollSeconds 9"));
        File.Delete(p);
    }

    // --- S2: reload refuses on EVERY load failure ------------------------

    [Theory]
    [InlineData("""{"sessions":[],"settings":{"bogus":1}}""")]                 // SettingsException
    [InlineData("""{"sessions":[],"settings":{"gitPollSeconds":0}}""")]        // out of range
    [InlineData("""{"sessions":[],}""" + "\n{bad")]                            // JsonException
    [InlineData("""not json at all""")]                                        // JsonException
    public void S2_reload_refuses_and_stays_up_for_any_unloadable_config(string broken)
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        File.WriteAllText(p, broken);            // broken underneath the running instance
        var text = Capture(ui, "reload /y");
        Assert.Contains("reload: refused", text);
        Assert.DoesNotContain("helper launched", text);
        File.Delete(p);
    }

    [Fact]
    public void S2_reload_refuses_when_the_config_has_been_deleted()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        File.Delete(p);
        var text = Capture(ui, "reload /y");
        Assert.Contains("reload: refused", text);
        Assert.DoesNotContain("helper launched", text);
    }

    [Fact]
    public void S2_reload_names_the_problem_so_the_operator_can_fix_it()
    {
        var (ui, p) = Make("""{"sessions":[]}""");
        File.WriteAllText(p, """{"sessions":[],"settings":{"bogus":1}}""");
        Assert.Contains("bogus", Capture(ui, "reload /y"));
        File.Delete(p);
    }
}

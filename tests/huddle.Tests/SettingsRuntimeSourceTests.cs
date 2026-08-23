using Huddle;
namespace Huddle.Tests;

// S1 (review 2026-08-22): config.Settings must be the ONLY runtime source for the nine
// pre-existing keys. Before this, every reader used the legacy POCO property, so `--set`
// changed the file and the `settings` display but NOT behaviour — the precedence the docs
// promise was inverted in practice and the startup "using settings (x)" warning was false.
//
// The five NEW keys (statsSinceDays, gitActivityLog, gitPollSeconds, taskAckMinutes,
// transcriptMaxScan) have no consumer yet BY DESIGN — stats and ledger-p2 own them.
public class SettingsRuntimeSourceTests
{
    static string Tmp(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void S1_settings_block_wins_over_top_level_for_every_legacy_key()
    {
        var p = Tmp("""
        {
          "sessions": [],
          "contextFile": true, "ipc": true, "crashLogRetention": 10,
          "rescanIntervalSeconds": 30, "reclaimResourcesOnStop": false,
          "seedPermissions": true, "autoRestart": false, "maxAutoRestarts": 3,
          "backoffSeconds": [2, 5, 15],
          "settings": {
            "contextFile": false, "ipc": false, "crashLogRetention": 99,
            "rescanIntervalSeconds": 45, "reclaimResourcesOnStop": true,
            "seedPermissions": false, "autoRestart": true, "maxAutoRestarts": 7,
            "backoffSeconds": "1,2,3"
          }
        }
        """);
        var s = HuddleConfig.Load(p).Settings;
        Assert.False(s.Bool("contextFile"));
        Assert.False(s.Bool("ipc"));
        Assert.Equal(99, s.Int("crashLogRetention"));
        Assert.Equal(45, s.Int("rescanIntervalSeconds"));
        Assert.True(s.Bool("reclaimResourcesOnStop"));
        Assert.False(s.Bool("seedPermissions"));
        Assert.True(s.Bool("autoRestart"));
        Assert.Equal(7, s.Int("maxAutoRestarts"));
        Assert.Equal(new[] { 1, 2, 3 }, s.IntList("backoffSeconds"));
        File.Delete(p);
    }

    [Fact]
    public void S1_auto_restart_resolves_from_settings_not_the_legacy_poco()
    {
        var p = Tmp("""
        {"sessions":[],"autoRestart":false,"maxAutoRestarts":3,"backoffSeconds":[2,5,15],
         "settings":{"autoRestart":true,"maxAutoRestarts":7,"backoffSeconds":"1,2,3"}}
        """);
        var cfg = HuddleConfig.Load(p);
        var (enabled, max, backoff) = cfg.GetAutoRestartConfig(new SessionDefinition { Name = "x" });
        Assert.True(enabled);
        Assert.Equal(7, max);
        Assert.Equal(new[] { 1, 2, 3 }, backoff);
        File.Delete(p);
    }

    // Spec section 10: per-session overrides are out of scope and must keep winning.
    [Fact]
    public void S1_session_overrides_still_beat_the_settings_block()
    {
        var p = Tmp("""
        {"sessions":[],"settings":{"autoRestart":true,"maxAutoRestarts":7,"backoffSeconds":"1,2,3"}}
        """);
        var cfg = HuddleConfig.Load(p);
        var session = new SessionDefinition
        {
            Name = "x", AutoRestart = false, MaxAutoRestarts = 1, BackoffSeconds = [9]
        };
        var (enabled, max, backoff) = cfg.GetAutoRestartConfig(session);
        Assert.False(enabled);
        Assert.Equal(1, max);
        Assert.Equal(new[] { 9 }, backoff);
        File.Delete(p);
    }

    [Fact]
    public void S1_legacy_top_level_still_drives_behaviour_when_no_block_is_present()
    {
        var p = Tmp("""{"sessions":[],"autoRestart":true,"maxAutoRestarts":5,"backoffSeconds":[4,8]}""");
        var cfg = HuddleConfig.Load(p);
        var (enabled, max, backoff) = cfg.GetAutoRestartConfig(new SessionDefinition { Name = "x" });
        Assert.True(enabled);
        Assert.Equal(5, max);
        Assert.Equal(new[] { 4, 8 }, backoff);
        File.Delete(p);
    }

    // The old code guarded an empty backoff array; the text tier must not lose that.
    [Fact]
    public void S1_empty_backoff_still_falls_back_to_a_single_two_second_wait()
    {
        var p = Tmp("""{"sessions":[],"backoffSeconds":[]}""");
        var cfg = HuddleConfig.Load(p);
        var (_, _, backoff) = cfg.GetAutoRestartConfig(new SessionDefinition { Name = "x" });
        Assert.Equal(new[] { 2 }, backoff);
        File.Delete(p);
    }

    [Fact]
    public void S1_default_backoff_is_the_catalog_default_when_nothing_is_configured()
    {
        var p = Tmp("""{"sessions":[]}""");
        var cfg = HuddleConfig.Load(p);
        var (_, _, backoff) = cfg.GetAutoRestartConfig(new SessionDefinition { Name = "x" });
        Assert.Equal(new[] { 2, 5, 15 }, backoff);
        File.Delete(p);
    }

    [Fact]
    public void S1_IntList_parses_the_comma_separated_text_tier()
    {
        var s = ResolvedSettings.Defaults();
        Assert.Equal(new[] { 2, 5, 15 }, s.IntList("backoffSeconds"));
    }
}

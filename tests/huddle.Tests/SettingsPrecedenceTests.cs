using Huddle;
namespace Huddle.Tests;

public class SettingsPrecedenceTests
{
    static Dictionary<string,string> D(params (string k, string v)[] kv) =>
        kv.ToDictionary(p => p.k, p => p.v, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Settings_block_beats_top_level()
    {
        var r = SettingsResolver.Resolve(D(("ipc","false")), D(("ipc","true")));
        Assert.False(r.Bool("ipc"));
        Assert.Equal(SettingSource.Settings, r.Get("ipc").Source);
        Assert.Single(r.Warnings); // duplicate reported once
    }

    [Fact]
    public void Top_level_used_when_absent_from_block()
    {
        var r = SettingsResolver.Resolve(D(), D(("rescanIntervalSeconds","45")));
        Assert.Equal(45, r.Int("rescanIntervalSeconds"));
        Assert.Equal(SettingSource.TopLevelLegacy, r.Get("rescanIntervalSeconds").Source);
    }

    [Fact]
    public void Default_when_neither_present()
    {
        var r = SettingsResolver.Resolve(D(), D());
        Assert.Equal(15, r.Int("taskAckMinutes"));
        Assert.Equal(SettingSource.Default, r.Get("taskAckMinutes").Source);
        Assert.Equal(SettingsCatalog.All.Count, r.All.Count);
    }

    [Fact]
    public void HuddleConfig_load_refuses_bad_settings_listing_every_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"sessions":[],"settings":{"bogus":1,"ipc":"x"}}""");
        var ex = Assert.Throws<SettingsException>(() => HuddleConfig.Load(path));
        Assert.Equal(2, ex.Errors.Count);
        File.Delete(path);
    }

    [Fact]
    public void HuddleConfig_load_without_block_behaves_as_today()
    {
        var path = Path.Combine(Path.GetTempPath(), $"huddle-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"sessions":[],"rescanIntervalSeconds":12}""");
        var cfg = HuddleConfig.Load(path);
        Assert.Equal(12, cfg.RescanIntervalSeconds);
        Assert.Equal(12, cfg.Settings.Int("rescanIntervalSeconds"));
        Assert.Equal(SettingSource.TopLevelLegacy, cfg.Settings.Get("rescanIntervalSeconds").Source);
        File.Delete(path);
    }
}

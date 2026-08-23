using System.Text.Json;
using Huddle;
namespace Huddle.Tests;

public class SettingsLoadTests
{
    static JsonElement? Block(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Absent_block_is_not_an_error()
    {
        var r = SettingsLoader.Load(null, "huddle.json");
        Assert.Empty(r.Errors);
        Assert.Empty(r.Values);
    }

    [Fact]
    public void Valid_values_are_accepted_and_normalized()
    {
        var r = SettingsLoader.Load(Block("""{"taskAckMinutes": 10, "gitActivityLog": false, "backoffSeconds": "1,2"}"""), "f");
        Assert.Empty(r.Errors);
        Assert.Equal("10", r.Values["taskAckMinutes"]);
        Assert.Equal("false", r.Values["gitActivityLog"]);
        Assert.Equal("1,2", r.Values["backoffSeconds"]);
    }

    [Fact]
    public void Unknown_key_is_refused_by_name()
    {
        var r = SettingsLoader.Load(Block("""{"bogus": 1}"""), "f");
        var e = Assert.Single(r.Errors);
        Assert.Contains("\"bogus\"", e);
        Assert.Contains("unknown setting", e);
    }

    [Fact]
    public void Near_miss_gets_did_you_mean()
    {
        var r = SettingsLoader.Load(Block("""{"rescanIntervalSecond": 30}"""), "f");
        var e = Assert.Single(r.Errors);
        Assert.Contains("did you mean \"rescanIntervalSeconds\"", e);
    }

    [Fact]
    public void Out_of_range_names_the_range()
    {
        var r = SettingsLoader.Load(Block("""{"rescanIntervalSeconds": -5}"""), "f");
        var e = Assert.Single(r.Errors);
        Assert.Contains("rescanIntervalSeconds", e);
        Assert.Contains("0..3600", e);
    }

    [Fact]
    public void Wrong_json_type_is_refused()
    {
        var r = SettingsLoader.Load(Block("""{"ipc": "yes"}"""), "f");
        var e = Assert.Single(r.Errors);
        Assert.Contains("must be true or false", e);
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var r = SettingsLoader.Load(Block("""{"bogus": 1, "ipc": "yes", "gitPollSeconds": 0}"""), "f");
        Assert.Equal(3, r.Errors.Count);
    }

    [Fact]
    public void Non_object_block_is_refused()
    {
        var r = SettingsLoader.Load(Block("[1,2]"), "f");
        var e = Assert.Single(r.Errors);
        Assert.Contains("must be a JSON object", e);
    }

    [Fact]
    public void BackoffSeconds_must_be_comma_separated_positive_ints()
    {
        var r = SettingsLoader.Load(Block("""{"backoffSeconds": "2,x"}"""), "f");
        Assert.Single(r.Errors);
    }

    [Fact]
    public void Catalog_has_the_fourteen_spec_keys()
    {
        Assert.Equal(14, SettingsCatalog.All.Count);
        Assert.True(SettingsCatalog.TryGet("TASKACKMINUTES", out var d)); // case-insensitive
        Assert.Equal("taskAckMinutes", d.Key);
    }
}

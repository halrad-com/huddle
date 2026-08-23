using Huddle;
namespace Huddle.Tests;

public class StatsAttributionTests
{
    static readonly DateTimeOffset T = new(2026, 8, 21, 22, 0, 0, TimeSpan.Zero);
    static RosterWindow W(string inst, DateTimeOffset s, DateTimeOffset? e = null) => new(inst, inst.Split(':')[0], s, e);
    static GitActivityEntry Auth(string inst, DateTimeOffset ts) => new(ts, "auth", inst, "s", "dev.azure.com", "https", null, null, null, null, null, null);
    static Movement Mv(DateTimeOffset ts) => new(ts, "origin", "master", "push", "abc1234", "dev.azure.com/x/y");

    [Fact]
    public void Cred_request_is_exact()
    {
        var a = Attributor.ForRepo("myapp", [W("myapp:architect", T.AddHours(-5))], [Auth("myapp:architect", T)], [], [], []);
        var x = Assert.Single(a);
        Assert.Equal(AttributionGrade.Exact, x.Grade);
        Assert.Contains(x.Evidence, e => e.Contains("cred"));
    }

    [Fact]
    public void Sole_live_session_at_movement_is_exact_two_live_are_inferred()
    {
        var roster1 = new[] { W("myapp:architect", T.AddHours(-5)) };
        var a1 = Attributor.ForRepo("myapp", roster1, [], [Mv(T)], [], []);
        Assert.Equal(AttributionGrade.Exact, Assert.Single(a1).Grade);

        var roster2 = new[] { W("myapp:architect", T.AddHours(-5)), W("myapp:backenddev", T.AddHours(-3)) };
        var a2 = Attributor.ForRepo("myapp", roster2, [], [Mv(T)], [], []);
        Assert.Equal(2, a2.Count);
        Assert.All(a2, x => Assert.Equal(AttributionGrade.Inferred, x.Grade));
    }

    [Fact]
    public void Dead_session_outside_window_is_not_a_candidate()
    {
        var roster = new[] { W("myapp:old", T.AddDays(-3), T.AddDays(-2)), W("myapp:architect", T.AddHours(-1)) };
        var a = Attributor.ForRepo("myapp", roster, [], [Mv(T)], [], []);
        Assert.Equal("myapp:architect", Assert.Single(a).Instance);
    }

    [Fact]
    public void Other_repo_sessions_are_ignored()
    {
        var a = Attributor.ForRepo("myapp", [W("otherapp:architect", T.AddHours(-5))], [Auth("otherapp:architect", T)], [Mv(T)], [], []);
        Assert.Empty(a);
    }

    [Fact]
    public void Exact_beats_inferred_for_the_same_instance()
    {
        var roster = new[] { W("myapp:architect", T.AddHours(-5)), W("myapp:backenddev", T.AddHours(-3)) };
        var a = Attributor.ForRepo("myapp", roster, [Auth("myapp:architect", T)], [Mv(T)], [], []);
        Assert.Equal(AttributionGrade.Exact, a.Single(x => x.Instance == "myapp:architect").Grade);
        Assert.Equal(AttributionGrade.Inferred, a.Single(x => x.Instance == "myapp:backenddev").Grade);
    }
}

using Huddle;
namespace Huddle.Tests;

public class BroadcastTargetingTests
{
    // fake resolver: "app" is an alias of "myapp"; known repos: myapp, webshop
    private static string Resolve(string n) => n.Equals("app", StringComparison.OrdinalIgnoreCase) ? "myapp" : n;
    private static bool Known(string n) => n is "myapp" or "webshop";

    [Fact]
    public void Single_repo_resolves()
    {
        var set = BroadcastTargeting.ResolveRepoFilter("myapp", Resolve, Known, out var err);
        Assert.Null(err);
        Assert.NotNull(set);
        Assert.Contains("myapp", set!);
    }

    [Fact]
    public void Alias_resolves_to_canonical()
    {
        var set = BroadcastTargeting.ResolveRepoFilter("app", Resolve, Known, out _);
        Assert.Contains("myapp", set!);
    }

    [Fact]
    public void Comma_list_with_spaces_resolves_all()
    {
        var set = BroadcastTargeting.ResolveRepoFilter(" app , webshop ", Resolve, Known, out _);
        Assert.Equal(2, set!.Count);
        Assert.Contains("webshop", set);
    }

    [Fact]
    public void Unknown_repo_errors_with_original_token()
    {
        var set = BroadcastTargeting.ResolveRepoFilter("app,bogus", Resolve, Known, out var err);
        Assert.Null(set);
        Assert.Equal("unknown repo 'bogus'", err);
    }

    [Fact]
    public void Empty_filter_errors()
    {
        var set = BroadcastTargeting.ResolveRepoFilter(" , ,", Resolve, Known, out var err);
        Assert.Null(set);
        Assert.Equal("repo filter contains no repo names", err);
    }

    [Fact]
    public void MatchesRepo_uses_prefix_before_colon_case_insensitive()
    {
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp" };
        Assert.True(BroadcastTargeting.MatchesRepo("myapp:architect", repos));
        Assert.True(BroadcastTargeting.MatchesRepo("MyApp:documenter", repos));
        Assert.True(BroadcastTargeting.MatchesRepo("myapp", repos));   // bare repo id
        Assert.False(BroadcastTargeting.MatchesRepo("webshop:architect", repos));
    }
}

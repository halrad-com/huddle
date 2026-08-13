using Xunit;
using Huddle;

namespace HuddleTests;

public class ArgCompleterTests
{
    // Fixed, deterministic providers — no live sessions needed.
    private static ArgCompleter Make() => new(new ArgProviders
    {
        LiveInstances    = () => new[] { "myapp:architect", "otherapp:architect", "myapp:researcher" },
        StoppedInstances = () => new[] { "myapp:versioner" },
        Repos            = () => new[] { "otherapp", "myapp", "huddle" },
        Personas         = () => new[] { "architect", "reviewer" },
    });

    // --- verb position unchanged ---

    [Fact]
    public void First_token_still_completes_verbs()
    {
        var c = Make();
        Assert.Contains("broadcast", c.Complete("broadc"));
    }

    // --- instance args ---

    [Fact]
    public void Say_completes_live_instances_as_whole_lines()
    {
        var c = Make();
        Assert.Equal(new[] { "say otherapp:architect" }, c.Complete("say oth"));
    }

    [Fact]
    public void Say_with_empty_arg_offers_all_live_instances_sorted()
    {
        var c = Make();
        Assert.Equal(new[]
        {
            "say myapp:architect",
            "say myapp:researcher",
            "say otherapp:architect",
        }, c.Complete("say "));
    }

    [Fact]
    public void Resume_offers_stopped_instances()
    {
        var c = Make();
        Assert.Equal(new[] { "resume myapp:versioner" }, c.Complete("resume my"));
    }

    [Fact]
    public void Stop_offers_instances_and_repos()
    {
        var c = Make();
        var got = c.Complete("stop oth");
        Assert.Contains("stop otherapp:architect", got);
        Assert.Contains("stop otherapp", got);
    }

    // --- positional providers ---

    [Fact]
    public void Start_first_arg_completes_repos_second_completes_personas()
    {
        var c = Make();
        Assert.Equal(new[] { "start huddle" }, c.Complete("start hu"));
        Assert.Equal(new[] { "start huddle architect" }, c.Complete("start huddle arc"));
    }

    // --- @repo tokens ---

    [Fact]
    public void Broadcast_at_token_completes_repos()
    {
        var c = Make();
        Assert.Equal(new[] { "broadcast @otherapp" }, c.Complete("broadcast @oth"));
    }

    [Fact]
    public void Docs_bare_at_offers_all_repos()
    {
        var c = Make();
        Assert.Equal(new[] { "docs @huddle", "docs @myapp", "docs @otherapp" }, c.Complete("docs @"));
    }

    [Fact]
    public void At_token_completes_after_the_last_comma()
    {
        var c = Make();
        Assert.Equal(new[] { "broadcast @otherapp,myapp" }, c.Complete("broadcast @otherapp,my"));
    }

    // --- free-text / unknown args stay quiet ---

    [Fact]
    public void Broadcast_message_position_has_no_completion()
    {
        var c = Make();
        Assert.Empty(c.Complete("broadcast hold the"));
    }

    [Fact]
    public void Unknown_verb_args_have_no_completion()
    {
        var c = Make();
        Assert.Empty(c.Complete("status extra"));
    }

    // --- hints ---

    [Fact]
    public void Hint_shows_arg_grammar_right_after_the_verb()
    {
        var c = Make();
        Assert.Equal("[@repo[,repo]] <message>", c.Hint("broadcast "));
    }

    [Fact]
    public void Hint_empty_once_an_arg_is_typed()
    {
        var c = Make();
        Assert.Equal("", c.Hint("broadcast hol"));
    }

    [Fact]
    public void Hint_empty_for_argless_verbs()
    {
        var c = Make();
        Assert.Equal("", c.Hint("status "));
    }

    [Fact]
    public void Hint_empty_while_verb_is_incomplete()
    {
        var c = Make();
        Assert.Equal("", c.Hint("broadc"));
    }
}

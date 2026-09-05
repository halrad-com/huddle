using Xunit;
using Huddle;

namespace HuddleTests;

public class VerbCompleterTests
{
    // Small fixed vocabulary so tests are independent of the live catalog.
    private static VerbCompleter Make() =>
        new(new[] { "say", "send", "shell", "status", "broadcast" });

    [Fact]
    public void Prefix_returns_matches_ordered_alphabetically()
    {
        var c = Make();
        Assert.Equal(new[] { "say", "send", "shell", "status" }, c.Complete("s"));
    }

    [Fact]
    public void Exact_longer_prefix_narrows()
    {
        var c = Make();
        Assert.Equal(new[] { "send", "shell" }, c.Complete("s").Where(v => v.StartsWith("sh") || v == "send").ToArray()); // sanity
        Assert.Equal(new[] { "shell" }, c.Complete("she"));
    }

    [Fact]
    public void No_match_returns_empty()
    {
        Assert.Empty(Make().Complete("zzz"));
    }

    [Fact]
    public void Empty_input_returns_all_verbs()
    {
        Assert.Equal(new[] { "broadcast", "say", "send", "shell", "status" }, Make().Complete(""));
    }

    [Fact]
    public void Once_a_space_is_typed_no_verb_completion()
    {
        // Args are not completed in v1.
        Assert.Empty(Make().Complete("say tru"));
    }

    [Fact]
    public void Live_catalog_has_one_entry_per_primary_switch_verb()
    {
        // Invariant: Verbs.Catalog carries exactly one entry per primary `case`
        // verb in ConsoleUI.HandleCommand's switch — aliases (s, r, p, q, exit,
        // ?, h, msg, unread, goto, rebuild, handoff, version) are excluded.
        // Adding a verb to that switch means adding it here and bumping this
        // count; this assertion is what makes forgetting the catalog a failure.
        Assert.Equal(42, Verbs.Catalog.Count);
        Assert.Equal(42, Verbs.Catalog.Select(v => v.Name).Distinct().Count());
    }

    [Fact]
    public void Live_catalog_is_non_empty_and_contains_known_verbs()
    {
        var names = Verbs.Catalog.Select(v => v.Name).ToHashSet();
        Assert.Contains("broadcast", names);
        Assert.Contains("say", names);
        Assert.Contains("status", names);
    }

    [Fact]
    public void Stats_is_in_the_catalog_with_argument_grammar()
    {
        // Argument-level help is the feature, not verb completion alone: typing
        // "stats " must show the grammar, or the verb reads as unfinished.
        Assert.Contains("stats", Verbs.Catalog.Select(v => v.Name));
        var hint = new ArgCompleter(new ArgProviders()).Hint("stats ");
        Assert.Contains("--who", hint);
        Assert.Contains("--since", hint);
        Assert.Contains("html", hint);
    }
}

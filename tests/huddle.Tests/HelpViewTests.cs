using Huddle;
using Xunit;

namespace HuddleTests;

// Help groups (spec 2026-08-31-shell-registration-design.md section 4): help renders
// FROM the verb catalog — structure from data, no hand-maintained duplicate list.
public class HelpViewTests
{
    [Fact]
    public void Every_catalog_verb_declares_a_known_group()
    {
        foreach (var v in Verbs.Catalog)
            Assert.Contains(v.Group, HelpView.GroupOrder);
    }

    [Fact]
    public void Compact_render_lists_every_verb_exactly_once_in_group_order()
    {
        var lines = HelpView.RenderCompact(Verbs.Catalog);
        var joined = string.Join("\n", lines);
        foreach (var v in Verbs.Catalog)
        {
            var count = lines.Count(l => (" " + l + " ").Contains(" " + v.Name + " ") ||
                                          l.EndsWith(" " + v.Name));
            Assert.True(count >= 1, $"verb '{v.Name}' missing from compact help");
        }
        // Groups appear in the declared order.
        var idx = HelpView.GroupOrder
            .Select(g => Array.FindIndex(lines.ToArray(), l => l.StartsWith(g)))
            .ToArray();
        Assert.All(idx, i => Assert.True(i >= 0));
        Assert.Equal(idx.OrderBy(i => i), idx);
    }

    [Fact]
    public void Full_render_contains_every_usage_line()
    {
        var joined = string.Join("\n", HelpView.RenderFull(Verbs.Catalog));
        foreach (var v in Verbs.Catalog)
            Assert.Contains(v.Usage, joined);
    }

    [Fact]
    public void Single_verb_render_returns_its_usage_and_flags_unknown()
    {
        var known = HelpView.RenderVerb(Verbs.Catalog, "census");
        Assert.Contains(known, l => l.Contains("census"));

        var unknown = HelpView.RenderVerb(Verbs.Catalog, "zzz");
        Assert.Contains(unknown, l => l.Contains("zzz") && l.Contains("unknown"));
    }
}

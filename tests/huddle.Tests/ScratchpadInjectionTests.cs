using Huddle;
using Xunit;

namespace HuddleTests;

// A scratchpad is injected whole into every spawn's system prompt. Fleet scratchpads
// measured 80-500 KB on 2026-09-09; the largest outweighed the task prompt 50:1 with
// months of stale state claims. The cap bounds what is PUSHED; the file on disk and
// `find` keep everything.
public class ScratchpadInjectionTests
{
    private static string Sections(int n, int lines)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= n; i++)
        {
            sb.Append("## Checkpoint ").Append(i).Append('\n');
            for (var l = 0; l < lines; l++) sb.Append("line ").Append(i).Append('.').Append(l).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Zero_cap_means_the_whole_file()
    {
        var content = Sections(20, 10);
        var s = ScratchpadInjection.Select(content, 0);
        Assert.False(s.Truncated);
        Assert.Equal(content, s.Text);
        Assert.Equal(content.Length, s.TotalChars);
    }

    [Fact]
    public void Content_under_the_cap_is_untouched()
    {
        var content = Sections(2, 3);
        var s = ScratchpadInjection.Select(content, content.Length + 1);
        Assert.False(s.Truncated);
        Assert.Equal(content, s.Text);
    }

    [Fact]
    public void Over_the_cap_takes_the_tail_starting_at_a_section_heading()
    {
        var content = Sections(20, 10);
        var s = ScratchpadInjection.Select(content, 400);
        Assert.True(s.Truncated);
        Assert.StartsWith("## Checkpoint ", s.Text);
        Assert.EndsWith("line 20.9\n\n", s.Text);
        Assert.True(s.Text.Length <= 400, $"shown {s.Text.Length} > cap 400");
        Assert.Equal(content.Length, s.TotalChars);
    }

    [Fact]
    public void Tail_never_starts_at_a_heading_before_the_cut()
    {
        // Snapping BACKWARD to a heading would overshoot the cap; snap forward only.
        var content = Sections(3, 200);
        var s = ScratchpadInjection.Select(content, 100);
        Assert.True(s.Truncated);
        Assert.True(s.Text.Length <= 100);
    }

    [Fact]
    public void Without_a_heading_in_the_tail_it_starts_on_a_line_boundary()
    {
        var content = string.Concat(Enumerable.Range(0, 50).Select(i => $"row {i:00} of a headingless file\n"));
        var s = ScratchpadInjection.Select(content, 120);
        Assert.True(s.Truncated);
        Assert.StartsWith("row ", s.Text);
        Assert.True(s.Text.Length <= 120);
        Assert.EndsWith("row 49 of a headingless file\n", s.Text);
    }

    [Fact]
    public void Preface_says_how_much_was_left_out_and_where_the_rest_is()
    {
        var content = Sections(20, 10);
        var s = ScratchpadInjection.Select(content, 400);
        var p = ScratchpadInjection.Preface(s, @"C:\x\logs\a_b\scratchpad.md");
        Assert.Contains($"last {s.Text.Length} of {content.Length} chars", p);
        Assert.Contains(@"C:\x\logs\a_b\scratchpad.md", p);

        var whole = ScratchpadInjection.Select(content, 0);
        var q = ScratchpadInjection.Preface(whole, "p");
        Assert.DoesNotContain("last ", q);
    }
}

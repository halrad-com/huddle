using Huddle;
using Xunit;

namespace HuddleTests;

// The chord is operator-editable text in huddle.json, so parsing it is the one part
// of the hotkey path that can be wrong in a way a test can catch.
public class PeekChordTests
{
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;

    // A candidate the parser cannot express, or one repeated, is a dead entry in the list
    // whose entire purpose is to have a live one: it costs a registration attempt and gives
    // the operator nothing. Ctrl+Alt+OemPlus measured free on the operator's desktop and is
    // deliberately NOT in the list for exactly this reason, since the grammar is letters,
    // digits and F1..F24 only.
    [Fact]
    public void Every_fallback_candidate_is_a_chord_this_parser_accepts()
    {
        Assert.NotEmpty(PeekChord.Candidates);
        foreach (var chord in PeekChord.Candidates)
            Assert.True(PeekChord.TryParse(chord, out _, out _), $"candidate '{chord}' does not parse");
    }

    [Fact]
    public void No_fallback_candidate_repeats_another()
    {
        var seen = PeekChord.Candidates.Select(c =>
        {
            PeekChord.TryParse(c, out var mods, out var vk);
            return (mods, vk);
        }).ToList();
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void Parses_the_default_chord()
    {
        Assert.True(PeekChord.TryParse("Ctrl+Alt+H", out var mods, out var vk));
        Assert.Equal(MOD_CONTROL | MOD_ALT, mods);
        Assert.Equal((uint)'H', vk);
    }

    [Fact]
    public void Is_case_and_space_insensitive()
    {
        Assert.True(PeekChord.TryParse("  ctrl + alt + h ", out var mods, out var vk));
        Assert.Equal(MOD_CONTROL | MOD_ALT, mods);
        Assert.Equal((uint)'H', vk);
    }

    [Fact]
    public void Accepts_the_common_spellings_of_each_modifier()
    {
        Assert.True(PeekChord.TryParse("Control+Shift+Win+Alt+K", out var mods, out _));
        Assert.Equal(MOD_CONTROL | MOD_SHIFT | MOD_WIN | MOD_ALT, mods);
    }

    [Fact]
    public void Accepts_digits_and_function_keys()
    {
        Assert.True(PeekChord.TryParse("Alt+1", out _, out var digit));
        Assert.Equal((uint)'1', digit);

        Assert.True(PeekChord.TryParse("Ctrl+F9", out _, out var f9));
        Assert.Equal(0x78u, f9);   // VK_F1 is 0x70, so VK_F9 is 0x70 + 8
    }

    // A chord with no modifier would swallow a bare key globally, across every
    // application on the desktop. Refuse it rather than register it.
    [Fact]
    public void Refuses_a_chord_with_no_modifier()
    {
        Assert.False(PeekChord.TryParse("H", out _, out _));
    }

    [Fact]
    public void Refuses_a_chord_with_no_key()
    {
        Assert.False(PeekChord.TryParse("Ctrl+Alt", out _, out _));
    }

    [Fact]
    public void Refuses_junk_and_empty_input()
    {
        Assert.False(PeekChord.TryParse("Ctrl+Alt+Nonsense", out _, out _));
        Assert.False(PeekChord.TryParse("", out _, out _));
        Assert.False(PeekChord.TryParse("   ", out _, out _));
    }
}

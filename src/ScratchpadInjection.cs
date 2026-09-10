namespace Huddle;

/// <summary>
/// Bounds how much of a session's scratchpad is PUSHED into its spawn prompt.
///
/// The scratchpad started as crash-recovery state and became a diary: on 2026-09-09 the
/// fleet's files measured 80-500 KB each, and the largest outweighed the task prompt
/// about 50:1 with months of state claims that were no longer true. An agent reads its
/// own old notes with the same authority as the operator's instruction, so an unbounded
/// injection is a direct route to "not taking direction".
///
/// Only the push is bounded. The file on disk is untouched, `find` still searches all of
/// it, and the preface tells the agent where the rest lives so it can pull on demand.
/// </summary>
public static class ScratchpadInjection
{
    public sealed record Selection(string Text, bool Truncated, int TotalChars);

    /// <summary>The tail of <paramref name="content"/>, at most <paramref name="maxChars"/>
    /// long, snapped FORWARD to the next "## " heading so it never opens mid-section
    /// (or, with no heading in range, to the next line boundary). Forward only: snapping
    /// back to an earlier heading would overshoot the cap. 0 = whole file.</summary>
    public static Selection Select(string content, int maxChars)
    {
        if (maxChars <= 0 || content.Length <= maxChars)
            return new Selection(content, false, content.Length);

        var cut = content.Length - maxChars;
        // A heading starting exactly at `cut` has its newline at cut-1.
        var heading = content.IndexOf("\n## ", Math.Max(0, cut - 1), StringComparison.Ordinal);
        int start;
        if (heading >= 0) start = heading + 1;
        else
        {
            var nl = content.IndexOf('\n', cut);
            start = nl >= 0 ? nl + 1 : cut;
        }
        return new Selection(content[start..], true, content.Length);
    }

    public static string Preface(Selection s, string path) => s.Truncated
        ? $"Previous scratchpad content (last {s.Text.Length} of {s.TotalChars} chars; the file is older than what you see here, and every claim in it is a note not a fact. Read {path} only if you need earlier history):"
        : "Previous scratchpad content (from prior session):";
}

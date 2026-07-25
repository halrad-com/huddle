using System.Text.Json;

namespace Huddle;

/// <summary>
/// Reads a live session's Claude Code transcript to answer "is this agent in
/// trouble right now?" for the status view. Huddle can't see a session's console,
/// but Claude Code records an API failure (500 / 529 / rate-limit / overload) as a
/// synthetic assistant entry carrying the top-level field
/// <c>"isApiErrorMessage": true</c> and a human-readable reason. We tail the
/// transcript (cheap — last 64 KB) and report when the most recent assistant
/// activity is such an error, i.e. the agent erred and hasn't recovered yet.
///
/// Idle is a separate, softer signal: how long since the transcript last grew. It
/// can't tell "stuck" from "waiting at the prompt", so it's reported as plain
/// information, never as an alarm.
/// </summary>
public static class SessionTrouble
{
    private const int TailBytes = 64 * 1024;

    /// <summary>
    /// Path to a session's transcript (~/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl),
    /// or null if it doesn't exist. Claude Code encodes the cwd by replacing ':',
    /// '\' and '/' with '-' (e.g. C:\a\b -> C--a-b).
    /// </summary>
    public static string? TranscriptPath(string projectsRoot, string? cwd, Guid sessionId)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        var encoded = cwd.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
        var path = Path.Combine(projectsRoot, encoded, sessionId + ".jsonl");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Last-write time of the transcript, or null if unavailable.</summary>
    public static DateTime? LastActivity(string transcriptPath)
    {
        try { return File.GetLastWriteTime(transcriptPath); } catch { return null; }
    }

    /// <summary>
    /// A concise API-error reason if the session's most recent assistant activity is
    /// an API error, else null. Reads only the tail of the file. Never throws.
    /// </summary>
    public static string? ApiErrorReason(string transcriptPath)
    {
        try { return ApiErrorReasonFromText(ReadTail(transcriptPath, TailBytes)); }
        catch { return null; }
    }

    /// <summary>
    /// Pure form: scan transcript text and return a concise reason when the LAST
    /// assistant-type entry is an API error (top-level isApiErrorMessage == true), or
    /// null when the latest assistant entry is a normal turn (recovered) or none is
    /// present. Matching the real JSON field — not a substring — so the phrase
    /// appearing inside tool output or mail bodies can't false-positive. Unit-tested.
    /// </summary>
    public static string? ApiErrorReasonFromText(string transcriptText)
    {
        if (string.IsNullOrEmpty(transcriptText)) return null;
        string? reason = null;
        foreach (var raw in transcriptText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] != '{') continue; // skip a partial leading line
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }                              // partial / malformed line
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!(root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                      && t.GetString() == "assistant"))
                    continue;

                // Each assistant entry supersedes the previous verdict: an API error
                // sets the reason; a normal turn clears it (the agent recovered).
                reason = root.TryGetProperty("isApiErrorMessage", out var e) && e.ValueKind == JsonValueKind.True
                    ? ExtractReason(root)
                    : null;
            }
        }
        return reason;
    }

    // Prefer the concise tail after "·" ("Rate limited", "Overloaded"); otherwise the
    // text with an "API Error:" prefix stripped, truncated. Falls back to a generic label.
    private static string ExtractReason(JsonElement root)
    {
        var text = FirstText(root);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Fall back to the numeric status if the text block is empty.
            if (root.TryGetProperty("apiErrorStatus", out var s) && s.ValueKind == JsonValueKind.Number)
                return $"API error {s.GetInt32()}";
            return "API error";
        }

        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        var dot = text.LastIndexOf('·');
        var reason = dot >= 0 && dot + 1 < text.Length ? text[(dot + 1)..].Trim() : text;
        const string prefix = "API Error:";
        if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            reason = reason[prefix.Length..].Trim();
        return reason.Length <= 48 ? reason : reason[..47] + "…";
    }

    private static string? FirstText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String
                && it.GetString() == "text"
                && item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                return tx.GetString();
        }
        return null;
    }

    // Read the last maxBytes of a file as text (UTF-8), tolerating a partial first
    // line (the scanner skips it). FileShare.ReadWrite: the session holds it open.
    private static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = fs.Length;
        var start = len > maxBytes ? len - maxBytes : 0;
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}

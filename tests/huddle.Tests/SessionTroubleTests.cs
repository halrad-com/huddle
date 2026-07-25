using Huddle;
namespace Huddle.Tests;

/// <summary>
/// Detecting agent trouble from a transcript tail: an API error is only reported
/// when it is the most recent assistant activity (not after recovery), it is keyed
/// on the real JSON field (not a substring in tool output/mail), and the reason is
/// reduced to a concise label.
/// </summary>
public class SessionTroubleTests
{
    private static string Assistant(string text, bool apiError) =>
        apiError
            ? $"{{\"type\":\"assistant\",\"isApiErrorMessage\":true,\"apiErrorStatus\":529,\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":{Quote(text)}}}]}}}}"
            : $"{{\"type\":\"assistant\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":{Quote(text)}}}]}}}}";

    private static string User(string text) =>
        $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":{Quote(text)}}}]}}}}";

    private static string Quote(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void An_unrecovered_api_error_is_reported_with_a_concise_reason()
    {
        var t = string.Join("\n",
            User("do the thing"),
            Assistant("API Error: Server is temporarily limiting requests (not your usage limit) · Rate limited", apiError: true));

        Assert.Equal("Rate limited", SessionTrouble.ApiErrorReasonFromText(t));
    }

    [Fact]
    public void A_normal_assistant_turn_after_an_error_means_recovered()
    {
        var t = string.Join("\n",
            Assistant("API Error: Overloaded · Overloaded", apiError: true),
            User("tool result"),
            Assistant("Back to work, here is the plan.", apiError: false));

        Assert.Null(SessionTrouble.ApiErrorReasonFromText(t));
    }

    [Fact]
    public void The_phrase_in_tool_output_is_not_a_false_positive()
    {
        // A normal assistant turn whose text merely mentions the field name must not
        // be read as an error — we key on the real top-level field, not a substring.
        var t = string.Join("\n",
            User("grep found isApiErrorMessage in a file"),
            Assistant("The string \\\"isApiErrorMessage\\\":true appears in the log.", apiError: false));

        Assert.Null(SessionTrouble.ApiErrorReasonFromText(t));
    }

    [Fact]
    public void No_assistant_entries_means_no_trouble()
    {
        Assert.Null(SessionTrouble.ApiErrorReasonFromText(User("just a prompt")));
        Assert.Null(SessionTrouble.ApiErrorReasonFromText(""));
    }

    [Fact]
    public void A_partial_leading_line_is_skipped_not_fatal()
    {
        // The tail read can start mid-line; a broken first line must not throw.
        var t = "e\":\"assistant\",\"message\": broken partial line\n" +
                Assistant("API Error: rate · Rate limited", apiError: true);

        Assert.Equal("Rate limited", SessionTrouble.ApiErrorReasonFromText(t));
    }

    [Fact]
    public void Reason_without_a_dot_strips_the_api_error_prefix()
    {
        var t = Assistant("API Error: Connection reset by peer", apiError: true);
        Assert.Equal("Connection reset by peer", SessionTrouble.ApiErrorReasonFromText(t));
    }

    [Fact]
    public void Transcript_path_encodes_the_cwd_like_claude_code()
    {
        var root = Path.Combine(Path.GetTempPath(), "huddle-tp-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var encoded = "C--Users-you-source-repos-myapp";
        var dir = Path.Combine(root, encoded);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file, "{}");
        try
        {
            var found = SessionTrouble.TranscriptPath(root, "C:\\Users\\you\\source\\repos\\myapp", id);
            Assert.Equal(file, found);
            // A cwd with no transcript yields null, not a throw.
            Assert.Null(SessionTrouble.TranscriptPath(root, "C:\\nowhere", id));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}

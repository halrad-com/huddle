using System.Text.Json;
using Huddle;
using Xunit;

namespace HuddleTests;

// Handoff tracing (2026-08-10): a `handoff` mail is recorded to a durable ledger and
// announced live, so the operator can trace who handed what to whom without asking.
public class HandoffTests
{
    private static string TempLedger() =>
        Path.Combine(Path.GetTempPath(), "huddle-handoff-" + Guid.NewGuid().ToString("N") + ".jsonl");

    [Fact]
    public void Ledger_RecordThenRead_Roundtrips()
    {
        var path = TempLedger();
        try
        {
            var ledger = new HandoffLedger(path);
            var t = new DateTime(2026, 8, 10, 3, 15, 0, DateTimeKind.Local);
            Assert.True(ledger.Record(new HandoffEntry(t, "app:architect", "app:reviewer", "review the overlay", "phase 1 done", "mail-1.json")));
            Assert.True(ledger.Record(new HandoffEntry(t, "a:x", "b:y", "task two", null, "mail-2.json")));

            var all = ledger.ReadAll();
            Assert.Equal(2, all.Count);
            Assert.Equal("app:architect", all[0].From);
            Assert.Equal("app:reviewer", all[0].To);
            Assert.Equal("review the overlay", all[0].Task);
            Assert.Equal("phase 1 done", all[0].State);
            Assert.Null(all[1].State);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Ledger_Record_IsIdempotentBySource()
    {
        var path = TempLedger();
        try
        {
            var ledger = new HandoffLedger(path);
            var e = new HandoffEntry(DateTime.Now, "a", "b", "t", null, "same.json");
            Assert.True(ledger.Record(e));    // first time: written
            Assert.False(ledger.Record(e));   // same source mail: skipped, not double-recorded
            Assert.Single(ledger.ReadAll());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ParseBody_ReadsFields()
    {
        using var doc = JsonDocument.Parse("""{"to":"app:reviewer","task":"verify X","state":"3 of 5"}""");
        var (to, task, state) = HandoffLedger.ParseBody(doc.RootElement, "fallback:to", "fallback subject");
        Assert.Equal("app:reviewer", to);
        Assert.Equal("verify X", task);
        Assert.Equal("3 of 5", state);
    }

    [Fact]
    public void ParseBody_MissingFields_UseFallbacks()
    {
        using var doc = JsonDocument.Parse("{}");
        var (to, task, state) = HandoffLedger.ParseBody(doc.RootElement, "mail:to", "mail subject");
        Assert.Equal("mail:to", to);       // falls back to the mail's own `to`
        Assert.Equal("mail subject", task); // falls back to the subject
        Assert.Null(state);
    }
}

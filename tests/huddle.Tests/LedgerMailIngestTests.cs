using System.Text.Json;
using Huddle;

namespace Huddle.Tests;

/// <summary>
/// Spec §5.4: any mail with "type":"task" opens a tracked row in the recipient's repo
/// ledger, whoever sent it, without anyone running a command. This is the change that
/// catches peer-to-peer dispatch — the audit's four dropped assignments were all
/// type:"task", and every one would have shown up in `ledger open --by-age` the day it
/// was sent.
/// </summary>
public class LedgerMailIngestTests : IDisposable
{
    readonly string _dir;
    readonly List<string> _log = new();

    public LedgerMailIngestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-mailingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string LedgerDir => Path.Combine(_dir, "docs", "ledger");
    LedgerWriter Writer() => new(LedgerDir, _log.Add, createIfAbsent: true);
    static DateTimeOffset Now => new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);
    static readonly LedgerId T1 = new(LedgerType.Task, 1, null);

    static IpcMessage Mail(string type, string from = "myapp:architect",
                           string subject = "WMA transcode server half", string body = "{}") =>
        JsonSerializer.Deserialize<IpcMessage>(
            $"{{\"from\":\"{from}\",\"to\":\"myapp:backenddev\",\"timestamp\":\"2026-08-23T11:00:00Z\"," +
            $"\"type\":\"{type}\",\"subject\":{JsonSerializer.Serialize(subject)},\"body\":{body}}}")!;

    const string Recipient = "myapp:backenddev";
    const string RelPath = "ipc/myapp_backenddev/inbox/011-from-myapp_architect.json";

    LedgerEvent? Assigned(IpcMessage m) => LedgerMailIngest.Assigned(m, RelPath, Recipient, T1, Now);

    // ---- what does and does not open a row ----

    [Theory]
    [InlineData("info")]
    [InlineData("request")]
    [InlineData("status")]
    [InlineData("command")]
    [InlineData("handoff")]
    public void Mail_that_is_not_a_task_opens_nothing(string type) =>
        Assert.Null(Assigned(Mail(type)));

    [Fact]
    public void Task_mail_opens_a_row_owned_by_the_recipient_and_attributed_to_the_sender()
    {
        var e = Assigned(Mail("task"))!;
        Assert.Equal("task-assigned", e.Event);
        Assert.Equal("T-001", e.Id);
        Assert.Equal(Recipient, e.Owner);                       // who owes the work
        Assert.Equal("myapp:architect", e.Actor);          // who asked for it
        Assert.Equal("WMA transcode server half", e.Title);
        Assert.Equal(new[] { RelPath }, e.Refs);                // the mail is the evidence
        Assert.Equal(Now, e.Ts);
    }

    [Fact]
    public void Type_is_matched_case_insensitively_because_agents_hand_write_this_json() =>
        Assert.NotNull(Assigned(Mail("TASK")));

    // ---- parent ----

    [Fact]
    public void A_qualified_ledger_field_is_kept_as_written()
    {
        var e = Assigned(Mail("task", body: "{\"ledger\":\"otherapp:D-014\"}"))!;
        Assert.Equal("otherapp:D-014", e.Parent);
    }

    [Fact]
    public void A_bare_ledger_field_is_qualified_with_the_recipients_repo()
    {
        // The event is read out of context — in `ledger`, in a diff, in another repo's
        // view. A bare D-014 there names nothing in particular.
        var e = Assigned(Mail("task", body: "{\"ledger\":\"D-14\"}"))!;
        Assert.Equal("myapp:D-014", e.Parent);
    }

    [Fact]
    public void A_ledger_field_that_does_not_parse_leaves_an_orphan_rather_than_failing()
    {
        // §6.3: an orphan is the SIGNAL — work being done that nobody ideated — not an
        // error. Refusing the mail here would drop the obligation the row exists to keep.
        var e = Assigned(Mail("task", body: "{\"ledger\":\"not-an-id\"}"))!;
        Assert.Null(e.Parent);
    }

    [Fact]
    public void A_ledger_field_naming_a_task_is_not_a_parent()
    {
        // Tasks are leaves; a task parented to a task would nest obligations forever.
        Assert.Null(Assigned(Mail("task", body: "{\"ledger\":\"T-004\"}"))!.Parent);
    }

    [Fact]
    public void No_ledger_field_at_all_is_an_orphan_and_that_is_fine() =>
        Assert.Null(Assigned(Mail("task"))!.Parent);

    [Fact]
    public void A_body_that_is_a_bare_string_does_not_throw()
    {
        var e = Assigned(Mail("task", body: "\"just some prose\""))!;
        Assert.Null(e.Parent);
        Assert.Null(e.Pri);
    }

    // ---- priority ----

    [Theory]
    [InlineData("P0")]
    [InlineData("P1")]
    [InlineData("P2")]
    [InlineData("P3")]
    public void A_valid_priority_is_carried(string pri) =>
        Assert.Equal(pri, Assigned(Mail("task", body: $"{{\"pri\":\"{pri}\"}}"))!.Pri);

    [Theory]
    [InlineData("P9")]
    [InlineData("high")]
    [InlineData("")]
    public void An_invalid_priority_is_dropped_not_recorded(string pri) =>
        Assert.Null(Assigned(Mail("task", body: $"{{\"pri\":\"{pri}\"}}"))!.Pri);

    [Fact]
    public void Priority_is_normalised_to_upper_case() =>
        Assert.Equal("P0", Assigned(Mail("task", body: "{\"pri\":\"p0\"}"))!.Pri);

    // ---- title ----

    [Fact]
    public void A_long_subject_is_truncated_to_a_hundred_characters()
    {
        var e = Assigned(Mail("task", subject: new string('x', 250)))!;
        Assert.Equal(100, e.Title!.Length);
    }

    [Fact]
    public void A_subject_spanning_lines_becomes_one_line()
    {
        var e = Assigned(Mail("task", subject: "first\nsecond\r\nthird"))!;
        Assert.DoesNotContain('\n', e.Title!);
        Assert.DoesNotContain('\r', e.Title!);
    }

    [Fact]
    public void Task_mail_with_no_subject_still_opens_a_row_with_a_usable_title()
    {
        var e = Assigned(Mail("task", subject: ""))!;
        Assert.False(string.IsNullOrWhiteSpace(e.Title));
    }

    // ---- acknowledgement ----

    [Fact]
    public void Acked_records_who_acknowledged_and_when()
    {
        var e = LedgerMailIngest.Acked(T1, Recipient, Now);
        Assert.Equal("task-acked", e.Event);
        Assert.Equal("T-001", e.Id);
        Assert.Equal(Recipient, e.Actor);
        Assert.Equal(Now, e.Ts);
    }

    // ---- the dedup key, end to end through the writer ----

    [Fact]
    public void The_same_mail_file_never_opens_a_second_row()
    {
        var w = Writer();
        var m = Mail("task");

        // Three passes: the FSW delivery, a retry tick, and a huddle restart's rescan.
        for (int pass = 0; pass < 3; pass++)
            if (!w.TryFindTaskByRef(RelPath, out _))
                w.AppendNewTask(id => LedgerMailIngest.Assigned(m, RelPath, Recipient, id, Now)!);

        var problems = new List<string>();
        var tasks = TaskMaterializer.Materialize(w.ReadAll(problems), problems);
        Assert.Single(tasks);
        Assert.Empty(problems);          // "assigned twice" would show up here
    }

    [Fact]
    public void Two_different_mails_open_two_rows()
    {
        var w = Writer();
        foreach (var rel in new[] { RelPath, RelPath.Replace("011", "012") })
            if (!w.TryFindTaskByRef(rel, out _))
                w.AppendNewTask(id => LedgerMailIngest.Assigned(Mail("task"), rel, Recipient, id, Now)!);

        var problems = new List<string>();
        var tasks = TaskMaterializer.Materialize(w.ReadAll(problems), problems);
        Assert.Equal(2, tasks.Count);
        Assert.Equal(new[] { "T-001", "T-002" }, tasks.Select(t => t.Id.ToString()));
    }

    [Fact]
    public void Acknowledgement_is_appended_once_however_many_times_the_move_is_noticed()
    {
        var w = Writer();
        var m = Mail("task");
        w.AppendNewTask(id => LedgerMailIngest.Assigned(m, RelPath, Recipient, id, Now)!);

        // reap and forget can both name the same file on consecutive ticks
        for (int tick = 0; tick < 3; tick++) LedgerMailIngest.AckIfOpen(w, RelPath, Recipient, Now, _log.Add);

        var problems = new List<string>();
        var events = w.ReadAll(problems);
        Assert.Equal(1, events.Count(e => e.Event == "task-acked"));
        var task = Assert.Single(TaskMaterializer.Materialize(events, problems));
        Assert.Equal("acked", task.State);
        Assert.Empty(problems);          // an illegal acked->acked would show up here
    }

    [Fact]
    public void Acknowledging_mail_that_opened_no_task_is_a_silent_no_op()
    {
        var w = Writer();
        LedgerMailIngest.AckIfOpen(w, "ipc/x/inbox/never-a-task.json", Recipient, Now, _log.Add);
        var problems = new List<string>();
        Assert.Empty(w.ReadAll(problems));
    }

    [Fact]
    public void A_task_already_moved_past_acked_is_not_dragged_backwards()
    {
        var w = Writer();
        var m = Mail("task");
        w.AppendNewTask(id => LedgerMailIngest.Assigned(m, RelPath, Recipient, id, Now)!);
        w.Append(new LedgerEvent(Now.AddMinutes(1), "task-acked", "T-001", Actor: Recipient));
        w.Append(new LedgerEvent(Now.AddMinutes(2), "task-progress", "T-001", Actor: Recipient));

        LedgerMailIngest.AckIfOpen(w, RelPath, Recipient, Now.AddMinutes(3), _log.Add);

        var problems = new List<string>();
        var task = Assert.Single(TaskMaterializer.Materialize(w.ReadAll(problems), problems));
        Assert.Equal("in-progress", task.State);
        Assert.Empty(problems);
    }

    // ---- the dedup key itself ----

    // Delivery opens the row from the inbox file's full path; acknowledgement finds it
    // again from a bare file name reassembled against the inbox directory. If those two
    // ever produced different text the row would open and then never be acknowledged —
    // and nothing would throw, it would just sit at "assigned" forever.
    [Fact]
    public void The_key_delivery_writes_is_the_key_acknowledgement_looks_up()
    {
        var root = Path.Combine(_dir, "huddleroot");
        var ipc = Path.Combine(root, "ipc");
        var name = "011-from-myapp_architect-20260823T1100.json";
        var full = Path.Combine(ipc, "myapp_backenddev", "inbox", name);

        Assert.Equal(
            LedgerMailIngest.MailRef(root, full),
            LedgerMailIngest.MailRef(root, ipc, "myapp_backenddev", name));
    }

    [Fact]
    public void The_key_is_relative_with_forward_slashes_so_it_reads_in_a_nudge()
    {
        var root = Path.Combine(_dir, "huddleroot");
        var key = LedgerMailIngest.MailRef(root, Path.Combine(root, "ipc"), "myapp_backenddev", "011.json");
        Assert.Equal("ipc/myapp_backenddev/inbox/011.json", key);
        Assert.DoesNotContain('\\', key);
        Assert.False(Path.IsPathRooted(key));
    }

    // ---- the nudge line (spec §5.8) ----

    [Fact]
    public void A_task_nudge_says_TASK_and_names_the_id()
    {
        var line = LedgerMailIngest.NudgeLine(Mail("task"), RelPath, T1);
        // A task must not read like an FYI in the one line an agent often sees before
        // deciding whether to interrupt itself.
        Assert.Contains("TASK", line);
        Assert.Contains("T-001", line);
        Assert.Contains("myapp:architect", line);
        Assert.Contains(RelPath, line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Ordinary_mail_keeps_the_nudge_line_it_always_had()
    {
        var line = LedgerMailIngest.NudgeLine(Mail("info", subject: "settings landed"), RelPath, null);
        Assert.StartsWith("[huddle mail from myapp:architect]", line);
        Assert.Contains("settings landed", line);
        Assert.Contains(RelPath, line);
        Assert.DoesNotContain("TASK", line);
    }

    [Fact]
    public void A_task_whose_row_could_not_be_opened_still_gets_announced()
    {
        // A repo with an unwritable ledger must not swallow the mail as well.
        var line = LedgerMailIngest.NudgeLine(Mail("task"), RelPath, null);
        Assert.Contains(RelPath, line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void A_subject_with_newlines_cannot_forge_a_second_nudge_line()
    {
        var line = LedgerMailIngest.NudgeLine(Mail("task", subject: "a\nb"), RelPath, T1);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }
}

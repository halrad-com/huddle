using Huddle;

namespace Huddle.Tests;

public class LedgerEventReplayTests
{
    static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    const string Assigned = """{"ts":"2026-08-21T21:30:00Z","event":"task-assigned","id":"T-107","actor":"myapp:architect","owner":"myapp:backenddev","parent":"myapp:D-014","pri":"P0","title":"WMA server half","refs":["ipc/x.json"]}""";
    const string Acked = """{"ts":"2026-08-21T22:00:00Z","event":"task-acked","id":"T-107","actor":"myapp:backenddev"}""";
    const string Declined = """{"ts":"2026-08-22T00:40:00Z","event":"task-declined","id":"T-107","actor":"myapp:backenddev","note":"architect took it"}""";

    [Fact]
    public void Reads_rotated_files_in_name_order_and_materializes()
    {
        var d = Dir();
        File.WriteAllText(Path.Combine(d, "events-20260820.jsonl"), Assigned + "\n");
        File.WriteAllText(Path.Combine(d, "events.jsonl"), Acked + "\n\n" + Declined + "\n");
        var problems = new List<string>();
        var ev = LedgerEventReader.ReadAll(d, problems);
        Assert.Empty(problems);
        Assert.Equal(3, ev.Count);
        var tasks = TaskMaterializer.Materialize(ev, problems);
        var t = Assert.Single(tasks);
        Assert.Equal("declined", t.State);
        Assert.Equal("myapp:backenddev", t.Owner);
        Assert.Equal("myapp:D-014", t.Parent!.Value.ToString());
        Assert.Equal("architect took it", t.LastNote);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 21, 30, 0, TimeSpan.Zero), t.AssignedAt);
        Directory.Delete(d, true);
    }

    [Fact]
    public void Out_of_order_lines_replay_by_timestamp()
    {
        var problems = new List<string>();
        var ev = LedgerEventReader.ParseLines(new[] { Declined, Assigned, Acked }, "mem", problems);
        var t = Assert.Single(TaskMaterializer.Materialize(ev, problems));
        Assert.Equal("declined", t.State);
        Assert.Empty(problems);
    }

    [Fact]
    public void Bad_line_is_a_problem_not_a_crash()
    {
        var problems = new List<string>();
        var ev = LedgerEventReader.ParseLines(new[] { "{not json", Assigned }, "f.jsonl", problems);
        Assert.Single(ev);
        Assert.Contains("f.jsonl:1", Assert.Single(problems));
    }

    [Fact]
    public void Illegal_transition_is_a_problem_and_ignored()
    {
        var problems = new List<string>();
        var bad = """{"ts":"2026-08-21T21:31:00Z","event":"task-accepted","id":"T-107","actor":"x"}""";
        var ev = LedgerEventReader.ParseLines(new[] { Assigned, bad }, "f", problems);
        var t = Assert.Single(TaskMaterializer.Materialize(ev, problems));
        Assert.Equal("assigned", t.State);
        Assert.Contains("assigned -> accepted", Assert.Single(problems));
    }

    [Fact]
    public void Event_without_assignment_is_a_problem()
    {
        var problems = new List<string>();
        var ev = LedgerEventReader.ParseLines(new[] { Acked }, "f", problems);
        Assert.Empty(TaskMaterializer.Materialize(ev, problems));
        Assert.Contains("T-107", Assert.Single(problems));
    }

    [Fact]
    public void Orphan_task_has_null_parent()
    {
        var problems = new List<string>();
        var orphan = """{"ts":"2026-08-21T21:30:00Z","event":"task-assigned","id":"T-001","owner":"a:b","title":"x"}""";
        var t = Assert.Single(TaskMaterializer.Materialize(LedgerEventReader.ParseLines(new[] { orphan }, "f", problems), problems));
        Assert.Null(t.Parent);
    }

    // L3: tasks are keyed on the PARSED, bare-normalised LedgerId — never the raw
    // string. `T-7`, `T-007` and `huddle:T-007` are one task, not three.
    [Fact]
    public void L3_tasks_are_keyed_on_the_parsed_id_not_the_raw_string()
    {
        var problems = new List<string>();
        var a = """{"ts":"2026-08-21T21:30:00Z","event":"task-assigned","id":"T-7","owner":"a:b","title":"x"}""";
        var b = """{"ts":"2026-08-21T22:00:00Z","event":"task-acked","id":"T-007","actor":"a:b"}""";
        var c = """{"ts":"2026-08-21T23:00:00Z","event":"task-progress","id":"huddle:T-007","actor":"a:b"}""";
        var ev = LedgerEventReader.ParseLines(new[] { a, b, c }, "f", problems);
        var t = Assert.Single(TaskMaterializer.Materialize(ev, problems));
        Assert.Equal("in-progress", t.State);
        Assert.Equal("T-007", t.Id.ToString());
        Assert.Empty(problems);
    }

    // L3: a second task-assigned for the same id in a different spelling is still
    // "assigned twice", not a silently separate task.
    [Fact]
    public void L3_reassignment_in_another_spelling_is_still_assigned_twice()
    {
        var problems = new List<string>();
        var a = """{"ts":"2026-08-21T21:30:00Z","event":"task-assigned","id":"T-7","owner":"a:b","title":"x"}""";
        var b = """{"ts":"2026-08-21T21:40:00Z","event":"task-assigned","id":"T-007","owner":"c:d","title":"y"}""";
        var ev = LedgerEventReader.ParseLines(new[] { a, b }, "f", problems);
        var t = Assert.Single(TaskMaterializer.Materialize(ev, problems));
        Assert.Equal("x", t.Title);
        Assert.Contains("assigned twice", Assert.Single(problems));
    }

    [Fact]
    public void Missing_dir_is_empty_not_error()
    {
        var problems = new List<string>();
        Assert.Empty(LedgerEventReader.ReadAll(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()), problems));
        Assert.Empty(problems);
    }
}

using Huddle;

namespace Huddle.Tests;

/// <summary>
/// The single append path. Every write to events.jsonl goes through here, so these pin
/// the three things that are silent when wrong: id allocation (a reused id merges two
/// obligations), the dedup key (a duplicated key opens a second task per rescan), and
/// canonical id text (L3 — `T-7` and `T-007` must never be two tasks).
/// </summary>
public class LedgerWriterTests : IDisposable
{
    readonly string _dir;
    readonly List<string> _log = new();

    public LedgerWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-ledgerwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string LedgerDir => Path.Combine(_dir, "docs", "ledger");
    LedgerWriter New(bool createIfAbsent = true) => new(LedgerDir, _log.Add, createIfAbsent);
    static DateTimeOffset At(int min) => new(2026, 8, 23, 10, min, 0, TimeSpan.Zero);

    IReadOnlyList<LedgerEvent> Read()
    {
        var problems = new List<string>();
        var all = LedgerEventReader.ReadAll(LedgerDir, problems);
        Assert.Empty(problems);
        return all;
    }

    // ---- round trip ----

    [Fact]
    public void Append_round_trips_every_field()
    {
        var w = New();
        var ev = new LedgerEvent(At(0), "task-assigned", "T-001",
            Actor: "huddle:architect", Owner: "huddle:backenddev", Parent: "huddle:D-014", Pri: "P0",
            Title: "WMA transcode server half",
            Refs: new[] { "ipc/huddle_backenddev/inbox/011.json", "unit:B-1#x" },
            From: "dispatched", To: "delivered", Note: "architect took the work", Ungated: true);
        w.Append(ev);

        var back = Assert.Single(Read());
        Assert.Equal(ev.Ts, back.Ts);
        Assert.Equal("task-assigned", back.Event);
        Assert.Equal("T-001", back.Id);
        Assert.Equal("huddle:architect", back.Actor);
        Assert.Equal("huddle:backenddev", back.Owner);
        Assert.Equal("huddle:D-014", back.Parent);
        Assert.Equal("P0", back.Pri);
        Assert.Equal("WMA transcode server half", back.Title);
        Assert.Equal(new[] { "ipc/huddle_backenddev/inbox/011.json", "unit:B-1#x" }, back.Refs);
        Assert.Equal("dispatched", back.From);
        Assert.Equal("delivered", back.To);
        Assert.Equal("architect took the work", back.Note);
        Assert.True(back.Ungated);
    }

    [Fact]
    public void One_append_is_exactly_one_line()
    {
        var w = New();
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001", Title: "a\nb\r\nc"));
        w.Append(new LedgerEvent(At(1), "task-acked", "T-001"));
        var lines = File.ReadAllLines(Path.Combine(LedgerDir, "events.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(2, lines.Count);
        // the newline in the title survives as an escape, not as a second record
        Assert.Contains("a\nb", Read()[0].Title);
    }

    [Fact]
    public void Timestamps_are_written_as_utc_with_a_z()
    {
        var w = New();
        w.Append(new LedgerEvent(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.FromHours(2)), "task-assigned", "T-001"));
        var line = File.ReadAllLines(Path.Combine(LedgerDir, "events.jsonl"))[0];
        Assert.Contains("\"ts\":\"2026-08-23T10:00:00", line);
        Assert.Contains("Z\"", line);
        Assert.Equal(TimeSpan.Zero, Read()[0].Ts.Offset);
    }

    [Fact]
    public void Absent_optional_fields_are_not_written()
    {
        var w = New();
        w.Append(new LedgerEvent(At(0), "task-acked", "T-001"));
        var line = File.ReadAllLines(Path.Combine(LedgerDir, "events.jsonl"))[0];
        Assert.DoesNotContain("null", line);
        Assert.DoesNotContain("ungated", line);
    }

    // ---- id allocation ----

    [Fact]
    public void NextTaskId_starts_at_T_001_on_an_empty_ledger() =>
        Assert.Equal("T-001", New().NextTaskId().ToString());

    [Fact]
    public void NextTaskId_is_written_canonically_not_as_T_7()
    {
        var w = New();
        for (int i = 0; i < 7; i++) w.Append(new LedgerEvent(At(i), "task-assigned", w.NextTaskId().ToString()));
        Assert.Equal("T-007", Read()[6].Id);
        Assert.Equal("T-008", w.NextTaskId().ToString());
    }

    [Fact]
    public void NextTaskId_never_reuses_an_id_after_a_new_writer_over_the_same_dir()
    {
        var a = New();
        a.Append(new LedgerEvent(At(0), "task-assigned", a.NextTaskId().ToString()));
        a.Append(new LedgerEvent(At(1), "task-assigned", a.NextTaskId().ToString()));
        // A restart is a NEW writer over the same directory. `T001` was issued 23 times
        // because the old TaskTracker's counter lived in memory.
        Assert.Equal("T-003", New().NextTaskId().ToString());
    }

    // L3: ids are compared PARSED. An event someone hand-wrote as `T-7`, or one carrying
    // a repo qualifier, occupies its number just as surely as the canonical spelling.
    //
    // The allocation must be read back FROM DISK for this to mean anything: the writer
    // also tracks what it has issued in memory, and that counter masks a broken disk
    // read for as long as the process lives. A fresh writer is the only way to test the
    // path a restart actually takes — which is the path that matters, because the ids
    // this protects were written by an EARLIER run.
    [Fact]
    public void NextTaskId_reads_sloppy_and_qualified_ids_from_disk_by_value()
    {
        var seed = New();
        seed.Append(new LedgerEvent(At(0), "task-assigned", "T-7"));
        seed.Append(new LedgerEvent(At(1), "task-assigned", "huddle:T-012"));

        Assert.Equal("T-013", New().NextTaskId().ToString());
    }

    [Fact]
    public void A_repo_qualified_id_on_disk_is_not_invisible_to_allocation()
    {
        Directory.CreateDirectory(LedgerDir);
        File.WriteAllText(Path.Combine(LedgerDir, "events.jsonl"),
            "{\"ts\":\"2026-08-23T10:00:00Z\",\"event\":\"task-assigned\",\"id\":\"huddle:T-009\"}\n");
        // Reusing T-009 here would merge a new obligation into an existing one.
        Assert.Equal("T-010", New().NextTaskId().ToString());
    }

    [Fact]
    public void NextTaskId_ignores_hierarchy_ids_and_malformed_lines()
    {
        Directory.CreateDirectory(LedgerDir);
        File.WriteAllLines(Path.Combine(LedgerDir, "events.jsonl"), new[]
        {
            "{\"ts\":\"2026-08-23T10:00:00Z\",\"event\":\"state\",\"id\":\"F-099\"}",
            "not json at all",
            "{\"ts\":\"2026-08-23T10:01:00Z\",\"event\":\"task-assigned\",\"id\":\"T-004\"}",
            "",
        });
        Assert.Equal("T-005", New().NextTaskId().ToString());
    }

    [Fact]
    public void NextTaskId_counts_ids_in_rotated_files_too()
    {
        Directory.CreateDirectory(LedgerDir);
        File.WriteAllText(Path.Combine(LedgerDir, "events-20260101.jsonl"),
            "{\"ts\":\"2026-01-01T00:00:00Z\",\"event\":\"task-assigned\",\"id\":\"T-042\"}\n");
        File.WriteAllText(Path.Combine(LedgerDir, "events.jsonl"),
            "{\"ts\":\"2026-08-23T10:00:00Z\",\"event\":\"task-assigned\",\"id\":\"T-003\"}\n");
        // Highest wins, not last — an id is never reused even after rotation.
        Assert.Equal("T-043", New().NextTaskId().ToString());
    }

    // ---- the dedup key ----

    [Fact]
    public void TryFindTaskByRef_finds_the_task_that_carries_the_ref()
    {
        var w = New();
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001", Refs: new[] { "ipc/a/inbox/1.json" }));
        w.Append(new LedgerEvent(At(1), "task-assigned", "T-002", Refs: new[] { "ipc/a/inbox/2.json", "unit:B-9#z" }));

        Assert.True(w.TryFindTaskByRef("ipc/a/inbox/2.json", out var id));
        Assert.Equal(new LedgerId(LedgerType.Task, 2, null), id);
        Assert.True(w.TryFindTaskByRef("unit:B-9#z", out var byUnit));
        Assert.Equal(new LedgerId(LedgerType.Task, 2, null), byUnit);
    }

    [Fact]
    public void TryFindTaskByRef_is_exact_not_a_prefix_or_substring_match()
    {
        var w = New();
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001", Refs: new[] { "ipc/a/inbox/11.json" }));
        Assert.False(w.TryFindTaskByRef("ipc/a/inbox/1.json", out _));
        Assert.False(w.TryFindTaskByRef("ipc/a/inbox", out _));
        Assert.False(w.TryFindTaskByRef("", out _));
    }

    [Fact]
    public void TryFindTaskByRef_returns_a_parsed_id_even_when_the_event_spelled_it_loosely()
    {
        var w = New();
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-7", Refs: new[] { "ipc/a/inbox/1.json" }));
        Assert.True(w.TryFindTaskByRef("ipc/a/inbox/1.json", out var id));
        Assert.Equal(new LedgerId(LedgerType.Task, 7, null), id);
        Assert.Equal("T-007", id.ToString());
    }

    [Fact]
    public void TryFindTaskByRef_sees_a_ref_added_after_the_fact()
    {
        var w = New();
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001"));
        w.Append(new LedgerEvent(At(1), "ref-added", "T-001", Refs: new[] { "docs/plan.md" }));
        Assert.True(w.TryFindTaskByRef("docs/plan.md", out var id));
        Assert.Equal(new LedgerId(LedgerType.Task, 1, null), id);
    }

    // ---- rotation ----

    // A tiny threshold so rotation is exercised without writing 5 MB per test. The real
    // threshold is LedgerWriter.RotateAtBytes; only the trigger size is under test here.
    const long SmallRotate = 512;
    LedgerWriter Rotating() => new(LedgerDir, _log.Add, true, SmallRotate);

    [Fact]
    public void The_default_rotation_threshold_is_the_five_megabytes_the_spec_states() =>
        Assert.Equal(5 * 1024 * 1024, LedgerWriter.RotateAtBytes);

    [Fact]
    public void Rotation_at_the_threshold_preserves_every_event()
    {
        var w = Rotating();
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001", Title: new string('x', (int)SmallRotate)));
        w.Append(new LedgerEvent(At(1), "task-acked", "T-001"));

        Assert.Single(Directory.GetFiles(LedgerDir, "events-*.jsonl"));
        Assert.True(new FileInfo(Path.Combine(LedgerDir, "events.jsonl")).Length < SmallRotate);
        var all = Read();
        Assert.Equal(2, all.Count);
        Assert.Equal("task-assigned", all[0].Event);   // rotated file is read first
        Assert.Equal("task-acked", all[1].Event);
        // ids keep climbing across a rotation — the archive is still part of the log
        Assert.Equal("T-002", w.NextTaskId().ToString());
    }

    [Fact]
    public void Rotating_twice_in_one_day_does_not_clobber_the_first_archive()
    {
        var w = Rotating();
        var big = new string('x', (int)SmallRotate);
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001", Title: big));
        w.Append(new LedgerEvent(At(1), "task-assigned", "T-002", Title: big));
        w.Append(new LedgerEvent(At(2), "task-acked", "T-001"));

        Assert.Equal(2, Directory.GetFiles(LedgerDir, "events-*.jsonl").Length);
        var all = Read();
        Assert.Equal(3, all.Count);
        // ordinal file order must stay chronological, or replay applies events backwards
        Assert.Equal(new[] { "task-assigned", "task-assigned", "task-acked" }, all.Select(e => e.Event));
    }

    // ---- allocate-and-append as one act ----

    [Fact]
    public void AppendNewTask_returns_the_id_it_wrote()
    {
        var w = New();
        var id = w.AppendNewTask(i => new LedgerEvent(At(0), "task-assigned", i.ToString(), Title: "first"));
        Assert.Equal("T-001", id?.ToString());
        Assert.Equal("T-001", Assert.Single(Read()).Id);
    }

    [Fact]
    public void AppendNewTask_on_an_uncreatable_ledger_returns_null_and_writes_nothing()
    {
        var w = New(createIfAbsent: false);
        Assert.Null(w.AppendNewTask(i => new LedgerEvent(At(0), "task-assigned", i.ToString())));
        Assert.False(Directory.Exists(LedgerDir));
    }

    [Fact]
    public void Concurrent_AppendNewTask_issues_each_number_exactly_once()
    {
        var w = New();
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 25, i =>
            ids.Add(w.AppendNewTask(id => new LedgerEvent(At(1), "task-assigned", id.ToString()))!.Value.ToString()));

        Assert.Equal(25, ids.Distinct().Count());
        Assert.Equal(25, Read().Count);
        Assert.Equal(25, Read().Select(e => e.Id).Distinct().Count());
        Assert.Equal("T-026", w.NextTaskId().ToString());
    }

    // ---- the directory is the operator's ----

    [Fact]
    public void Append_does_not_create_the_ledger_dir_unasked()
    {
        var w = New(createIfAbsent: false);
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001"));
        Assert.False(Directory.Exists(LedgerDir));
        Assert.Contains(_log, l => l.Contains("no docs/ledger"));
    }

    [Fact]
    public void Append_writes_into_an_existing_ledger_dir_even_without_createIfAbsent()
    {
        Directory.CreateDirectory(LedgerDir);
        File.WriteAllText(Path.Combine(LedgerDir, "ledger.md"), "# Ledger\n");
        New(createIfAbsent: false).Append(new LedgerEvent(At(0), "task-assigned", "T-001"));
        Assert.Single(Read());
    }

    [Fact]
    public void CreateIfAbsent_creates_events_jsonl_but_never_ledger_md()
    {
        New(createIfAbsent: true).Append(new LedgerEvent(At(0), "task-assigned", "T-001"));
        Assert.True(File.Exists(Path.Combine(LedgerDir, "events.jsonl")));
        // ledger.md is the operator's hierarchy — huddle indexes it, it does not author it.
        Assert.False(File.Exists(Path.Combine(LedgerDir, "ledger.md")));
    }

    [Fact]
    public void NextTaskId_on_an_absent_dir_is_T_001_and_creates_nothing()
    {
        Assert.Equal("T-001", New(createIfAbsent: false).NextTaskId().ToString());
        Assert.False(Directory.Exists(LedgerDir));
    }

    // ---- concurrency ----

    [Fact]
    public void Concurrent_appends_neither_interleave_nor_lose_a_line()
    {
        var w = New();
        w.Append(new LedgerEvent(At(0), "task-assigned", "T-001"));  // create the dir first
        Parallel.For(0, 40, i =>
            w.Append(new LedgerEvent(At(1), "task-progress", "T-001", Note: "n" + i)));

        var all = Read();                       // Read() asserts zero parse problems
        Assert.Equal(41, all.Count);
        Assert.Equal(40, all.Count(e => e.Event == "task-progress"));
        Assert.Equal(40, all.Where(e => e.Note != null).Select(e => e.Note).Distinct().Count());
    }

    [Fact]
    public void Concurrent_id_allocation_issues_each_number_once()
    {
        var w = New();
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 25, i =>
        {
            var id = w.NextTaskId();
            w.Append(new LedgerEvent(At(1), "task-assigned", id.ToString()));
            ids.Add(id.ToString());
        });
        Assert.Equal(25, ids.Distinct().Count());
        Assert.Equal("T-026", w.NextTaskId().ToString());
    }
}

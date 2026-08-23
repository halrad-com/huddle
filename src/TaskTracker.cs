namespace Huddle;

public enum TaskState { Pending, Delegated, InProgress, Completed, Failed }

public class TrackedTask
{
    public string TaskId { get; set; } = "";
    public string Description { get; set; } = "";
    public string AssignedTo { get; set; } = "";
    public string DelegatedBy { get; set; } = "";
    public TaskState State { get; set; } = TaskState.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Spec §5.2. The same public shape it always had, but the dictionary and the counter are
/// gone: every task is now a row in the assignee repo's event log, and the getters
/// materialize by replaying it.
///
/// <para>Two consequences fall out, and they are the point of the change. Ids stop
/// resetting on restart — the in-memory counter issued <c>T001</c> 23 times to 23
/// different pieces of work — and <c>HandleTaskUpdate</c> stops nacking "unknown task"
/// for work that really happened and that the tracker had simply forgotten.</para>
///
/// <para>Ids are repo-qualified (<c>myapp:T-001</c>) because numbering is per repo:
/// a bare id echoed back by an agent would name a different task in every ledger.</para>
/// </summary>
public class TaskTracker
{
    readonly LedgerWriters _writers;
    readonly Func<IEnumerable<string>> _repos;
    readonly Action<string> _log;

    public TaskTracker(LedgerWriters writers, Func<IEnumerable<string>> repos, Action<string> log)
    { _writers = writers; _repos = repos; _log = log; }

    /// <summary>Exposed so the orchestrator and the console can append through the same
    /// per-repo writers rather than opening a second one over the same directory.</summary>
    public LedgerWriters Writers => _writers;

    /// <summary>
    /// Open a task owned by <paramref name="assignedTo"/> in that agent's repo ledger.
    ///
    /// <para>Returns null when the repo has no ledger that can be written. Handing back a
    /// task in that case would recreate the very bug this class exists to remove: an
    /// obligation that looks tracked and is gone at the next restart. The caller reports
    /// the refusal instead.</para>
    /// </summary>
    /// <param name="ledgerParent">Optional hierarchy id from the request's
    /// <c>"ledger"</c> field. Absent, the task is an orphan — which §6.3 treats as a
    /// signal to count, never an error to refuse.</param>
    public TrackedTask? Create(string description, string assignedTo, string delegatedBy, string? ledgerParent = null)
    {
        var repo = LedgerMailIngest.RepoOf(assignedTo);
        var writer = _writers.For(repo);
        if (writer is null)
        {
            _log($"ledger: cannot track a task for '{assignedTo}' — no ledger for repo '{repo}'");
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var title = Truncate(description);
        var parent = ParentFor(ledgerParent, repo);

        var id = writer.AppendNewTask(newId => new LedgerEvent(
            now, "task-assigned", newId.ToString(),
            Actor: delegatedBy, Owner: assignedTo, Parent: parent, Title: title));
        if (id is null)
        {
            _log($"ledger: could not open a task for '{assignedTo}' in repo '{repo}'");
            return null;
        }

        return new TrackedTask
        {
            TaskId = id.Value.Qualify(repo).ToString(),
            Description = title,
            AssignedTo = assignedTo,
            DelegatedBy = delegatedBy,
            State = TaskState.Delegated,
            CreatedAt = now.LocalDateTime,
        };
    }

    /// <summary>
    /// Append the event matching <paramref name="newState"/>. False — appending nothing —
    /// when the id names no task, when it is bare and ambiguous across repos, or when the
    /// transition is illegal. The log is forward-only, so a task that has been delivered
    /// is not walked back to in-progress and a terminal one is not reopened.
    /// </summary>
    public bool UpdateState(string taskId, TaskState newState, string? notes = null)
    {
        if (StateName(newState) is null)
        { _log($"ledger: {taskId} -> {newState} is not a state a task update can set"); return false; }
        if (!TryResolve(taskId, out var repo, out var task)) return false;

        var toState = TargetState(newState, task!.State);
        if (!LedgerStateMachine.CanTransitionTask(task.State, toState))
        {
            _log($"ledger: {repo}:{task.Id} is {task.State}; {task.State} -> {toState} is not a legal move");
            return false;
        }

        _writers.For(repo)!.Append(new LedgerEvent(
            DateTimeOffset.UtcNow, "task-" + toState, task.Id.ToString(), Actor: task.Owner, Note: notes));
        return true;
    }

    public TrackedTask? Get(string taskId) =>
        TryResolve(taskId, out var repo, out var task) ? ToTracked(repo!, task!) : null;

    public IReadOnlyList<TrackedTask> GetAll() =>
        AllTasks().OrderBy(x => x.Task.AssignedAt).Select(x => ToTracked(x.Repo, x.Task)).ToList();

    public IReadOnlyList<TrackedTask> GetBySession(string instanceId) =>
        AllTasks()
            .Where(x => (x.Task.Owner ?? "").Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Task.AssignedAt)
            .Select(x => ToTracked(x.Repo, x.Task))
            .ToList();

    // ---- internals ----

    IEnumerable<(string Repo, LedgerTask Task)> AllTasks()
    {
        foreach (var repo in _repos())
        {
            var writer = _writers.For(repo);
            if (writer is null) continue;
            var problems = new List<string>();
            foreach (var t in TaskMaterializer.Materialize(writer.ReadAll(problems), problems))
                yield return (repo, t);
        }
    }

    /// <summary>
    /// Find the task an id names. A repo-qualified id resolves directly. A BARE id is
    /// looked up across every repo and refused when more than one matches — numbering is
    /// per repo, so silently updating whichever was found first would mark the wrong
    /// agent's work complete.
    /// </summary>
    bool TryResolve(string taskId, out string? repo, out LedgerTask? task)
    {
        repo = null; task = null;
        if (!TryParseTaskId(taskId, out var id)) { _log($"ledger: \"{taskId}\" is not a task id"); return false; }

        var bare = id with { Repo = null };
        var matches = AllTasks()
            .Where(x => x.Task.Id == bare && (id.Repo is null || id.Repo.Equals(x.Repo, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0) { _log($"ledger: no task {taskId}"); return false; }
        if (matches.Count > 1)
        {
            _log($"ledger: {bare} is ambiguous — it exists in {string.Join(", ", matches.Select(m => m.Repo))}; qualify it (<repo>:{bare})");
            return false;
        }
        (repo, task) = matches[0];
        return true;
    }

    /// <summary>
    /// Accepts every spelling an id reaches us in, including the pre-ledger <c>T001</c>
    /// form an agent may still be holding from a mail subject issued before this change.
    /// </summary>
    public static bool TryParseTaskId(string? raw, out LedgerId id)
    {
        if (LedgerId.TryParse(raw, out id) && id.Type == LedgerType.Task) return true;

        // "T001" / "myapp:T001" — no dash, as the old in-memory tracker formatted it.
        var s = (raw ?? "").Trim();
        string? repo = null;
        var colon = s.IndexOf(':');
        if (colon >= 0) { repo = s[..colon].Trim(); s = s[(colon + 1)..].Trim(); }
        if (s.Length > 1 && (s[0] is 'T' or 't') && int.TryParse(s[1..], out var n) && n > 0)
        { id = new LedgerId(LedgerType.Task, n, string.IsNullOrEmpty(repo) ? null : repo); return true; }

        id = default;
        return false;
    }

    TrackedTask ToTracked(string repo, LedgerTask t) => new()
    {
        TaskId = t.Id.Qualify(repo).ToString(),
        Description = t.Title,
        AssignedTo = t.Owner ?? "",
        DelegatedBy = t.Actor ?? "",
        State = MapState(t.State),
        CreatedAt = t.AssignedAt.LocalDateTime,
        CompletedAt = LedgerStateMachine.IsTerminal(t.State) || t.State == "delivered"
            ? t.LastAt.LocalDateTime : null,
        Notes = t.LastNote,
    };

    /// <summary>
    /// Ledger state onto the five-value enum callers already switch on. `acked` is still
    /// Delegated: acknowledgement means the agent has READ the assignment, not that it
    /// has started.
    /// </summary>
    static TaskState MapState(string ledgerState) => ledgerState switch
    {
        "assigned" or "acked" => TaskState.Delegated,
        "in-progress" => TaskState.InProgress,
        "delivered" or "accepted" => TaskState.Completed,
        "declined" or "abandoned" => TaskState.Failed,
        _ => TaskState.Pending,
    };

    /// <summary>
    /// The ledger state a caller's <see cref="TaskState"/> means, given where the task is
    /// now. Only Failed is context-dependent, and the distinction matters: work that was
    /// never started is DECLINED, work that started and stopped is ABANDONED. Collapsing
    /// them would lose the difference between "nobody ever took this on" and "someone
    /// tried and it broke" — which is most of what a failure trail is for.
    /// </summary>
    static string TargetState(TaskState s, string current) => s switch
    {
        TaskState.InProgress => "in-progress",
        TaskState.Completed => "delivered",        // delivered, NEVER accepted (§5.3)
        TaskState.Failed => current.Equals("assigned", StringComparison.OrdinalIgnoreCase)
            ? "declined" : "abandoned",
        _ => "",
    };

    static string? StateName(TaskState s) => s switch
    {
        TaskState.InProgress => "in-progress",
        TaskState.Completed => "delivered",
        TaskState.Failed => "abandoned",
        _ => null,
    };

    /// <summary>A bare parent is qualified with the assignee's repo so the event names
    /// something definite when read out of context. A Task cannot parent a Task.</summary>
    static string? ParentFor(string? raw, string repo) =>
        LedgerId.TryParse(raw, out var p) && p.Type != LedgerType.Task
            ? p.Qualify(repo).ToString()
            : null;

    static string Truncate(string s)
    {
        s = (s ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= LedgerMailIngest.MaxTitle ? s : s[..LedgerMailIngest.MaxTitle];
    }
}

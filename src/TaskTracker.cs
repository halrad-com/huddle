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

public class TaskTracker
{
    private readonly Dictionary<string, TrackedTask> _tasks = new();
    private readonly object _lock = new();
    private int _nextId = 1;

    public TrackedTask Create(string description, string assignedTo, string delegatedBy)
    {
        lock (_lock)
        {
            var task = new TrackedTask
            {
                TaskId = $"T{_nextId:D3}",
                Description = description,
                AssignedTo = assignedTo,
                DelegatedBy = delegatedBy,
                State = TaskState.Delegated,
                CreatedAt = DateTime.Now
            };
            _tasks[task.TaskId] = task;
            _nextId++;
            return task;
        }
    }

    public bool UpdateState(string taskId, TaskState newState, string? notes = null)
    {
        lock (_lock)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return false;

            task.State = newState;
            if (notes != null)
                task.Notes = notes;
            if (newState is TaskState.Completed or TaskState.Failed)
                task.CompletedAt = DateTime.Now;
            return true;
        }
    }

    public TrackedTask? Get(string taskId)
    {
        lock (_lock)
        {
            return _tasks.GetValueOrDefault(taskId);
        }
    }

    public IReadOnlyList<TrackedTask> GetAll()
    {
        lock (_lock)
        {
            return _tasks.Values.OrderBy(t => t.CreatedAt).ToList();
        }
    }

    public IReadOnlyList<TrackedTask> GetBySession(string instanceId)
    {
        lock (_lock)
        {
            return _tasks.Values
                .Where(t => t.AssignedTo.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.CreatedAt)
                .ToList();
        }
    }
}

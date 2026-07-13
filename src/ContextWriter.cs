namespace Huddle;

public class ContextWriter
{
    private readonly string _contextPath;
    private readonly Action<string> _log;
    private readonly object _lock = new();

    public string ContextPath => _contextPath;

    public ContextWriter(string dataDir, Action<string> log)
    {
        Directory.CreateDirectory(dataDir);
        _contextPath = Path.Combine(dataDir, "context.md");
        _log = log;
        _log($"Context file: {_contextPath}");
    }

    public void Update(IReadOnlyDictionary<string, SessionInstance> instances)
    {
        lock (_lock)
        {
            try
            {
                var lines = new List<string>
                {
                    "# Claude Huddle — Active Sessions",
                    "",
                    $"Last updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    ""
                };

                // Group by repo for readability
                var grouped = instances.Values
                    .GroupBy(i => i.RepoName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    foreach (var instance in group.OrderBy(i => i.InstanceId))
                    {
                        lines.Add($"## {instance.InstanceId}");
                        lines.Add($"- **Repo:** {instance.RepoName}");
                        lines.Add($"- **Root:** {instance.Root}");
                        lines.Add($"- **Purpose:** {instance.Purpose}");

                        var statusText = instance.Status switch
                        {
                            SessionStatus.Running => $"Running ({instance.FormatUptime()})",
                            SessionStatus.Crashed => $"Crashed (exit code {instance.LastExitCode}, crashes: {instance.CrashCount})",
                            _ => instance.Status.ToString()
                        };
                        lines.Add($"- **Status:** {statusText}");

                        if (instance.ActivePersona != null)
                            lines.Add($"- **Persona:** {instance.ActivePersona}");
                        if (instance.StartedAt.HasValue)
                            lines.Add($"- **Started:** {instance.StartedAt:yyyy-MM-dd HH:mm:ss}");
                        if (instance.StoppedAt.HasValue)
                            lines.Add($"- **Stopped:** {instance.StoppedAt:yyyy-MM-dd HH:mm:ss}");
                        if (instance.ResumeCommand != null)
                            lines.Add($"- **Resume:** `{instance.ResumeCommand}` (run in {instance.Root})");

                        lines.Add("");
                    }
                }

                File.WriteAllLines(_contextPath, lines);
            }
            catch (Exception ex)
            {
                _log($"Failed to write context file: {ex.Message}");
            }
        }
    }
}

using System.Diagnostics;

namespace Huddle;

public enum SessionStatus
{
    Stopped,
    Starting,
    Running,
    Crashed,
    Stopping,
    AutoRestarting
}

public class SessionInstance
{
    public readonly object Lock = new();

    public string InstanceId { get; }
    public string RepoName { get; }
    public SessionDefinition Definition { get; }
    public SessionStatus Status { get; set; } = SessionStatus.Stopped;
    public Process? Process { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public int? LastExitCode { get; set; }
    public int CrashCount { get; set; }
    public int ConsecutiveAutoRestarts { get; set; }
    public CancellationTokenSource? AutoRestartCts { get; set; }
    public DateTime? AutoRestartAt { get; set; }
    public string? ActivePersona { get; set; }
    public Guid? SessionId { get; set; }

    // The task this session was started for (I010 F3) — the spawn prompt / dispatch
    // task text, persisted so the crash-recovery roster can say what a dead session
    // was doing without transcript forensics. Null for bare starts.
    public string? DeclaredPurpose { get; set; }

    // Project slug this session serves (projects phase 1); null when unstamped.
    public string? Project { get; set; }

    // Console window handle, captured at spawn (see SessionWindow). Not persisted:
    // handles do not survive a huddle restart, so sessions recovered from state.json
    // have none and cannot be focused until they are restarted.
    public IntPtr WindowHandle { get; set; } = IntPtr.Zero;
    public List<string> PersonaTempFiles { get; } = new();
    public PersonaConfig? PersonaConfig { get; set; }

    public SessionInstance(string instanceId, SessionDefinition definition)
    {
        InstanceId = instanceId;
        RepoName = definition.Name;
        Definition = definition;
    }

    public string Root => Definition.Root;
    public string Purpose => Definition.Purpose;

    public TimeSpan? Uptime => Status == SessionStatus.Running && StartedAt.HasValue
        ? DateTime.Now - StartedAt.Value
        : null;

    public bool IsAlive => Process is { HasExited: false };

    public string FormatUptime()
    {
        var up = Uptime;
        return up == null ? "" : FormatUptime(up.Value);
    }

    /// <summary>The same uptime wording for a span that is not a session's. The peek
    /// overlay draws huddle's own console beside the sessions, and a second format for
    /// the same line would read as a bug in whichever tile the operator looked at second.
    /// </summary>
    public static string FormatUptime(TimeSpan up)
    {
        if (up.TotalHours >= 1)
            return $"{(int)up.TotalHours}h {up.Minutes}m";
        if (up.TotalMinutes >= 1)
            return $"{(int)up.TotalMinutes}m {up.Seconds}s";
        return $"{(int)up.TotalSeconds}s";
    }

    /// <summary>
    /// Sanitize instance ID for use in file paths (colons are invalid on Windows).
    /// </summary>
    public string SafePathName => InstanceId.Replace(':', '_');

    /// <summary>
    /// The command that resumes this session's Claude conversation, or null if no
    /// session id was assigned (e.g. a --continue session). This is the single
    /// source of truth for the resume string — console log, context.md ledger, and
    /// the `resume` verb all consume it. Run it from <see cref="Root"/>: Claude keys
    /// session storage by working directory.
    /// </summary>
    public string? ResumeCommand => SessionId.HasValue
        ? $"claude --resume {SessionId.Value}"
        : null;
}

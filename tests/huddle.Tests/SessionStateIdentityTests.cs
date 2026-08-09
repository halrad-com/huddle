using Huddle;
using Xunit;

namespace HuddleTests;

// F1 (2026-07-12 review, ISSUES I009): session recovery must not bind to a recycled
// PID. IdentityMatches is the pure decision Recover consults before re-attaching:
// new-schema entries carry the original process's StartTime + image name and must
// match both; legacy entries (pre-identity state.json) fall back to requiring the
// live process to have started within a window of the SESSION's own start — a
// recycled PID always starts later, a post-reboot PID much later.
public class SessionStateIdentityTests
{
    private static readonly DateTime SessionStart = new(2026, 8, 9, 10, 0, 0, DateTimeKind.Local);

    private static SessionStateEntry Entry(DateTime? procStartedAt = null, string? procName = null) =>
        new()
        {
            InstanceId = "huddle:architect",
            RepoName = "huddle",
            Pid = 1234,
            StartedAt = SessionStart,
            ProcStartedAt = procStartedAt,
            ProcName = procName,
        };

    // ---- new schema: exact identity ----

    [Fact]
    public void NewSchema_MatchingStartTimeAndName_Accepts()
    {
        var e = Entry(procStartedAt: SessionStart.AddSeconds(1), procName: "cmd");
        Assert.True(SessionState.IdentityMatches(e, SessionStart.AddSeconds(1), "cmd"));
    }

    [Fact]
    public void NewSchema_StartTimeWithinToleranceStillAccepts()
    {
        // Serialization round-trips and kernel-time reads can drift by a hair.
        var e = Entry(procStartedAt: SessionStart, procName: "cmd");
        Assert.True(SessionState.IdentityMatches(e, SessionStart.AddSeconds(2), "cmd"));
    }

    [Fact]
    public void NewSchema_DifferentStartTime_Rejects()
    {
        // Recycled PID: same id, process born hours later.
        var e = Entry(procStartedAt: SessionStart, procName: "cmd");
        Assert.False(SessionState.IdentityMatches(e, SessionStart.AddHours(6), "cmd"));
    }

    [Fact]
    public void NewSchema_DifferentImageName_Rejects()
    {
        var e = Entry(procStartedAt: SessionStart, procName: "cmd");
        Assert.False(SessionState.IdentityMatches(e, SessionStart, "notepad"));
    }

    [Fact]
    public void NewSchema_NameComparisonIsCaseInsensitive()
    {
        var e = Entry(procStartedAt: SessionStart, procName: "CMD");
        Assert.True(SessionState.IdentityMatches(e, SessionStart, "cmd"));
    }

    // ---- legacy schema (no identity fields): session-start window fallback ----

    [Fact]
    public void Legacy_ProcessStartedNearSessionStart_Accepts()
    {
        var e = Entry(); // no ProcStartedAt / ProcName
        Assert.True(SessionState.IdentityMatches(e, SessionStart.AddSeconds(30), "cmd"));
    }

    [Fact]
    public void Legacy_RecycledPidStartedMuchLater_Rejects()
    {
        var e = Entry();
        Assert.False(SessionState.IdentityMatches(e, SessionStart.AddHours(3), "cmd"));
    }

    [Fact]
    public void Legacy_PostRebootPid_Rejects()
    {
        var e = Entry();
        Assert.False(SessionState.IdentityMatches(e, SessionStart.AddDays(1), "svchost"));
    }
}

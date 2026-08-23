using System.Text.Json;
using Huddle;
using Xunit;

namespace HuddleTests;

/// <summary>
/// 2026-08-23: a reload left the in-memory roster empty while child sessions kept running.
/// A delegate-task with startIfNeeded saw no `otherapp:architect`, started a second one over
/// the working session, and the two shared an identity — so the claims ledger saw one holder
/// and both read the same mailbox. The guard is: a live process on disk blocks the spawn,
/// whatever the roster believes.
/// </summary>
public class DuplicateSpawnGuardTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stateFile;

    public DuplicateSpawnGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _stateFile = Path.Combine(_dir, "state.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SessionManager NewManager() =>
        new(new HuddleConfig(), "claude", _dir, Path.Combine(_dir, "personas"), null, _ => { });

    private void WriteState(params SessionStateEntry[] entries) =>
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(entries));

    /// <summary>An entry describing THIS test process — guaranteed live, and its identity
    /// matches, so the guard has a real process to find.</summary>
    private static SessionStateEntry Self(string instanceId, string status = "live")
    {
        var p = System.Diagnostics.Process.GetCurrentProcess();
        return new SessionStateEntry
        {
            InstanceId = instanceId,
            RepoName = instanceId.Split(':')[0],
            Persona = instanceId.Contains(':') ? instanceId.Split(':')[1] : null,
            Pid = p.Id,
            StartedAt = p.StartTime,
            ProcStartedAt = p.StartTime,
            ProcName = p.ProcessName,
            Status = status,
        };
    }

    [Fact]
    public void A_live_process_absent_from_the_roster_is_detected()
    {
        var m = NewManager();
        m.StateFile = _stateFile;
        WriteState(Self("otherapp:architect"));
        Assert.True(m.IsLiveButUntracked("otherapp:architect", out var pid));
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, pid);
    }

    [Fact]
    public void A_dead_pid_never_blocks_a_start()
    {
        var m = NewManager();
        m.StateFile = _stateFile;
        var entry = Self("otherapp:architect");
        entry.Pid = 999999; // not a live process
        WriteState(entry);
        Assert.False(m.IsLiveButUntracked("otherapp:architect", out _));
    }

    [Fact]
    public void A_recycled_pid_never_blocks_a_start()
    {
        var m = NewManager();
        m.StateFile = _stateFile;
        var entry = Self("otherapp:architect");
        entry.ProcName = "definitely-not-this-process";   // identity mismatch (I009)
        entry.ProcStartedAt = new DateTime(2000, 1, 1);
        WriteState(entry);
        Assert.False(m.IsLiveButUntracked("otherapp:architect", out _));
    }

    [Fact]
    public void A_recoverable_entry_never_blocks_a_start()
    {
        var m = NewManager();
        m.StateFile = _stateFile;
        WriteState(Self("otherapp:architect", status: "recoverable"));
        Assert.False(m.IsLiveButUntracked("otherapp:architect", out _));
    }

    [Fact]
    public void A_different_identity_is_not_confused_for_this_one()
    {
        var m = NewManager();
        m.StateFile = _stateFile;
        WriteState(Self("otherapp:architect-2"));
        Assert.False(m.IsLiveButUntracked("otherapp:architect", out _));
    }

    [Fact]
    public void No_state_file_or_missing_path_never_blocks_a_start()
    {
        var m = NewManager();
        Assert.False(m.IsLiveButUntracked("otherapp:architect", out _));   // StateFile unset
        m.StateFile = Path.Combine(_dir, "absent.json");
        Assert.False(m.IsLiveButUntracked("otherapp:architect", out _));
    }

    [Fact]
    public void A_malformed_state_file_never_blocks_a_start()
    {
        var m = NewManager();
        m.StateFile = _stateFile;
        File.WriteAllText(_stateFile, "{ not json");
        Assert.False(m.IsLiveButUntracked("otherapp:architect", out _));
    }

    [Fact]
    public void RecoveryComplete_starts_false_so_nothing_persists_a_half_roster()
    {
        Assert.False(NewManager().RecoveryComplete);
    }
}

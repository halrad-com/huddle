namespace Huddle;

/// <summary>
/// The cross-process nudge behind <c>huddle --peek</c>: a named event the running
/// instance waits on and the launcher sets.
///
/// <para>Keyed to the config root by the same hash the singleton mutex uses, so two
/// huddle roots on one machine cannot summon each other's overlay. No files, no
/// polling, and no new process model.</para>
/// </summary>
public static class PeekSignal
{
    /// <summary>The event name for a root. The hash is shared with the singleton mutex
    /// rather than reproduced here: if the two ever disagreed, --peek would signal a name
    /// nobody is listening on and start a second huddle the mutex then refuses.</summary>
    public static string NameFor(string configDir) =>
        "Local\\huddle-peek-" + ConfigPathResolver.RootHash(configDir);

    /// <summary>Set the event if a huddle is listening for this root. False means
    /// nothing is listening, which the launcher reads as "start huddle".</summary>
    public static bool TrySignal(string configDir)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(NameFor(configDir), out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch { return false; }
    }

    /// <summary>Wait for signals on a background thread until cancelled.
    ///
    /// <para>Cancelling <paramref name="token"/> is what stops the listener; disposing the
    /// returned handle only releases the event. Disposal alone does NOT end the thread —
    /// <see cref="WaitHandle.WaitAny(WaitHandle[])"/> holds a ref on the SafeWaitHandle for
    /// the duration of the wait, so a parked wait survives the dispose and only unblocks on
    /// a later signal, whose next loop iteration then throws into the catch below. Cancel
    /// first, then dispose.</para></summary>
    public static IDisposable Listen(
        string configDir, Action onSignalled, CancellationToken token, Action<string>? log = null)
    {
        EventWaitHandle handle;
        try
        {
            handle = new EventWaitHandle(false, EventResetMode.AutoReset, NameFor(configDir));
        }
        catch (Exception ex)
        {
            // Failure-tolerant for the same reason HotkeyListener.Start is, and it matters
            // more here: this runs AFTER the orchestrator and the auto-start sessions are
            // live, so a name already held by a different kind of kernel object
            // (WaitHandleCannotBeOpenedException) or an ACL refusal would take a huddle
            // down with a running fleet attached to it, for a convenience. The peek verb
            // and the hotkey still work; only the pinned button loses its nudge.
            log?.Invoke($"peek signal: {ex.Message}; the pinned 'Huddle Sessions' button will start a second huddle instead of summoning this one");
            return new NoSignal();
        }

        var thread = new Thread(() =>
        {
            try
            {
                var waits = new[] { handle, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    if (WaitHandle.WaitAny(waits) != 0) return;   // cancelled
                    try { onSignalled(); }
                    catch { /* a failed summon must not end the listener */ }
                }
            }
            catch { /* handle disposed during shutdown */ }
        })
        { IsBackground = true, Name = "huddle-peek-signal" };

        thread.Start();
        return handle;
    }

    /// <summary>What a refused registration returns, so the caller's <c>using</c> is still
    /// a valid statement and shutdown has nothing to special-case.</summary>
    private sealed class NoSignal : IDisposable
    {
        public void Dispose() { }
    }
}

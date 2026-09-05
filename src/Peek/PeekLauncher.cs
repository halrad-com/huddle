using System.Diagnostics;

namespace Huddle;

public enum PeekLaunch { Signalled, StartHuddle, Failed }

/// <summary>
/// <c>huddle --peek</c>: the pinned taskbar button.
///
/// <para>Follows corelib's <c>mediaappLauncher.OpenOrFocus</c> shape. Detect a running
/// instance first, act accordingly, never throw, and log a typed result so an operator
/// can see why a click did or did not surface anything. One button that is correct
/// whether or not huddle is already up: it raises the switcher when huddle is running,
/// and starts huddle when it is not.</para>
/// </summary>
public static class PeekLauncher
{
    public static PeekLaunch Decide(bool signalled) =>
        signalled ? PeekLaunch.Signalled : PeekLaunch.StartHuddle;

    /// <summary>
    /// True when <c>--peek</c> appears anywhere in <paramref name="args"/>.
    ///
    /// <para>Position-independent for the reason the settings dispatch is (S3): matching
    /// <c>args[0]</c> only means <c>huddle --config x.json --peek</c> falls through the
    /// launcher and boots a second orchestrator the singleton mutex then refuses, in a
    /// red banner. A <c>--config</c> VALUE that happens to spell the verb is skipped, so
    /// a file really named <c>--peek</c> cannot hijack the dispatch.</para>
    /// </summary>
    public static bool IsPeek(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (ConfigPathResolver.IsConfigFlag(args[i])) { i++; continue; }   // skip its value
            if (args[i] == "--peek") return true;
        }
        return false;
    }

    public static int Run(string[] args, Action<string> log)
    {
        try
        {
            // The same three-arg resolve Program.Main uses, and for the same reason: from
            // a config-less cwd (Win+R, or a pinned shortcut whose working directory has
            // moved) the cwd-only overload names a huddle.json that is not there, so the
            // signal event is computed for the wrong root, TrySignal reports false with a
            // huddle plainly running, and the "started one" branch launches an instance
            // the mutex refuses.
            var configPath = Path.GetFullPath(
                ConfigPathResolver.Resolve(args, Directory.GetCurrentDirectory(), ShellRegistration.RegisteredRoot));
            var configDir = Path.GetDirectoryName(configPath) ?? ".";

            switch (Decide(PeekSignal.TrySignal(configDir)))
            {
                case PeekLaunch.Signalled:
                    // Not decoration. TrySignal proves only that some process holds the
                    // event, never that the summon reached the screen: the listener runs
                    // PeekController.Show synchronously and Show blocks on ui.Join(), so
                    // while an overlay is already up the listener is parked and the event
                    // is still opened, still set, and still reported true. Without this
                    // line every such click exits 0 in silence, and the class doc above
                    // promises the operator can see why a click did or did not surface
                    // anything.
                    log($"peek: signalled the huddle for {configDir}");
                    return 0;

                case PeekLaunch.StartHuddle:
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe))
                    { log("peek: cannot determine the running exe path"); return 3; }

                    Process.Start(new ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = configDir,
                    });
                    log($"peek: no huddle running for {configDir}; started one");
                    return 0;
            }

            return 3;
        }
        catch (Exception ex)
        {
            log($"peek: {ex.Message}");
            return 3;
        }
    }
}

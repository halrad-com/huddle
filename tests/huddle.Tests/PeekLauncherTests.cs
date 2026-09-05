using Huddle;
using Xunit;

namespace HuddleTests;

// The launcher follows corelib's OpenOrFocus shape: detect a running instance first,
// act accordingly, and never throw. One pinned button has to be correct whether or
// not huddle is already up.
public class PeekLauncherTests
{
    [Fact]
    public void A_running_instance_is_signalled_rather_than_started_again()
    {
        Assert.Equal(PeekLaunch.Signalled, PeekLauncher.Decide(signalled: true));
    }

    [Fact]
    public void With_nothing_listening_the_launcher_starts_huddle()
    {
        Assert.Equal(PeekLaunch.StartHuddle, PeekLauncher.Decide(signalled: false));
    }

    // Keyed to the config root exactly as the singleton mutex is, so two huddle roots
    // on one machine never cross-signal each other.
    [Fact]
    public void The_signal_name_is_stable_and_root_scoped()
    {
        var a = PeekSignal.NameFor(@"C:\repos\myapp");
        var b = PeekSignal.NameFor(@"C:\repos\myapp\");
        var c = PeekSignal.NameFor(@"C:\REPOS\myapp");
        var other = PeekSignal.NameFor(@"C:\repos\elsewhere");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.NotEqual(a, other);
        Assert.StartsWith(@"Local\", a);
    }

    // The recipe behind both per-root kernel object names. Pinned to an independently
    // computed value (SHA-256 of the UTF-8 bytes of "c:\repos\myapp", first 16 hex
    // characters) rather than to whatever the code happens to produce, so a change to the
    // hashing shows up here instead of at runtime as a --peek that starts a second huddle
    // the singleton mutex then refuses.
    [Fact]
    public void The_root_hash_is_the_key_the_mutex_and_the_signal_share()
    {
        // The path deliberately contains no repository name. A pinned hash is the only
        // thing that can catch a silent change to the recipe, but the public mirror
        // rewrites private names on its way out, and a renamed path hashes differently:
        // pinning one against a real repo name made this test pass privately and fail in
        // the mirror, every sync. A neutral path pins the recipe and survives the scrub.
        Assert.Equal("6337BD8590408E37", ConfigPathResolver.RootHash(@"C:\a\b"));

        // Normalisation: a trailing separator and a different case are the same root.
        Assert.Equal("6337BD8590408E37", ConfigPathResolver.RootHash(@"C:\a\b\"));
        Assert.Equal("6337BD8590408E37", ConfigPathResolver.RootHash(@"C:\A\B"));

        // ...and a different root is a different key, or two huddles would share one.
        Assert.NotEqual(ConfigPathResolver.RootHash(@"C:\a\b"), ConfigPathResolver.RootHash(@"C:\a\c"));

        Assert.Equal(
            @"Local\huddle-peek-" + ConfigPathResolver.RootHash(@"C:\a\b"),
            PeekSignal.NameFor(@"C:\a\b"));
    }

    // Position-independent, like the settings verbs: `huddle --config x.json --peek` used
    // to fall through the dispatch and boot a second orchestrator (S3).
    [Fact]
    public void Peek_is_recognised_anywhere_in_the_arguments_but_never_as_a_config_value()
    {
        Assert.True(PeekLauncher.IsPeek(new[] { "--peek" }));
        Assert.True(PeekLauncher.IsPeek(new[] { "--config", @"C:\x\huddle.json", "--peek" }));
        Assert.False(PeekLauncher.IsPeek(new[] { "--config", "--peek" }));   // a file named --peek
        Assert.False(PeekLauncher.IsPeek(Array.Empty<string>()));
    }

    [Fact]
    public void Signalling_with_no_listener_reports_false()
    {
        Assert.False(PeekSignal.TrySignal(@"C:\repos\nothing-is-listening-here"));
    }

    [Fact]
    public void A_listener_receives_a_signal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huddle-peek-" + Guid.NewGuid().ToString("N"));
        using var fired = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        using (PeekSignal.Listen(dir, () => fired.Set(), cts.Token))
        {
            Assert.True(PeekSignal.TrySignal(dir));
            Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
        }

        cts.Cancel();
    }
}

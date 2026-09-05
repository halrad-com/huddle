using System.Drawing;
using Huddle;
using Xunit;

namespace HuddleTests;

// A DWM thumbnail is an OS resource. The registration and unregistration counts must
// balance across every show and hide, or the overlay leaks one handle per tile per
// summon. The DWM calls are injected so that balance is testable without a desktop.
public class ThumbnailHostTests
{
    private sealed class FakeDwm
    {
        public int Registered, Unregistered;
        private long _next = 1000;

        public IntPtr Register(IntPtr dest, IntPtr src)
        {
            if (src == IntPtr.Zero) return IntPtr.Zero;   // as DWM does for a dead window
            Registered++;
            return new IntPtr(_next++);
        }

        public bool Unregister(IntPtr h) { Unregistered++; return true; }
    }

    private static ThumbnailHost Host(FakeDwm f) =>
        new(new IntPtr(1), f.Register, f.Unregister);

    [Fact]
    public void Adding_a_source_registers_one_thumbnail()
    {
        var f = new FakeDwm();
        using var host = Host(f);

        Assert.True(host.Add(new IntPtr(7), new Rectangle(0, 0, 320, 180)));
        Assert.Equal(1, host.Count);
        Assert.Equal(1, f.Registered);
    }

    [Fact]
    public void A_source_the_dwm_refuses_is_reported_and_not_counted()
    {
        var f = new FakeDwm();
        using var host = Host(f);

        Assert.False(host.Add(IntPtr.Zero, new Rectangle(0, 0, 320, 180)));
        Assert.Equal(0, host.Count);
    }

    [Fact]
    public void Clear_unregisters_everything_it_registered()
    {
        var f = new FakeDwm();
        using var host = Host(f);
        host.Add(new IntPtr(7), new Rectangle(0, 0, 320, 180));
        host.Add(new IntPtr(8), new Rectangle(0, 0, 320, 180));

        host.Clear();

        Assert.Equal(0, host.Count);
        // Without this the balance assertion below would read 0 == 0 and pass vacuously if
        // both Adds had silently failed.
        Assert.Equal(2, f.Registered);
        Assert.Equal(f.Registered, f.Unregistered);
    }

    [Fact]
    public void Repeated_show_and_hide_cycles_stay_balanced()
    {
        var f = new FakeDwm();
        using var host = Host(f);

        for (var cycle = 0; cycle < 5; cycle++)
        {
            for (var i = 0; i < 4; i++)
                host.Add(new IntPtr(i + 1), new Rectangle(0, 0, 320, 180));
            host.Clear();
        }

        Assert.Equal(20, f.Registered);
        Assert.Equal(20, f.Unregistered);
        Assert.Equal(0, host.Count);
    }

    [Fact]
    public void Dispose_releases_anything_still_held()
    {
        var f = new FakeDwm();
        var host = Host(f);
        host.Add(new IntPtr(7), new Rectangle(0, 0, 320, 180));

        host.Dispose();

        Assert.Equal(1, f.Unregistered);
    }

    // Every interop failure below used to be swallowed. A blank tile with no trace is
    // indistinguishable from a struct-layout bug, and a release that fails silently is the
    // leak this class exists to prevent, happening with no evidence. The log is the
    // evidence, so these pin that it actually fires.

    [Fact]
    public void A_refused_registration_is_logged_and_names_the_window()
    {
        var log = new List<string>();
        using var host = new ThumbnailHost(new IntPtr(1), (_, _) => IntPtr.Zero, _ => true, log.Add);

        Assert.False(host.Add(new IntPtr(0x7f), new Rectangle(0, 0, 320, 180)));
        Assert.Contains(log, m => m.Contains("7f"));
    }

    [Fact]
    public void A_source_window_already_gone_is_logged_differently_from_a_refusal()
    {
        var f = new FakeDwm();
        var refused = new List<string>();
        var gone = new List<string>();

        using (var host = new ThumbnailHost(new IntPtr(1), (_, _) => IntPtr.Zero, f.Unregister, refused.Add))
            host.Add(new IntPtr(7), new Rectangle(0, 0, 320, 180));

        using (var host = new ThumbnailHost(new IntPtr(1), f.Register, f.Unregister, gone.Add))
            host.Add(IntPtr.Zero, new Rectangle(0, 0, 320, 180));

        // A dwmapi that cannot load must not read as "that window just closed".
        Assert.NotEqual(Assert.Single(refused), Assert.Single(gone));
    }

    [Fact]
    public void A_release_that_fails_is_logged_with_its_handle()
    {
        var f = new FakeDwm();
        var log = new List<string>();
        using var host = new ThumbnailHost(new IntPtr(1), f.Register, _ => false, log.Add);
        host.Add(new IntPtr(7), new Rectangle(0, 0, 320, 180));   // the fake's first handle is 1000

        host.Clear();

        Assert.Contains(log, m => m.Contains("releasing") && m.Contains("3e8"));
        Assert.Equal(0, host.Count);
    }

    [Fact]
    public void A_release_that_throws_is_logged_and_the_rest_are_still_released()
    {
        var f = new FakeDwm();
        var log = new List<string>();
        var released = new List<IntPtr>();
        using var host = new ThumbnailHost(
            new IntPtr(1),
            f.Register,
            h =>
            {
                if (h == new IntPtr(1000)) throw new InvalidOperationException("gone");
                released.Add(h);
                return true;
            },
            log.Add);
        host.Add(new IntPtr(7), new Rectangle(0, 0, 320, 180));   // handle 1000, throws on release
        host.Add(new IntPtr(8), new Rectangle(0, 0, 320, 180));   // handle 1001, must still be released

        host.Clear();

        Assert.Contains(new IntPtr(1001), released);
        Assert.Contains(log, m => m.Contains("InvalidOperationException") && m.Contains("gone"));
        Assert.Equal(0, host.Count);
    }
}

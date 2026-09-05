using System.Drawing;
using System.Runtime.InteropServices;

namespace Huddle;

/// <summary>
/// Live thumbnails of other windows, composited by the desktop window manager into
/// regions of the overlay.
///
/// <para>This is the reason the overlay is a real window and not a web page: DWM
/// composites into a window handle, so a thumbnail cannot be placed inside a hosted
/// document.</para>
///
/// <para>Every registration is an OS resource. They are released on <see cref="Clear"/>
/// and on <see cref="Dispose"/>, and the overlay clears on every hide, so a summon
/// cannot leak one handle per tile. The DWM calls are injectable so that the balance
/// is unit-testable without a desktop.</para>
///
/// <para>No failure here is silent. Every interop call that can fail reports through the
/// injected <c>log</c>: a swallowed exception is how a struct-layout mistake turns into a
/// blank tile with no trace, and a discarded unregister HRESULT is how the very leak this
/// class exists to prevent happens without evidence.</para>
/// </summary>
public sealed class ThumbnailHost : IDisposable
{
    private readonly IntPtr _destination;
    private readonly Func<IntPtr, IntPtr, IntPtr> _register;
    private readonly Func<IntPtr, bool> _unregister;
    private readonly Action<string> _log;
    private readonly List<IntPtr> _handles = new();

    public ThumbnailHost(
        IntPtr destination,
        Func<IntPtr, IntPtr, IntPtr>? register = null,
        Func<IntPtr, bool>? unregister = null,
        Action<string>? log = null)
    {
        _destination = destination;
        _log = log ?? (_ => { });
        _register = register ?? RegisterViaDwm;
        _unregister = unregister ?? UnregisterViaDwm;
    }

    public int Count => _handles.Count;

    /// <summary>Register one source window and place it in <paramref name="bounds"/>.
    /// Returns false when the DWM will not give a thumbnail for that window, which is
    /// normal for a window that closed between the snapshot and the show; the caller
    /// draws that tile's chrome and labels without a picture.
    ///
    /// <para>A registration that succeeds but cannot be <see cref="Place"/>d still returns
    /// true, and says so in the log. The return value answers "is there a registration to
    /// account for", which is the question the caller acts on and the one this class can
    /// answer without a desktop; placement runs through an un-injected OS call, so folding
    /// it into the result would make the contract depend on whether a real compositor is
    /// present. The blank tile is reported rather than returned.</para></summary>
    public bool Add(IntPtr source, Rectangle bounds)
    {
        var handle = _register(_destination, source);
        if (handle == IntPtr.Zero)
        {
            // Distinguishing these matters: the first is routine, the second means the DWM
            // said no to a window we believed was alive, which is worth noticing.
            _log(source == IntPtr.Zero
                ? "ThumbnailHost: no thumbnail — the source window was already gone (null handle)"
                : $"ThumbnailHost: no thumbnail for window 0x{source.ToInt64():x} — the desktop window manager refused it");
            return false;
        }

        _handles.Add(handle);
        if (!Place(handle, bounds))
            _log($"ThumbnailHost: thumbnail 0x{handle.ToInt64():x} is registered but unplaced — its tile will be blank");
        return true;
    }

    public void Clear()
    {
        foreach (var h in _handles)
        {
            try
            {
                if (!_unregister(h))
                    _log($"ThumbnailHost: releasing thumbnail 0x{h.ToInt64():x} failed — the handle is dropped, so this is the shape of a leak");
            }
            catch (Exception ex)
            {
                // A thumbnail whose window died is already gone; keep releasing the rest.
                _log($"ThumbnailHost: releasing thumbnail 0x{h.ToInt64():x} threw {ex.GetType().Name} — {ex.Message}");
            }
        }
        _handles.Clear();
    }

    public void Dispose() => Clear();

    private bool Place(IntPtr handle, Rectangle bounds)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY | DWM_TNP_OPACITY,
            rcDestination = new RECT
            {
                left = bounds.Left,
                top = bounds.Top,
                right = bounds.Right,
                bottom = bounds.Bottom,
            },
            fVisible = true,
            fSourceClientAreaOnly = true,
            opacity = 255,
        };

        // Placement failure leaves the tile blank and never crashes the overlay, but it is
        // reported: a throw here is a defect in this file rather than a runtime condition
        // (a wrong struct layout raises MarshalDirectiveException, a missing dwmapi raises
        // DllNotFoundException), and naming the type and message is the only thing that
        // makes such a bug findable from a blank tile.
        try
        {
            var hr = DwmUpdateThumbnailProperties(handle, ref props);
            if (hr == 0) return true;

            _log($"ThumbnailHost: DwmUpdateThumbnailProperties(0x{handle.ToInt64():x}) failed, hr=0x{hr:x8}");
            return false;
        }
        catch (Exception ex)
        {
            _log($"ThumbnailHost: placing thumbnail 0x{handle.ToInt64():x} threw {ex.GetType().Name} — {ex.Message}");
            return false;
        }
    }

    private IntPtr RegisterViaDwm(IntPtr destination, IntPtr source)
    {
        try
        {
            var hr = DwmRegisterThumbnail(destination, source, out var handle);
            if (hr == 0) return handle;

            _log($"ThumbnailHost: DwmRegisterThumbnail for window 0x{source.ToInt64():x} failed, hr=0x{hr:x8}");
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            // Without this line a missing or broken dwmapi.dll is indistinguishable from a
            // source window that simply closed.
            _log($"ThumbnailHost: DwmRegisterThumbnail threw {ex.GetType().Name} — {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private bool UnregisterViaDwm(IntPtr handle)
    {
        var hr = DwmUnregisterThumbnail(handle);
        if (hr == 0) return true;

        _log($"ThumbnailHost: DwmUnregisterThumbnail(0x{handle.ToInt64():x}) failed, hr=0x{hr:x8}");
        return false;
    }

    private const int DWM_TNP_RECTDESTINATION = 0x00000001;
    private const int DWM_TNP_VISIBLE = 0x00000008;
    private const int DWM_TNP_OPACITY = 0x00000004;
    private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
}

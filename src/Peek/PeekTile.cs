namespace Huddle;

/// <summary>
/// One session as the peek overlay needs to see it: a flat snapshot taken by
/// <see cref="PeekController"/>, never a live <see cref="SessionInstance"/>.
/// <para>The indirection is deliberate. Tile construction is where the
/// selectability rule lives, and that rule has to be testable without a running
/// process, a real window handle or a transcript file on disk.</para>
/// </summary>
public readonly record struct PeekSource(
    string InstanceId,
    string? Project,
    string Uptime,
    IntPtr WindowHandle,
    string? ApiError,
    int IdleMinutes,
    int Unread,
    string? Title = null);

/// <summary>One drawn tile. <see cref="Selectable"/> is false when pressing Enter on
/// it would do nothing, which keeps it out of the default selection entirely.</summary>
public readonly record struct PeekTile(
    string InstanceId,
    IntPtr WindowHandle,
    bool Selectable,
    string Line1,
    string Line2,
    string? Note);

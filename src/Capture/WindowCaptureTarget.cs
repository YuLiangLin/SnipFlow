namespace SnipFlow.Capture;

/// <summary>Identity and physical screen geometry of a user-selected top-level window.</summary>
/// <remarks>
/// <see cref="Handle"/> is an HWND on Windows and the X11 client window id on Linux. On X11,
/// <see cref="ProcessId"/> comes from _NET_WM_PID and is 0 for the few clients that do not set it;
/// <see cref="Dpi"/> is 0 (unknown) on Linux, which disables the DPI comparison.
/// </remarks>
public sealed record WindowCaptureTarget(nint Handle, uint ProcessId,
    PixelRect WindowBounds, PixelRect ClientBounds, uint Dpi = 0);

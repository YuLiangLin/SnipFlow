namespace SnipFlow.Capture;

internal sealed record CaptureMonitor(PixelRect Bounds, bool IsPrimary);
internal sealed record CaptureWindow(nint Handle, uint ProcessId, PixelRect Bounds,
    string Title, bool CanSelect);

/// <summary>A visible top-level surface stacked above another window, with physical bounds.</summary>
/// <remarks>On X11, <see cref="Handle"/> is the client window id for managed windows (the same id as
/// <see cref="WindowCaptureTarget.Handle"/> and Avalonia's platform handle) and the window itself for
/// unmanaged override-redirect surfaces such as menus and tooltips.</remarks>
internal readonly record struct CaptureWindowLayer(nint Handle, PixelRect Bounds);

/// <summary>
/// Monitors, desktop pixels and top-level window identity in physical desktop pixels.
/// The platform parts live in Platform/Windows/Capture (Win32, DWM, GDI) and Platform/Linux/Capture
/// (X11: XRandR, Xlib, EWMH; Wayland: xdg-desktop-portal).
/// </summary>
internal static partial class CaptureNative
{
    /// <summary>Physical monitor rectangles. Throws when no monitor can be captured.</summary>
    internal static partial IReadOnlyList<CaptureMonitor> GetMonitors();

    /// <summary>The pointer position in physical desktop pixels.</summary>
    internal static partial PixelPoint CursorPosition();

    /// <summary>Snapshot top-level windows in desktop z-order (topmost first) before the overlays are shown.
    /// Empty in Wayland sessions, where other applications' windows cannot be enumerated.</summary>
    internal static partial IReadOnlyList<CaptureWindow> GetWindows();

    /// <summary>Reads the current state of one top-level window as <see cref="GetWindows"/> reports it.</summary>
    internal static partial bool TryGetWindow(nint window, out CaptureWindow item);

    /// <summary>Reads the identity, frame, client area and DPI of a selectable top-level window.</summary>
    internal static partial bool TryGetWindowTarget(nint window, out WindowCaptureTarget target);

    /// <summary>Brings a window the user just picked to the foreground; true when it is the foreground window.</summary>
    internal static partial bool ActivateSelectedWindow(WindowCaptureTarget target);

    /// <summary>The top-level foreground window (HWND root / X11 _NET_ACTIVE_WINDOW), or 0 when unknown
    /// (always 0 in Wayland sessions).</summary>
    internal static partial nint GetForegroundWindow();

    /// <summary>True when the window excludes itself from capture (Windows display affinity). Always false on Linux.</summary>
    internal static partial bool IsCaptureProtected(nint window);

    /// <summary>Waits until the compositor has presented the latest desktop changes (DwmFlush on Windows).</summary>
    internal static partial void FlushDesktopChanges();

    /// <summary>
    /// Places <paramref name="window"/> exactly over <paramref name="bounds"/> (physical pixels) above other
    /// windows without activating it. On Linux a rectangle equal to a monitor makes the window full screen
    /// on that monitor, so desktop panels cannot push it aside.
    /// </summary>
    internal static partial void PositionOverlay(Window window, PixelRect bounds);

    /// <summary>
    /// Copies desktop pixels. Gaps between monitors stay black. The buffer has exactly
    /// <c>bounds.Size</c> pixels except in Wayland sessions, where it keeps the compositor resolution.
    /// </summary>
    internal static partial PixelBuffer CopyDesktop(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors);

    /// <summary>Asynchronous <see cref="CopyDesktop"/>; only Wayland (portal) captures actually wait.</summary>
    internal static partial Task<PixelBuffer> CopyDesktopAsync(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors);

    /// <summary>True while the window exists, is shown (mapped), not minimized and not cloaked.</summary>
    internal static partial bool IsWindowAlive(nint window);

    /// <summary>The visible frame of a top-level window (DWM / _NET_FRAME_EXTENTS), or null.</summary>
    internal static partial PixelRect? GetWindowBounds(nint window);

    /// <summary>The client area of a top-level window in screen pixels, or null.</summary>
    internal static partial PixelRect? GetClientBounds(nint window);

    /// <summary>The owning process id (_NET_WM_PID on X11), or 0 when unknown.</summary>
    internal static partial uint GetWindowProcessId(nint window);

    /// <summary>The window DPI on Windows; 0 (unknown) on Linux.</summary>
    internal static partial uint GetWindowDpi(nint window);

    /// <summary>The topmost top-level window under a physical point, or 0.</summary>
    internal static partial nint GetWindowAt(PixelPoint point);

    /// <summary>True while a mouse button is held anywhere on the desktop (X11 / Windows; false on Wayland).</summary>
    internal static partial bool IsPointerButtonDown();

    /// <summary>
    /// Visible top-level surfaces stacked above <paramref name="window"/>, nearest first, skipping
    /// <paramref name="excludedWindows"/>. Null while the window list is changing or the window is
    /// not on the desktop (also on Wayland, where stacking is unknown).
    /// </summary>
    internal static partial IReadOnlyList<CaptureWindowLayer>? GetWindowsAbove(nint window,
        IReadOnlyCollection<nint>? excludedWindows = null);

    /// <summary>Excludes one of SnipFlow's windows from screen capture where the platform allows it
    /// (Windows display affinity). Returns false on Linux: keep such windows outside the captured area.</summary>
    internal static partial bool TryExcludeFromCapture(nint window);

    internal static bool IsCurrentWindow(CaptureWindow window) =>
        TryGetWindow(window.Handle, out var current) && current.CanSelect
        && current.ProcessId == window.ProcessId && current.Bounds == window.Bounds;

    internal static WindowCaptureTarget GetWindowTarget(CaptureWindow selected)
    {
        if (!IsCurrentWindow(selected) || !TryGetWindowTarget(selected.Handle, out var target)
            || target.ProcessId != selected.ProcessId)
            throw new InvalidOperationException(I18n.T("選定視窗已關閉、隱藏或移動，請重新選取。"));
        return target;
    }

    internal static bool IsCurrentTarget(WindowCaptureTarget target) =>
        TryGetWindowTarget(target.Handle, out var current) && current.ProcessId == target.ProcessId
        && current.WindowBounds == target.WindowBounds && current.ClientBounds == target.ClientBounds
        && (target.Dpi == 0 || current.Dpi == target.Dpi);

    internal static bool IsForegroundTarget(WindowCaptureTarget target) =>
        target.Handle != 0 && GetForegroundWindow() == target.Handle;

    internal static bool SameMonitorLayout(IReadOnlyList<CaptureMonitor> first,
        IReadOnlyList<CaptureMonitor> second) => first.Count == second.Count
        && first.OrderBy(monitor => monitor.Bounds.X).ThenBy(monitor => monitor.Bounds.Y)
            .ThenBy(monitor => monitor.Bounds.Width).ThenBy(monitor => monitor.Bounds.Height)
            .ThenBy(monitor => monitor.IsPrimary)
            .SequenceEqual(second.OrderBy(monitor => monitor.Bounds.X).ThenBy(monitor => monitor.Bounds.Y)
                .ThenBy(monitor => monitor.Bounds.Width).ThenBy(monitor => monitor.Bounds.Height)
                .ThenBy(monitor => monitor.IsPrimary));

    internal static bool CanCaptureWindowRegion(WindowCaptureTarget target, PixelRect region,
        IReadOnlyList<CaptureMonitor> monitors, IReadOnlyCollection<nint>? excludedWindows, out string reason)
    {
        reason = "";
        if (!IsCurrentTarget(target))
        { reason = "選定視窗的位置、大小或縮放已改變，請重新選取。"; return false; }
        if (IsCaptureProtected(target.Handle))
        { reason = "此視窗禁止擷取，請選取其他視窗。"; return false; }
        if (region.Width <= 0 || region.Height <= 0 || !CaptureGeometry.Contains(target.ClientBounds, region))
        { reason = "請在選定視窗的內容區內框選。"; return false; }
        if (!SameMonitorLayout(monitors, GetMonitors()) || !IsOnMonitors(region, monitors))
        { reason = "顯示器排列已改變，或範圍不在螢幕內，請重新選取。"; return false; }

        var above = GetWindowsAbove(target.Handle, excludedWindows);
        if (above is null)
        { reason = "視窗狀態正在變更，請稍後重新選取。"; return false; }
        if (above.Any(covering => CaptureGeometry.IntersectsWith(covering.Bounds, region)))
        { reason = "框選範圍被其他視窗遮住，請重新框選。"; return false; }
        return true;
    }

    // A bounding union alone would accept the black gaps between staggered displays.
    internal static bool IsOnMonitors(PixelRect region, IReadOnlyList<CaptureMonitor> monitors) =>
        CaptureGeometry.Subtract(region, monitors.Select(monitor => monitor.Bounds)).Count == 0;

    /// <summary>Union of all monitor rectangles (the virtual desktop).</summary>
    internal static PixelRect DesktopBounds(IReadOnlyList<CaptureMonitor> monitors) =>
        monitors.Select(monitor => monitor.Bounds).Aggregate(CaptureGeometry.Union);
}

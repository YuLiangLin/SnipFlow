using SnipFlow.Platform.Linux.Capture;

namespace SnipFlow.Capture;

/// <summary>
/// Linux implementation of <see cref="CaptureNative"/>. X11 sessions use Xlib directly
/// (<see cref="X11Desktop"/>). In Wayland sessions SnipFlow's X connection is XWayland: monitors and the
/// pointer over SnipFlow's own windows still come from it, but other windows are not visible and desktop
/// pixels come from xdg-desktop-portal (<see cref="WaylandDesktop"/>).
/// </summary>
internal static partial class CaptureNative
{
    private static bool Wayland => PlatformInfo.IsWayland;

    internal static partial IReadOnlyList<CaptureMonitor> GetMonitors() => X11Desktop.GetMonitors();

    internal static partial PixelPoint CursorPosition() => X11Desktop.CursorPosition();

    internal static partial IReadOnlyList<CaptureWindow> GetWindows() =>
        Wayland ? Array.Empty<CaptureWindow>() : X11Desktop.GetWindows();

    internal static partial bool TryGetWindow(nint window, out CaptureWindow item)
    {
        if (Wayland) { item = null!; return false; }
        return X11Desktop.TryGetWindow(window, out item);
    }

    internal static partial bool TryGetWindowTarget(nint window, out WindowCaptureTarget target)
    {
        target = null!;
        if (Wayland || !X11Desktop.TryGetGeometry(window, out var geometry) || geometry.IsShellSurface
            || geometry.ProcessId == Environment.ProcessId || !CaptureGeometry.Contains(geometry.Frame, geometry.ClientArea))
            return false;
        target = new WindowCaptureTarget(window, geometry.ProcessId, geometry.Frame, geometry.ClientArea);
        return true;
    }

    internal static partial bool ActivateSelectedWindow(WindowCaptureTarget target)
    {
        if (Wayland || !IsCurrentTarget(target)) return false;
        if (GetForegroundWindow() == target.Handle) return true;
        // Called only after a user picks this exact window: one EWMH activation request, no
        // synthesized input. Window managers apply it asynchronously, so wait briefly.
        X11Desktop.RequestActivation(target.Handle);
        var deadline = DateTime.UtcNow.AddMilliseconds(750);
        while (DateTime.UtcNow < deadline && GetForegroundWindow() != target.Handle)
            Thread.Sleep(15);
        return IsCurrentTarget(target) && GetForegroundWindow() == target.Handle;
    }

    internal static partial nint GetForegroundWindow() => Wayland ? 0 : X11Desktop.GetForegroundWindow();

    // No display affinity on Linux.
    internal static partial bool IsCaptureProtected(nint window) => false;

    internal static partial bool TryExcludeFromCapture(nint window) => false;

    internal static partial void FlushDesktopChanges()
    {
        // X11 has no DwmFlush. Wait for the X server to process pending requests, then give the
        // window manager, the uncovered applications and any compositor (including window close
        // animations) time to present the new desktop.
        X11Desktop.Sync();
        Thread.Sleep(Wayland || X11Desktop.IsCompositing() ? 220 : 120);
        X11Desktop.Sync();
    }

    internal static partial void PositionOverlay(Window window, PixelRect bounds)
    {
        var fullScreen = window.Screens.All.Any(screen => screen.Bounds == bounds)
            || GetMonitors().Any(monitor => monitor.Bounds == bounds);
        if (window.WindowState == WindowState.FullScreen)
        {
            if (fullScreen) return;
            window.WindowState = WindowState.Normal;
        }
        var screen = window.Screens.ScreenFromBounds(bounds);
        var scaling = screen?.Scaling is > 0 and var value ? value : window.RenderScaling > 0 ? window.RenderScaling : 1;
        window.Position = bounds.Position;
        window.Width = bounds.Width / scaling;
        window.Height = bounds.Height / scaling;
        // A full-screen window covers its monitor above desktop panels (GNOME keeps normal windows
        // below the top bar); the window manager picks the monitor that contains the window.
        if (fullScreen)
            window.WindowState = WindowState.FullScreen;
    }

    internal static partial PixelBuffer CopyDesktop(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors) =>
        Wayland ? WaylandDesktop.CopyDesktop(bounds, monitors) : X11Desktop.CopyDesktop(bounds, monitors);

    internal static partial Task<PixelBuffer> CopyDesktopAsync(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors) =>
        Wayland ? WaylandDesktop.CopyDesktopAsync(bounds, monitors) : Task.FromResult(X11Desktop.CopyDesktop(bounds, monitors));

    internal static partial bool IsWindowAlive(nint window) =>
        !Wayland && X11Desktop.TryGetGeometry(window, out _);

    internal static partial PixelRect? GetWindowBounds(nint window) =>
        !Wayland && X11Desktop.TryGetGeometry(window, out var geometry) ? geometry.Frame : null;

    internal static partial PixelRect? GetClientBounds(nint window) =>
        !Wayland && X11Desktop.TryGetGeometry(window, out var geometry) ? geometry.ClientArea : null;

    internal static partial uint GetWindowProcessId(nint window) =>
        !Wayland && X11Desktop.TryGetGeometry(window, out var geometry) ? geometry.ProcessId : 0;

    internal static partial uint GetWindowDpi(nint window) => 0;

    internal static partial nint GetWindowAt(PixelPoint point) => Wayland ? 0 : X11Desktop.GetWindowAt(point);

    internal static partial bool IsPointerButtonDown() => !Wayland && X11Desktop.IsPointerButtonDown();

    internal static partial IReadOnlyList<CaptureWindowLayer>? GetWindowsAbove(nint window,
        IReadOnlyCollection<nint>? excludedWindows) =>
        Wayland ? null : X11Desktop.GetWindowsAbove(window, excludedWindows);
}

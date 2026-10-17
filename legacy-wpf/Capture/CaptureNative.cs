using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SnipFlow.Services;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Capture;

internal sealed record CaptureMonitor(DrawingRectangle Bounds, bool IsPrimary);
internal sealed record CaptureWindow(IntPtr Handle, uint ProcessId, DrawingRectangle Bounds,
    string Title, bool CanSelect);

internal static class CaptureNative
{
    private const uint MonitorInfoPrimary = 1;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private static readonly IntPtr Topmost = new(-1);

    internal static IReadOnlyList<CaptureMonitor> GetMonitors()
    {
        var monitors = new List<CaptureMonitor>();
        var enumerationError = 0;
        MonitorEnumeration callback = (IntPtr monitor, IntPtr dc, ref NativeRectangle rectangle, IntPtr data) =>
        {
            var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
            if (!GetMonitorInfo(monitor, ref information))
            {
                enumerationError = Marshal.GetLastWin32Error();
                if (enumerationError == 0)
                    enumerationError = 1;
                return false;
            }

            var bounds = information.Monitor.ToRectangle();
            if (bounds.Width > 0 && bounds.Height > 0)
                monitors.Add(new CaptureMonitor(bounds, (information.Flags & MonitorInfoPrimary) != 0));
            return true;
        };

        var succeeded = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        if (enumerationError != 0)
            throw new Win32Exception(enumerationError);
        if (!succeeded)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (monitors.Count == 0)
            throw new InvalidOperationException(I18n.T("目前找不到可擷取的螢幕。"));

        return monitors;
    }

    internal static void CopyDesktopPixels(IntPtr destination, DrawingRectangle bounds,
        int destinationX = 0, int destinationY = 0)
    {
        var desktop = GetDC(IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new Win32Exception(I18n.T("無法取得桌面的繪圖內容。"));
        try
        {
            const uint sourceCopyWithLayeredWindows = 0x40CC0020;
            if (!BitBlt(destination, destinationX, destinationY, bounds.Width, bounds.Height, desktop,
                    bounds.X, bounds.Y, sourceCopyWithLayeredWindows))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, desktop);
        }
    }

    internal static DrawingPoint CursorPosition()
    {
        if (!GetPhysicalCursorPos(out var position))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new DrawingPoint(position.X, position.Y);
    }

    /// <summary>Snapshot top-level windows in desktop z-order before the overlays are shown.</summary>
    internal static IReadOnlyList<CaptureWindow> GetWindows()
    {
        var windows = new List<CaptureWindow>();
        WindowEnumeration callback = (window, _) =>
        {
            if (TryGetWindow(window, out var item))
                windows.Add(item);
            return true;
        };
        if (!EnumWindows(callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return windows;
    }

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

    private static bool TryGetWindowTarget(IntPtr window, out WindowCaptureTarget target)
    {
        target = null!;
        if (!TryGetWindow(window, out var selected) || !selected.CanSelect
            || GetAncestor(window, 2) != window || !GetWindowRect(window, out var frame)
            || !GetClientRect(window, out var client))
            return false;
        var first = new NativePoint { X = client.Left, Y = client.Top };
        var last = new NativePoint { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(window, ref first) || !ClientToScreen(window, ref last))
            return false;
        var clientBounds = DrawingRectangle.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
            Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
        var windowBounds = frame.ToRectangle();
        var dpi = GetDpiForWindow(window);
        if (dpi == 0 || clientBounds.Width <= 0 || clientBounds.Height <= 0
            || !windowBounds.Contains(clientBounds))
            return false;
        target = new WindowCaptureTarget(window, selected.ProcessId, windowBounds, clientBounds, dpi);
        return true;
    }

    internal static bool ActivateSelectedWindow(WindowCaptureTarget target)
    {
        if (!IsCurrentTarget(target)) return false;
        if (GetAncestor(GetForegroundWindow(), 2) == target.Handle) return true;
        // Called only after a user picks this exact window. Do not attach input queues,
        // synthesize input, restore minimized windows or activate any replacement HWND.
        SetForegroundWindow(target.Handle);
        return IsCurrentTarget(target) && GetAncestor(GetForegroundWindow(), 2) == target.Handle;
    }

    internal static bool IsForegroundTarget(WindowCaptureTarget target) =>
        GetAncestor(GetForegroundWindow(), 2) == target.Handle;

    internal static bool IsCaptureProtected(IntPtr window) =>
        GetWindowDisplayAffinity(window, out var affinity) && affinity != 0;

    internal static bool SameMonitorLayout(IReadOnlyList<CaptureMonitor> first,
        IReadOnlyList<CaptureMonitor> second) => first.Count == second.Count
        && first.OrderBy(monitor => monitor.Bounds.Left).ThenBy(monitor => monitor.Bounds.Top)
            .ThenBy(monitor => monitor.Bounds.Width).ThenBy(monitor => monitor.Bounds.Height)
            .ThenBy(monitor => monitor.IsPrimary)
            .SequenceEqual(second.OrderBy(monitor => monitor.Bounds.Left).ThenBy(monitor => monitor.Bounds.Top)
                .ThenBy(monitor => monitor.Bounds.Width).ThenBy(monitor => monitor.Bounds.Height)
                .ThenBy(monitor => monitor.IsPrimary));

    internal static bool CanCaptureWindowRegion(WindowCaptureTarget target, DrawingRectangle region,
        IReadOnlyList<CaptureMonitor> monitors, IReadOnlyCollection<IntPtr>? excludedWindows, out string reason)
    {
        reason = "";
        if (!IsCurrentTarget(target))
        { reason = "選定視窗的位置、大小或縮放已改變，請重新選取。"; return false; }
        if (IsCaptureProtected(target.Handle))
        { reason = "此視窗禁止擷取，請選取其他視窗。"; return false; }
        if (region.Width <= 0 || region.Height <= 0 || !target.ClientBounds.Contains(region))
        { reason = "請在選定視窗的內容區內框選。"; return false; }
        if (!SameMonitorLayout(monitors, GetMonitors()) || !IsOnMonitors(region, monitors))
        { reason = "顯示器排列已改變，或範圍不在螢幕內，請重新選取。"; return false; }

        var seen = new HashSet<IntPtr> { target.Handle };
        for (var above = GetWindow(target.Handle, 3); above != IntPtr.Zero; above = GetWindow(above, 3))
        {
            if (seen.Count >= 4096 || !seen.Add(above))
            { reason = "視窗狀態正在變更，請稍後重新選取。"; return false; }
            if (excludedWindows?.Contains(above) == true || !IsWindowVisible(above) || IsIconic(above)
                || (DwmGetWindowAttribute(above, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                continue;
            if (!GetWindowRect(above, out var covering))
            { reason = "視窗狀態正在變更，請稍後重新選取。"; return false; }
            if (covering.ToRectangle().IntersectsWith(region))
            { reason = "框選範圍被其他視窗遮住，請重新框選。"; return false; }
        }
        return true;
    }

    private static bool IsOnMonitors(DrawingRectangle region, IReadOnlyList<CaptureMonitor> monitors)
    {
        // A bounding union alone would accept the black gaps between staggered displays.
        var uncovered = new List<DrawingRectangle> { region };
        foreach (var monitor in monitors)
        {
            var remaining = new List<DrawingRectangle>();
            foreach (var piece in uncovered)
            {
                var intersection = DrawingRectangle.Intersect(piece, monitor.Bounds);
                if (intersection.Width <= 0 || intersection.Height <= 0) { remaining.Add(piece); continue; }
                if (piece.Top < intersection.Top)
                    remaining.Add(DrawingRectangle.FromLTRB(piece.Left, piece.Top, piece.Right, intersection.Top));
                if (intersection.Bottom < piece.Bottom)
                    remaining.Add(DrawingRectangle.FromLTRB(piece.Left, intersection.Bottom, piece.Right, piece.Bottom));
                if (piece.Left < intersection.Left)
                    remaining.Add(DrawingRectangle.FromLTRB(piece.Left, intersection.Top, intersection.Left, intersection.Bottom));
                if (intersection.Right < piece.Right)
                    remaining.Add(DrawingRectangle.FromLTRB(intersection.Right, intersection.Top, piece.Right, intersection.Bottom));
            }
            uncovered = remaining;
            if (uncovered.Count == 0) return true;
        }
        return false;
    }

    internal static void FlushDesktopChanges() => Marshal.ThrowExceptionForHR(DwmFlush());

    private static bool TryGetWindow(IntPtr window, out CaptureWindow item)
    {
        item = null!;
        if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)
            || (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            return false;

        // DWM frame bounds are physical pixels and exclude invisible resize borders.
        if (DwmGetWindowAttribute(window, 9, out NativeRectangle rectangle,
                Marshal.SizeOf<NativeRectangle>()) != 0 && !GetWindowRect(window, out rectangle))
            return false;
        var bounds = rectangle.ToRectangle();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        GetWindowThreadProcessId(window, out var processId);
        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        var shellSurface = className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd" or "tooltips_class32";
        item = new CaptureWindow(window, processId, bounds, title.ToString(),
            processId != 0 && processId != Environment.ProcessId && !shellSurface);
        return true;
    }

    internal static void PositionOverlay(IntPtr window, DrawingRectangle bounds)
    {
        if (!SetWindowPos(window, Topmost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NoActivate | NoOwnerZOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly DrawingRectangle ToRectangle() =>
            DrawingRectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    private delegate bool MonitorEnumeration(IntPtr monitor, IntPtr dc, ref NativeRectangle rectangle, IntPtr data);
    private delegate bool WindowEnumeration(IntPtr window, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowEnumeration callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute,
        out NativeRectangle value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumeration callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation information);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out NativePoint position);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr handle);
}

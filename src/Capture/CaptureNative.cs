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

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute,
        out NativeRectangle value, int size);

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

using System.ComponentModel;
using System.Runtime.InteropServices;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Capture;

internal sealed record CaptureMonitor(DrawingRectangle Bounds, bool IsPrimary);

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
            throw new InvalidOperationException("目前找不到可擷取的螢幕。");

        return monitors;
    }

    internal static void CopyDesktopPixels(IntPtr destination, DrawingRectangle bounds)
    {
        var desktop = GetDC(IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new Win32Exception("無法取得桌面的繪圖內容。");
        try
        {
            const uint sourceCopyWithLayeredWindows = 0x40CC0020;
            if (!BitBlt(destination, 0, 0, bounds.Width, bounds.Height, desktop,
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

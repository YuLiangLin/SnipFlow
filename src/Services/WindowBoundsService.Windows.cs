using System.Runtime.InteropServices;

namespace SnipFlow.Services;

public static partial class WindowBoundsService
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;
    static readonly HashSet<Window> Attached = new();

    static partial void AttachNative(Window window)
    {
        if (!Attached.Add(window)) return;
        window.Closed += (_, _) => Attached.Remove(window);
        Win32Properties.AddWndProcHookCallback(window, WindowProc);
    }

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;
        if (!TryGetMonitorInfo(window, out var info)) return IntPtr.Zero;

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Both RECTs and MINMAXINFO use device pixels, including on monitors with different DPI.
        // Match the usable monitor area so a frameless window cannot cover the taskbar when maximized.
        bounds.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        bounds.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        Marshal.StructureToPtr(bounds, lParam, false);
        // Let Avalonia still apply MinWidth/MinHeight tracking constraints.
        handled = false;
        return IntPtr.Zero;
    }

    private static bool TryGetMonitorInfo(IntPtr window, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
        public NativePoint(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

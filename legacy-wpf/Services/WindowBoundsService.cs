using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SnipFlow.Services;

public static class WindowBoundsService
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached", typeof(bool), typeof(WindowBoundsService), new PropertyMetadata(false));

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.VerifyAccess();
        if ((bool)window.GetValue(IsAttachedProperty)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized -= SourceInitialized;
            window.SourceInitialized += SourceInitialized;
            return;
        }

        var source = HwndSource.FromHwnd(handle);
        if (source == null) return;
        source.AddHook(WindowProc);
        window.SetValue(IsAttachedProperty, true);
    }

    private static void SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= SourceInitialized;
        Attach(window);
    }

    public static Rect GetWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.VerifyAccess();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var source = HwndSource.FromHwnd(handle);
        if (source == null || source.IsDisposed || source.CompositionTarget == null || !TryGetMonitorInfo(handle, out var info))
            return SystemParameters.WorkArea;

        var area = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        area.Transform(source.CompositionTarget.TransformFromDevice);
        return area;
    }

    private static IntPtr WindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;
        if (!TryGetMonitorInfo(window, out var info)) return IntPtr.Zero;

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Both RECTs and MINMAXINFO use device pixels, including on monitors with different DPI.
        // Match the usable monitor area so WindowChrome cannot clip the bottom row behind the taskbar.
        bounds.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        bounds.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        Marshal.StructureToPtr(bounds, lParam, false);
        // WPF still applies its MinWidth/MinHeight tracking constraints after this hook.
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

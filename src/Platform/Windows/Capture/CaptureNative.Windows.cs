using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SnipFlow.Capture;

/// <summary>Win32 / DWM / GDI implementation of <see cref="CaptureNative"/>.</summary>
internal static partial class CaptureNative
{
    private const uint MonitorInfoPrimary = 1;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private static readonly IntPtr Topmost = new(-1);

    internal static partial IReadOnlyList<CaptureMonitor> GetMonitors()
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
        GC.KeepAlive(callback);
        if (enumerationError != 0)
            throw new Win32Exception(enumerationError);
        if (!succeeded)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (monitors.Count == 0)
            throw new InvalidOperationException(I18n.T("目前找不到可擷取的螢幕。"));

        return monitors;
    }

    internal static partial PixelBuffer CopyDesktop(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        // A top-down 32-bit DIB section receives the GDI copies; its zeroed memory keeps
        // virtual-desktop gaps black instead of depending on undefined desktop DC pixels.
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = bounds.Width,
            Height = -bounds.Height,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };
        var memory = CreateCompatibleDC(IntPtr.Zero);
        if (memory == IntPtr.Zero)
            throw new Win32Exception(I18n.T("無法取得桌面的繪圖內容。"));
        var section = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            section = CreateDIBSection(memory, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (section == IntPtr.Zero || bits == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, section);

            // Copy each physical display separately.
            foreach (var monitor in monitors)
            {
                var intersection = CaptureGeometry.Intersect(bounds, monitor.Bounds);
                if (!CaptureGeometry.IsEmpty(intersection))
                    CopyDesktopPixels(memory, intersection, intersection.X - bounds.X, intersection.Y - bounds.Y);
            }
            GdiFlush();

            var buffer = new PixelBuffer(bounds.Width, bounds.Height);
            PixelBuffer.CopyRows(bits, bounds.Width * 4, buffer.Pixels, buffer.Stride, bounds.Height);
            // GDI leaves the alpha byte undefined for 32-bit RGB; the desktop is opaque.
            SetOpaque(buffer.Pixels);
            return buffer;
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SelectObject(memory, previous);
            if (section != IntPtr.Zero)
                DeleteObject(section);
            DeleteDC(memory);
        }
    }

    internal static partial Task<PixelBuffer> CopyDesktopAsync(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors) =>
        Task.FromResult(CopyDesktop(bounds, monitors));

    private static void CopyDesktopPixels(IntPtr destination, PixelRect bounds,
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

    private static void SetOpaque(byte[] pixels)
    {
        var values = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        for (var index = 0; index < values.Length; index++)
            values[index] |= 0xFF000000;
    }

    internal static partial PixelPoint CursorPosition()
    {
        if (!GetPhysicalCursorPos(out var position))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new PixelPoint(position.X, position.Y);
    }

    internal static partial IReadOnlyList<CaptureWindow> GetWindows()
    {
        var windows = new List<CaptureWindow>();
        WindowEnumeration callback = (window, _) =>
        {
            if (TryGetWindow(window, out var item))
                windows.Add(item);
            return true;
        };
        var succeeded = EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (!succeeded)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return windows;
    }

    internal static partial bool TryGetWindowTarget(nint window, out WindowCaptureTarget target)
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
        var clientBounds = CaptureGeometry.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
            Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
        var windowBounds = frame.ToRectangle();
        var dpi = GetDpiForWindow(window);
        if (dpi == 0 || clientBounds.Width <= 0 || clientBounds.Height <= 0
            || !CaptureGeometry.Contains(windowBounds, clientBounds))
            return false;
        target = new WindowCaptureTarget(window, selected.ProcessId, windowBounds, clientBounds, dpi);
        return true;
    }

    internal static partial bool ActivateSelectedWindow(WindowCaptureTarget target)
    {
        if (!IsCurrentTarget(target)) return false;
        if (GetForegroundWindow() == target.Handle) return true;
        // Called only after a user picks this exact window. Do not attach input queues,
        // synthesize input, restore minimized windows or activate any replacement HWND.
        SetForegroundWindow(target.Handle);
        return IsCurrentTarget(target) && GetForegroundWindow() == target.Handle;
    }

    internal static partial nint GetForegroundWindow() => GetAncestor(NativeGetForegroundWindow(), 2);

    internal static partial bool IsCaptureProtected(nint window) =>
        GetWindowDisplayAffinity(window, out var affinity) && affinity != 0;

    internal static partial bool TryExcludeFromCapture(nint window) => SetWindowDisplayAffinity(window, 0x11)
        && GetWindowDisplayAffinity(window, out var affinity) && affinity == 0x11;

    internal static partial void FlushDesktopChanges() => Marshal.ThrowExceptionForHR(DwmFlush());

    internal static partial IReadOnlyList<CaptureWindowLayer>? GetWindowsAbove(nint window,
        IReadOnlyCollection<nint>? excludedWindows)
    {
        var layers = new List<CaptureWindowLayer>();
        var seen = new HashSet<IntPtr> { window };
        for (var above = GetWindow(window, 3); above != IntPtr.Zero; above = GetWindow(above, 3))
        {
            if (seen.Count >= 4096 || !seen.Add(above))
                return null;
            if (excludedWindows?.Contains(above) == true || !IsWindowVisible(above) || IsIconic(above)
                || IsCloaked(above))
                continue;
            if (!GetWindowRect(above, out var covering))
                return null;
            layers.Add(new CaptureWindowLayer(above, covering.ToRectangle()));
        }
        return layers;
    }

    internal static partial bool IsWindowAlive(nint window) =>
        window != IntPtr.Zero && IsWindow(window) && IsWindowVisible(window) && !IsIconic(window) && !IsCloaked(window);

    internal static partial PixelRect? GetWindowBounds(nint window) =>
        GetWindowRect(window, out var rectangle) ? rectangle.ToRectangle() : null;

    internal static partial PixelRect? GetClientBounds(nint window)
    {
        if (!GetClientRect(window, out var rectangle)) return null;
        var first = new NativePoint { X = rectangle.Left, Y = rectangle.Top };
        var last = new NativePoint { X = rectangle.Right, Y = rectangle.Bottom };
        if (!ClientToScreen(window, ref first) || !ClientToScreen(window, ref last)) return null;
        var bounds = CaptureGeometry.FromLTRB(first.X, first.Y, last.X, last.Y);
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    internal static partial uint GetWindowProcessId(nint window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    internal static partial uint GetWindowDpi(nint window) => GetDpiForWindow(window);

    internal static partial nint GetWindowAt(PixelPoint point) =>
        GetAncestor(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), 2); // GA_ROOT.

    internal static partial bool IsPointerButtonDown() => (GetAsyncKeyState(1) & 0x8000) != 0
        || (GetAsyncKeyState(2) & 0x8000) != 0 || (GetAsyncKeyState(4) & 0x8000) != 0;

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    internal static partial bool TryGetWindow(nint window, out CaptureWindow item)
    {
        item = null!;
        if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window) || IsCloaked(window))
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

    internal static partial void PositionOverlay(Window window, PixelRect bounds)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            throw new Win32Exception(1400); // ERROR_INVALID_WINDOW_HANDLE: the window is not created yet.
        if (!SetWindowPos(handle, Topmost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
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

        public readonly PixelRect ToRectangle() => CaptureGeometry.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
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

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativeGetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

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
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr handle);
}

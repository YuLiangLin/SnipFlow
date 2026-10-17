using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SnipFlow.Recording;

/// <summary>
/// Windows border: solid, click-through, no-activate popup windows on the inner edge of the recorded rectangle,
/// excluded from capture with WDA_EXCLUDEFROMCAPTURE so they never appear in the video. They are plain Win32
/// windows (not Avalonia windows, whose composition surfaces cannot be colour-keyed or made click-through
/// across processes), owned by the toolbar and handled by this thread's message loop.
/// </summary>
internal sealed partial class RecordingRegionHighlight
{
    private const uint ExcludeFromCapture = 0x11;

    private static bool AllowsEmptyBorder => false;

    // Older Windows treats 0x11 as WDA_MONITOR, which could leave a black border in video.
    private static bool CanShowBorder() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && IsCompositionEnabled();

    private static bool IsCompositionEnabled() => DwmIsCompositionEnabled(out var enabled) >= 0 && enabled;

    private IEnumerable<PixelRect> BorderPieces(PixelRect monitor)
    {
        var pieces = new List<PixelRect>();
        var viewport = SnipFlow.Capture.CaptureGeometry.Intersect(_bounds, monitor);
        if (SnipFlow.Capture.CaptureGeometry.IsEmpty(viewport))
            return pieces;
        // Clip the ORIGINAL four edges, never stroke each monitor's intersection rectangle.
        foreach (var edge in Edges())
        {
            var visible = SnipFlow.Capture.CaptureGeometry.Intersect(edge, viewport);
            if (!SnipFlow.Capture.CaptureGeometry.IsEmpty(visible))
                pieces.Add(visible);
        }
        return pieces;
    }

    // Topmost windows stay above normal windows; nothing to refresh.
    private void KeepOnTop() { }

    private void ReleasePlatform() { }

    private sealed unsafe class BorderWindow
    {
        private const int ExtendedStyleIndex = -20;
        private const long Layered = 0x00080000;
        private const long Transparent = 0x00000020;
        private const long NoActivate = 0x08000000;
        private const long ToolWindow = 0x00000080;
        private const long TopmostStyle = 0x00000008;
        private const long AppWindow = 0x00040000;
        private const long RequiredStyles = Layered | Transparent | NoActivate | ToolWindow;
        private const uint PopupStyle = 0x80000000;
        private const uint AlphaFlag = 0x00000002; // LWA_ALPHA
        private static readonly Dictionary<IntPtr, BorderWindow> Overlays = new();
        private static readonly IntPtr ReadyBrush = CreateSolidBrush(ColorReference(ReadyColor));
        private static readonly IntPtr RecordingBrush = CreateSolidBrush(ColorReference(RecordingColor));
        private static IntPtr _className;
        private readonly RecordingRegionHighlight _highlight;
        private readonly PixelRect _physicalBounds;
        private readonly IntPtr _owner;
        private IntPtr _handle;
        private bool _positioning;
        private bool _positionQueued;
        private bool _closing;
        private bool _closed;

        internal BorderWindow(RecordingRegionHighlight highlight, Window owner, PixelRect bounds)
        {
            _highlight = highlight;
            _physicalBounds = bounds;
            _owner = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }

        internal bool IsVerified => !_closed && _handle != IntPtr.Zero && IsWindowVisible(_handle)
            && (GetWindowLongPtr(_handle, ExtendedStyleIndex).ToInt64() & RequiredStyles) == RequiredStyles
            && GetLayeredWindowAttributes(_handle, out _, out var alpha, out var flags)
            && (flags & AlphaFlag) != 0 && alpha == 255
            && GetWindowDisplayAffinity(_handle, out var affinity) && affinity == ExcludeFromCapture
            && GetWindowRect(_handle, out var rectangle)
            && rectangle.Left == _physicalBounds.X && rectangle.Top == _physicalBounds.Y
            && rectangle.Right == _physicalBounds.Right && rectangle.Bottom == _physicalBounds.Bottom;

        /// <summary>Creates the hidden window, then sets and verifies its capture exclusion.</summary>
        internal void Prepare()
        {
            var instance = GetModuleHandleW(IntPtr.Zero);
            _handle = CreateWindowExW((uint)(RequiredStyles | TopmostStyle), RegisterClass(instance), IntPtr.Zero, PopupStyle,
                _physicalBounds.X, _physicalBounds.Y, _physicalBounds.Width, _physicalBounds.Height,
                _owner, IntPtr.Zero, instance, IntPtr.Zero);
            if (_handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Overlays[_handle] = this;
            // Layered with full opacity: WS_EX_TRANSPARENT then passes input to other processes as well.
            if (!SetLayeredWindowAttributes(_handle, 0, 255, AlphaFlag))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetWindowDisplayAffinity(_handle, ExcludeFromCapture)
                || !GetWindowDisplayAffinity(_handle, out var affinity) || affinity != ExcludeFromCapture)
                throw new InvalidOperationException("Windows could not verify capture exclusion for the recording border.");
            PositionPhysical();
        }

        internal void ShowPrepared()
        {
            if (_handle == IntPtr.Zero || _closed)
                return;
            ShowWindow(_handle, 4); // SW_SHOWNOACTIVATE
            PositionPhysical();
        }

        internal void Refresh()
        {
            if (_handle != IntPtr.Zero && !_closed)
                InvalidateRect(_handle, IntPtr.Zero, true);
        }

        internal void CloseOverlay()
        {
            if (_closed)
                return;
            _closing = true;
            try
            {
                if (_handle != IntPtr.Zero && !DestroyWindow(_handle))
                {
                    LogSafely(new Win32Exception(Marshal.GetLastWin32Error()));
                    ShowWindow(_handle, 0); // Hide only this owned overlay if closing failed.
                }
            }
            catch (Exception exception) { LogSafely(exception); }
            Overlays.Remove(_handle);
            _closed = true;
            _handle = IntPtr.Zero;
        }

        private void PositionSafely()
        {
            if (_closed || _closing || _highlight._disposed || _highlight._invalid)
                return;
            try
            {
                PositionPhysical();
                if (_highlight._shown && !IsVerified)
                    _highlight.Invalidate();
            }
            catch (Exception exception) { _highlight.Invalidate(exception); }
        }

        private void PositionPhysical()
        {
            if (_handle == IntPtr.Zero || _closed || _positioning)
                return;
            _positioning = true;
            try
            {
                // HWND_TOPMOST; SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED.
                if (!SetWindowPos(_handle, new IntPtr(-1), _physicalBounds.X, _physicalBounds.Y,
                        _physicalBounds.Width, _physicalBounds.Height, 0x0010 | 0x0200 | 0x0020))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Refresh();
            }
            finally { _positioning = false; }
        }

        private IntPtr WindowMessage(uint message, IntPtr wParam, IntPtr lParam, out bool handled)
        {
            handled = false;
            switch (message)
            {
                case 0x007C when unchecked((int)wParam.ToInt64()) == ExtendedStyleIndex && lParam != IntPtr.Zero:
                {
                    // WM_STYLECHANGING: nothing may remove the layered/click-through/no-activate styles.
                    // STYLESTRUCT is two DWORDs on both x86 and x64, not two pointer-sized values.
                    var styles = (uint*)lParam;
                    styles[1] = (styles[1] | (uint)RequiredStyles) & ~(uint)AppWindow;
                    handled = true;
                    return IntPtr.Zero;
                }
                case 0x0084: // WM_NCHITTEST; layered + WS_EX_TRANSPARENT also passes input across processes.
                    handled = true;
                    return new IntPtr(-1); // HTTRANSPARENT
                case 0x0021: // WM_MOUSEACTIVATE
                    handled = true;
                    return new IntPtr(3); // MA_NOACTIVATE
                case 0x0014: // WM_ERASEBKGND: the WM_PAINT fill covers the whole window.
                    handled = true;
                    return new IntPtr(1);
                case 0x000F: // WM_PAINT
                {
                    var paint = stackalloc byte[72]; // PAINTSTRUCT
                    var context = BeginPaint(_handle, paint);
                    if (context != IntPtr.Zero && GetClientRect(_handle, out var client))
                        FillRect(context, ref client, _highlight._recording ? RecordingBrush : ReadyBrush);
                    EndPaint(_handle, paint);
                    handled = true;
                    return IntPtr.Zero;
                }
                case 0x007E or 0x031E: // WM_DISPLAYCHANGE / WM_DWMCOMPOSITIONCHANGED
                    _highlight.Invalidate();
                    break;
                case 0x0018 when wParam == IntPtr.Zero && !_closing && _highlight._shown:
                    _highlight.Invalidate(); // An unexpected hide removes the visible recording boundary.
                    break;
                case 0x02E0 or 0x0047 or 0x007D when !_positioning && !_positionQueued:
                    _positionQueued = true; // WM_DPICHANGED / WM_WINDOWPOSCHANGED / WM_STYLECHANGED
                    Dispatcher.UIThread.Post(() =>
                    {
                        _positionQueued = false;
                        PositionSafely();
                    }, DispatcherPriority.Loaded);
                    break;
                case 0x0082: // WM_NCDESTROY
                    Overlays.Remove(_handle);
                    var unexpected = !_closing && !_closed;
                    _closed = true;
                    _handle = IntPtr.Zero;
                    if (unexpected)
                        _highlight.Invalidate();
                    break;
            }
            return IntPtr.Zero;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (Overlays.TryGetValue(window, out var overlay))
                {
                    var result = overlay.WindowMessage(message, wParam, lParam, out var handled);
                    if (handled)
                        return result;
                }
            }
            catch (Exception exception)
            {
                // Exceptions must never cross the native window procedure.
                if (Overlays.TryGetValue(window, out var overlay))
                    overlay._highlight.Invalidate(exception);
                else
                    LogSafely(exception);
            }
            return DefWindowProcW(window, message, wParam, lParam);
        }

        private static IntPtr RegisterClass(IntPtr instance)
        {
            if (_className != IntPtr.Zero)
                return _className;
            var name = Marshal.StringToHGlobalUni("SnipFlowRecordingBorder");
            var windowClass = new WindowClass
            {
                Size = (uint)sizeof(WindowClass),
                Procedure = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WindowProcedure,
                Instance = instance,
                ClassName = name
            };
            if (RegisterClassExW(ref windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    Marshal.FreeHGlobal(name);
                    throw new Win32Exception(error);
                }
            }
            // The class lives as long as the process; keep its name allocated.
            _className = name;
            return name;
        }

        private static uint ColorReference(Color color) => color.R | ((uint)color.G << 8) | ((uint)color.B << 16);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowClass
        {
            public uint Size, Style;
            public IntPtr Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WindowClass windowClass);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint extendedStyle, IntPtr className, IntPtr windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandleW(IntPtr name);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colorKey, out byte alpha, out uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRectangle bounds);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRectangle bounds);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr window, byte* paint);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndPaint(IntPtr window, byte* paint);
        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr context, ref NativeRectangle rectangle, IntPtr brush);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint color);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}

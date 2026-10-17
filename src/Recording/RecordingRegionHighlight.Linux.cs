using System.Text;
using SnipFlow.Capture;
using SnipFlow.Platform.Linux.X11;

namespace SnipFlow.Recording;

/// <summary>
/// Linux border: X11 has no capture exclusion, so four thin solid windows frame the rectangle from OUTSIDE and
/// never appear in the recorded pixels. They are plain override-redirect windows on SnipFlow's own X connection
/// (also XWayland in Wayland sessions): the window manager does not move, focus, decorate or list them, their
/// input shape is empty so clicks pass through, and their window type keeps compositors from adding shadows
/// that could reach into the recorded rectangle. Parts outside every monitor (a rectangle that ends at a screen
/// edge) are simply not drawn.
/// </summary>
internal sealed partial class RecordingRegionHighlight
{
    private XDisplay? _display;
    private bool? _hasShape;
    private nuint? _readyPixel;
    private nuint? _recordingPixel;

    private static bool AllowsEmptyBorder => true;

    private bool CanShowBorder()
    {
        _display ??= XDisplay.TryOpen();
        return _display is not null;
    }

    private static bool IsCompositionEnabled() => true;

    private IEnumerable<PixelRect> BorderPieces(PixelRect monitor) => OuterEdges()
        .Select(edge => CaptureGeometry.Intersect(edge, monitor))
        .Where(piece => !CaptureGeometry.IsEmpty(piece))
        .ToList();

    /// <summary>Other windows raised above the border would hide it; raise it again (outside the recording, so harmless).</summary>
    private void KeepOnTop()
    {
        foreach (var overlay in _windows)
            overlay.Raise();
        if (_display is { Handle: var handle } && handle != IntPtr.Zero)
            XLib.XFlush(handle);
    }

    private void ReleasePlatform()
    {
        _display?.Dispose();
        _display = null;
    }

    private nuint BorderPixel(bool recording)
    {
        ref var cached = ref recording ? ref _recordingPixel : ref _readyPixel;
        if (cached is { } pixel)
            return pixel;
        var color = recording ? RecordingColor : ReadyColor;
        var display = _display!.Handle;
        var request = new XColor
        {
            Red = (ushort)(color.R * 257), Green = (ushort)(color.G * 257), Blue = (ushort)(color.B * 257), Flags = 7
        };
        // TrueColor visuals always succeed; the packed value is a fallback for unusual servers.
        var value = XLib.XAllocColor(display, XLib.XDefaultColormap(display, XLib.XDefaultScreen(display)), ref request) != 0
            ? request.Pixel
            : (nuint)((color.R << 16) | (color.G << 8) | color.B);
        cached = value;
        return value;
    }

    private bool HasShape()
    {
        if (_hasShape is { } known)
            return known;
        try { _hasShape = XLib.XShapeQueryExtension(_display!.Handle, out _, out _); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { _hasShape = false; }
        return _hasShape.Value;
    }

    private sealed class BorderWindow
    {
        private readonly RecordingRegionHighlight _highlight;
        private readonly PixelRect _physicalBounds;
        private nint _window;
        private bool _shownOnce;
        private bool _closed;

        internal BorderWindow(RecordingRegionHighlight highlight, Window owner, PixelRect bounds)
        {
            _highlight = highlight;
            _physicalBounds = bounds;
        }

        private IntPtr Display => _highlight._display?.Handle ?? IntPtr.Zero;

        internal bool IsVerified
        {
            get
            {
                var display = Display;
                if (_closed || _window == 0 || display == IntPtr.Zero)
                    return false;
                // Override-redirect top-level windows are children of the root, so x/y are desktop pixels.
                return XLib.XGetWindowAttributes(display, _window, out var attributes) != 0
                    && attributes.MapState == XLib.IsViewable && attributes.OverrideRedirect != 0
                    && attributes.X == _physicalBounds.X && attributes.Y == _physicalBounds.Y
                    && attributes.Width == _physicalBounds.Width && attributes.Height == _physicalBounds.Height;
            }
        }

        internal void Prepare()
        {
            var display = Display;
            if (display == IntPtr.Zero)
                throw new InvalidOperationException("The X display for the recording border is not available.");
            var attributes = new XSetWindowAttributes
            {
                BackgroundPixel = _highlight.BorderPixel(_highlight._recording),
                BorderPixel = 0,
                OverrideRedirect = 1,
                SaveUnder = 0,
                EventMask = 0
            };
            _window = XLib.XCreateWindow(display, XLib.XDefaultRootWindow(display), _physicalBounds.X, _physicalBounds.Y,
                (uint)_physicalBounds.Width, (uint)_physicalBounds.Height, 0, 0, XLib.InputOutput, IntPtr.Zero,
                XLib.CWBackPixel | XLib.CWBorderPixel | XLib.CWOverrideRedirect | XLib.CWSaveUnder | XLib.CWEventMask,
                ref attributes);
            if (_window == 0)
                throw new InvalidOperationException("The recording border window could not be created.");
            var name = Encoding.UTF8.GetBytes("SnipFlow recording frame");
            var display32 = _highlight._display!;
            XLib.XChangeProperty(display, _window, display32.Atom("_NET_WM_NAME"), display32.Atom("UTF8_STRING"), 8,
                XLib.PropModeReplace, name, name.Length);
            var windowClass = Encoding.ASCII.GetBytes("snipflow-recording\0SnipFlow\0");
            XLib.XChangeProperty(display, _window, display32.Atom("WM_CLASS"), XLib.XA_STRING, 8, XLib.PropModeReplace,
                windowClass, windowClass.Length);
            // Compositors add drop shadows to override-redirect menus; a shadow would reach into the recording.
            // Mutter and picom never shadow drag-and-drop windows.
            XLib.XChangeProperty(display, _window, display32.Atom("_NET_WM_WINDOW_TYPE"), XLib.XA_ATOM, 32,
                XLib.PropModeReplace, new[] { display32.Atom("_NET_WM_WINDOW_TYPE_DND") }, 1);
            XLib.XChangeProperty(display, _window, display32.Atom("_NET_WM_PID"), XLib.XA_CARDINAL, 32,
                XLib.PropModeReplace, new nint[] { Environment.ProcessId }, 1);
            // An empty input shape passes every click and scroll through to the windows below.
            if (_highlight.HasShape())
                XLib.XShapeCombineRectangles(display, _window, XLib.ShapeInput, 0, 0, IntPtr.Zero, 0, XLib.ShapeSet, XLib.Unsorted);
            XLib.XFlush(display);
        }

        internal void ShowPrepared()
        {
            var display = Display;
            if (_closed || _window == 0 || display == IntPtr.Zero)
                return;
            XLib.XMapRaised(display, _window);
            // Window managers never move override-redirect windows; position again in case the server clamped it.
            XLib.XMoveResizeWindow(display, _window, _physicalBounds.X, _physicalBounds.Y,
                (uint)_physicalBounds.Width, (uint)_physicalBounds.Height);
            XLib.XSync(display, false);
            _shownOnce = true;
        }

        internal void Refresh()
        {
            var display = Display;
            if (_closed || _window == 0 || display == IntPtr.Zero)
                return;
            XLib.XSetWindowBackground(display, _window, _highlight.BorderPixel(_highlight._recording));
            XLib.XClearWindow(display, _window);
            XLib.XFlush(display);
        }

        internal void Raise()
        {
            var display = Display;
            if (!_closed && _shownOnce && _window != 0 && display != IntPtr.Zero)
                XLib.XRaiseWindow(display, _window);
        }

        internal void CloseOverlay()
        {
            if (_closed)
                return;
            _closed = true;
            var display = Display;
            try
            {
                if (_window != 0 && display != IntPtr.Zero)
                {
                    XLib.XDestroyWindow(display, _window);
                    XLib.XFlush(display);
                }
            }
            catch (Exception exception) { LogSafely(exception); }
            _window = 0;
        }
    }
}

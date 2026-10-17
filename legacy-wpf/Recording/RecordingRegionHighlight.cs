using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Recording;

/// <summary>A physical-pixel recording border. All members are used on the owner's UI dispatcher.</summary>
internal sealed class RecordingRegionHighlight : IDisposable
{
    private const uint ExcludeFromCapture = 0x11;
    private readonly DrawingRectangle _bounds;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _verificationTimer;
    private readonly List<HighlightOverlay> _windows = new();
    private Window? _owner;
    private DrawingRectangle[] _monitorBounds = Array.Empty<DrawingRectangle>();
    private bool _shown;
    private bool _recording;
    private bool _invalid;
    private bool _invalidationRaised;
    private bool _disposed;
    private int _generation;

    internal event EventHandler? Invalidated;

    internal RecordingRegionHighlight(DrawingRectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > int.MaxValue
            || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        _bounds = bounds;
        _verificationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _verificationTimer.Tick += VerifyOverlays;
    }

    internal bool IsExcludedFromCapture
    {
        get
        {
            _dispatcher.VerifyAccess();
            if (_disposed || !_shown || _invalid || _windows.Count == 0)
                return false;
            try
            {
                if (_monitorBounds.SequenceEqual(GetMonitorBounds())
                    && IsCompositionEnabled() && _windows.All(window => window.IsVerified))
                    return true;
                Invalidate();
            }
            catch (Exception exception) { Invalidate(exception); }
            return false;
        }
    }

    internal void Show(Window owner)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Dispatcher != _dispatcher || new WindowInteropHelper(owner).Handle == IntPtr.Zero)
            throw new InvalidOperationException("The recording owner must have an initialized window on this dispatcher.");
        _dispatcher.ShutdownStarted -= OnDispatcherShutdown;
        _dispatcher.ShutdownStarted += OnDispatcherShutdown;
        if (_shown && ReferenceEquals(_owner, owner) && IsExcludedFromCapture)
            return;

        Hide();
        if (_owner is not null)
            _owner.Closed -= OnOwnerClosed;
        _owner = owner;
        _owner.Closed += OnOwnerClosed;
        _invalid = false;
        _invalidationRaised = false;
        try
        {
            // Older Windows treats 0x11 as WDA_MONITOR, which could leave a black border in video.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !IsCompositionEnabled())
                return;
            _monitorBounds = GetMonitorBounds();
            foreach (var monitor in _monitorBounds)
            {
                var viewport = DrawingRectangle.Intersect(_bounds, monitor);
                if (viewport.Width <= 0 || viewport.Height <= 0
                    || !Edges().Any(edge => edge.IntersectsWith(viewport)))
                    continue;
                var overlay = new HighlightOverlay(this, owner, viewport);
                _windows.Add(overlay);
                // Create and verify every HWND while hidden, before any border is displayed.
                overlay.Prepare();
            }
            if (_windows.Count == 0)
                return;
            foreach (var overlay in _windows)
                overlay.ShowPrepared();
            if (_invalid || !_monitorBounds.SequenceEqual(GetMonitorBounds())
                || !IsCompositionEnabled() || !_windows.All(window => window.IsVerified))
                throw new InvalidOperationException("The recording border could not be excluded from capture or positioned correctly.");
            _shown = true;
            _verificationTimer.Start();
        }
        catch (Exception exception)
        {
            LogSafely(exception);
            _invalid = true;
            CloseOverlays();
        }
    }

    internal void Hide()
    {
        _dispatcher.VerifyAccess();
        _generation++;
        _shown = false;
        _verificationTimer.Stop();
        CloseOverlays();
    }

    internal void SetRecording(bool recording)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _recording == recording)
            return;
        _recording = recording;
        foreach (var overlay in _windows)
            overlay.Refresh();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        _generation++;
        _shown = false;
        _verificationTimer.Stop();
        _verificationTimer.Tick -= VerifyOverlays;
        _dispatcher.ShutdownStarted -= OnDispatcherShutdown;
        if (_owner is not null)
            _owner.Closed -= OnOwnerClosed;
        _owner = null;
        CloseOverlays();
    }

    private IEnumerable<DrawingRectangle> Edges()
    {
        var horizontal = Math.Min(2, _bounds.Height);
        var vertical = Math.Min(2, _bounds.Width);
        yield return new(_bounds.Left, _bounds.Top, _bounds.Width, horizontal);
        yield return new(_bounds.Left, _bounds.Bottom - horizontal, _bounds.Width, horizontal);
        yield return new(_bounds.Left, _bounds.Top, vertical, _bounds.Height);
        yield return new(_bounds.Right - vertical, _bounds.Top, vertical, _bounds.Height);
    }

    private static DrawingRectangle[] GetMonitorBounds() => System.Windows.Forms.Screen.AllScreens
        .Select(screen => screen.Bounds).Distinct().OrderBy(bounds => bounds.Left).ThenBy(bounds => bounds.Top)
        .ThenBy(bounds => bounds.Width).ThenBy(bounds => bounds.Height).ToArray();

    private static bool IsCompositionEnabled() => DwmIsCompositionEnabled(out var enabled) >= 0 && enabled;

    private void OnOwnerClosed(object? sender, EventArgs args) => Dispose();
    private void OnDispatcherShutdown(object? sender, EventArgs args) => Dispose();
    private void VerifyOverlays(object? sender, EventArgs args) => _ = IsExcludedFromCapture;

    private void Invalidate(Exception? exception = null)
    {
        if (exception is not null)
            LogSafely(exception);
        if (_disposed || _invalid)
            return;
        _invalid = true;
        _verificationTimer.Stop();
        var notify = _shown;
        _shown = false;
        var generation = _generation;
        // Native hooks must return before closing their HWNDs or notifying recording code.
        try
        {
            if (!_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    if (_disposed || generation != _generation)
                        return;
                    CloseOverlays();
                    if (!notify || _disposed || _invalidationRaised)
                        return;
                    _invalidationRaised = true;
                    if (Invalidated is { } handlers)
                        foreach (EventHandler handler in handlers.GetInvocationList())
                            try { handler(this, EventArgs.Empty); }
                            catch (Exception error) { LogSafely(error); }
                }));
        }
        catch (Exception error) { LogSafely(error); }
    }

    private void CloseOverlays()
    {
        var windows = _windows.ToArray();
        _windows.Clear();
        foreach (var overlay in windows)
            overlay.CloseOverlay();
    }

    private static void LogSafely(Exception exception)
    {
        try { SettingsStore.Log(exception); }
        catch (Exception) { } // Logging must not escape a native window hook or prevent cleanup.
    }

    private sealed class HighlightOverlay : Window
    {
        private const long Layered = 0x00080000;
        private const long Transparent = 0x00000020;
        private const long NoActivate = 0x08000000;
        private const long ToolWindow = 0x00000080;
        private const long RequiredStyles = Layered | Transparent | NoActivate | ToolWindow;
        private const uint TransparencyColor = 0x00FF00FF; // RGB(255, 0, 255) in Win32 COLORREF order.
        private const uint ColorKeyFlag = 0x00000001;
        private static readonly Brush ColorKeyBrush = MakeBrush(Color.FromRgb(255, 0, 255));
        private static readonly Brush ReadyBrush = MakeBrush(Color.FromRgb(96, 213, 196));
        private static readonly Brush RecordingBrush = MakeBrush(Color.FromRgb(255, 92, 104));
        private readonly RecordingRegionHighlight _highlight;
        private readonly DrawingRectangle _physicalBounds;
        private readonly BorderSurface _surface;
        private HwndSource? _source;
        private IntPtr _handle;
        private bool _positioning;
        private bool _positionQueued;
        private bool _closing;
        private bool _closed;

        internal HighlightOverlay(RecordingRegionHighlight highlight, Window owner, DrawingRectangle bounds)
        {
            _highlight = highlight;
            _physicalBounds = bounds;
            _surface = new BorderSurface(this);
            Owner = owner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // Use redirected color-key transparency, not WPF's UpdateLayeredWindow per-pixel path.
            AllowsTransparency = false;
            Background = ColorKeyBrush;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            Width = Math.Max(1, bounds.Width);
            Height = Math.Max(1, bounds.Height);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Content = _surface;
            RenderOptions.SetEdgeMode(_surface, EdgeMode.Aliased);
            SourceInitialized += InitializeSource;
            ContentRendered += (_, _) => PositionSafely();
            Closed += OnClosed;
        }

        internal bool IsVerified => !_closed && _handle != IntPtr.Zero && IsWindowVisible(_handle)
            && (GetWindowLongPtr(_handle, -20).ToInt64() & RequiredStyles) == RequiredStyles
            && GetLayeredWindowAttributes(_handle, out var colorKey, out var alpha, out var flags)
            && colorKey == TransparencyColor && (flags & ColorKeyFlag) != 0
            && ((flags & 0x00000002) == 0 || alpha == 255)
            && GetWindowDisplayAffinity(_handle, out var affinity) && affinity == ExcludeFromCapture
            && GetWindowRect(_handle, out var rectangle)
            && rectangle.Left == _physicalBounds.Left && rectangle.Top == _physicalBounds.Top
            && rectangle.Right == _physicalBounds.Right && rectangle.Bottom == _physicalBounds.Bottom;

        internal void Prepare() => new WindowInteropHelper(this).EnsureHandle();

        internal void ShowPrepared()
        {
            Show();
            PositionPhysical();
        }

        internal void Refresh() => _surface.InvalidateVisual();

        private void InitializeSource(object? sender, EventArgs args)
        {
            _handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_handle)
                ?? throw new InvalidOperationException("The recording border HWND has no WPF source.");
            _source.AddHook(WindowMessage);
            var styles = (GetWindowLongPtr(_handle, -20).ToInt64() | RequiredStyles) & ~0x00040000L;
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLongPtr(_handle, -20, new IntPtr(styles)) == IntPtr.Zero
                && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            ConfigureColorKey();
            if (!SetWindowDisplayAffinity(_handle, ExcludeFromCapture)
                || !GetWindowDisplayAffinity(_handle, out var affinity) || affinity != ExcludeFromCapture)
                throw new InvalidOperationException("Windows could not verify capture exclusion for the recording border.");
            PositionPhysical();
        }

        private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (message == 0x007C && unchecked((int)wParam.ToInt64()) == -20 && lParam != IntPtr.Zero)
                {
                    // WM_STYLECHANGING: WPF must not remove layered/click-through/no-activate styles.
                    // STYLESTRUCT is two DWORDs on both x86 and x64, not two pointer-sized values.
                    var styles = Marshal.PtrToStructure<NativeStyles>(lParam);
                    styles.New = (styles.New | (uint)RequiredStyles) & ~0x00040000U;
                    Marshal.StructureToPtr(styles, lParam, false);
                    handled = true;
                    return IntPtr.Zero;
                }
                if (message == 0x0084) // WM_NCHITTEST; layered + WS_EX_TRANSPARENT also passes input across processes.
                {
                    handled = true;
                    return new IntPtr(-1); // HTTRANSPARENT
                }
                if (message == 0x0021) // WM_MOUSEACTIVATE
                {
                    handled = true;
                    return new IntPtr(3); // MA_NOACTIVATE
                }
                if (message is 0x007E or 0x031E) // WM_DISPLAYCHANGE / WM_DWMCOMPOSITIONCHANGED
                    _highlight.Invalidate();
                else if (message == 0x0018 && wParam == IntPtr.Zero && !_closing && _highlight._shown)
                    _highlight.Invalidate(); // An unexpected hide removes the visible recording boundary.
                else if ((message is 0x02E0 or 0x0047 or 0x007D) && !_positioning && !_positionQueued)
                {
                    _positionQueued = true; // WM_DPICHANGED / WM_WINDOWPOSCHANGED / WM_STYLECHANGED
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        _positionQueued = false;
                        PositionSafely();
                    }));
                }
            }
            catch (Exception exception) { _highlight.Invalidate(exception); }
            return IntPtr.Zero;
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
                if (!SetWindowPos(_handle, new IntPtr(-1), _physicalBounds.Left, _physicalBounds.Top,
                        _physicalBounds.Width, _physicalBounds.Height, 0x0010 | 0x0200 | 0x0020))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                ConfigureColorKey();
                Refresh();
            }
            finally { _positioning = false; }
        }

        private void ConfigureColorKey()
        {
            if (!SetLayeredWindowAttributes(_handle, TransparencyColor, 255, ColorKeyFlag))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal void CloseOverlay()
        {
            if (_closed)
                return;
            _closing = true;
            try { Close(); }
            catch (Exception exception)
            {
                LogSafely(exception);
                try
                {
                    if (_handle != IntPtr.Zero)
                        ShowWindow(_handle, 0); // Hide only this owned overlay if WPF closure failed.
                    _source?.Dispose();
                }
                catch (Exception error) { LogSafely(error); }
                _closed = true;
                _source = null;
                _handle = IntPtr.Zero;
            }
        }

        private void OnClosed(object? sender, EventArgs args)
        {
            _closed = true;
            try { _source?.RemoveHook(WindowMessage); }
            catch (Exception exception) { LogSafely(exception); }
            _source = null;
            _handle = IntPtr.Zero;
            if (!_closing)
                _highlight.Invalidate();
        }

        private void DrawBorder(DrawingContext drawing)
        {
            if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0)
                return;
            var scaleX = _surface.ActualWidth / _physicalBounds.Width;
            var scaleY = _surface.ActualHeight / _physicalBounds.Height;
            var brush = _highlight._recording ? RecordingBrush : ReadyBrush;
            var viewport = new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight);
            drawing.PushClip(new RectangleGeometry(viewport));
            drawing.DrawRectangle(ColorKeyBrush, null, viewport);
            // Clip the ORIGINAL four edges, never stroke each monitor's intersection rectangle.
            foreach (var edge in _highlight.Edges())
            {
                var visible = DrawingRectangle.Intersect(edge, _physicalBounds);
                if (visible.Width > 0 && visible.Height > 0)
                    drawing.DrawRectangle(brush, null, new Rect((visible.Left - _physicalBounds.Left) * scaleX,
                        (visible.Top - _physicalBounds.Top) * scaleY, visible.Width * scaleX, visible.Height * scaleY));
            }
            drawing.Pop();
        }

        private static Brush MakeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private sealed class BorderSurface(HighlightOverlay owner) : FrameworkElement
        {
            protected override void OnRender(DrawingContext drawing)
            {
                base.OnRender(drawing);
                owner.DrawBorder(drawing);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStyles { public uint Old, New; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
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
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}

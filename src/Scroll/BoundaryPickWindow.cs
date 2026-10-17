using System.ComponentModel;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Media.Immutable;
using SnipFlow.Capture;
using SnipFlow.Services;

namespace SnipFlow.Scroll;

/// <summary>Selects a physical pixel boundary on an already captured region.</summary>
/// <remarks>
/// The snapshot normally has exactly the region's pixels. Wayland captures keep the compositor
/// resolution, which can exceed the desktop rectangle on scaled desktops; rows are then mapped by the
/// ratio, and <see cref="SelectedPixel"/> always counts snapshot rows.
/// </remarks>
public sealed class BoundaryPickWindow : DialogWindow
{
    private static readonly IBrush MaskBrush = new ImmutableSolidColorBrush(Color.FromArgb(142, 16, 16, 16));
    private static IBrush PanelBrush => ThemeService.Brush("OverlayPanel");
    private static IBrush AccentBrush => ThemeService.Brush("SelectionHandle");
    private static IBrush MutedBrush => ThemeService.Brush("OverlayMuted");
    // Focus can bounce through a brief unfocused state while X11 window managers map the window.
    private static readonly TimeSpan DeactivationGrace = PlatformInfo.IsWindows ? TimeSpan.Zero : TimeSpan.FromMilliseconds(400);
    private readonly PixelRect _physicalBounds;
    private readonly Bitmap _snapshot;
    private readonly bool _isStart;
    private readonly int _maximumPixel;
    private readonly BoundarySurface _surface;
    private nint _handle;
    private int _pixel;
    private bool _positioning;
    private bool _wasActivated;
    private bool _closing;
    private bool _confirmed;
    private bool _closed;

    /// <summary>Start row [0, H - 1], or exclusive end boundary [0, H]; null on cancellation.</summary>
    public int? SelectedPixel { get; private set; }

    public BoundaryPickWindow(PixelRect physicalBounds, Bitmap frozenSnapshot,
        bool isStart, int? initialPixel = null, bool fromCollectedFrame = false)
    {
        ArgumentNullException.ThrowIfNull(frozenSnapshot);
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
            throw new ArgumentException("The selected region must have positive dimensions.", nameof(physicalBounds));
        if (!PlatformInfo.IsWayland && frozenSnapshot.PixelSize != physicalBounds.Size)
            throw new ArgumentException("The snapshot must match the selected region in physical pixels.", nameof(frozenSnapshot));

        _physicalBounds = physicalBounds;
        _snapshot = frozenSnapshot;
        _isStart = isStart;
        var height = frozenSnapshot.PixelSize.Height;
        _maximumPixel = isStart ? height - 1 : height;
        _pixel = Math.Clamp(initialPixel ?? height / 2, 0, _maximumPixel);
        _surface = new BoundarySurface(this) { Focusable = true };

        Title = I18n.T(fromCollectedFrame ? (isStart ? "從已收集首頁選起點" : "從已收集末頁選終點")
            : isStart ? "點選第一則訊息的上緣" : "點選最後一則訊息的下緣");
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        UseLayoutRounding = true;
        Content = _surface;
        var exact = frozenSnapshot.PixelSize == physicalBounds.Size;
        RenderOptions.SetBitmapInterpolationMode(this, exact ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        // Physical placement before the first map; Windows corrects it with SetWindowPos once the window exists.
        if (PlatformInfo.IsWindows)
        {
            var scaling = Screens.ScreenFromBounds(physicalBounds)?.Scaling is > 0 and var value ? value : 1;
            Position = physicalBounds.Position;
            Width = Math.Max(1, physicalBounds.Width / scaling);
            Height = Math.Max(1, physicalBounds.Height / scaling);
        }
        else
            CaptureNative.PositionOverlay(this, physicalBounds);
        Opened += OnOpened;
        Activated += (_, _) => _wasActivated = true;
        Deactivated += OnDeactivated;
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPreviewPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ThemeService.Changed += OnThemeChanged;
    }

    private void OnOpened(object? sender, EventArgs args)
    {
        _handle = TryGetPlatformHandle()?.Handle ?? 0;
        AttachDisplayHooks();
        PositionPhysical();
        // WPF ContentRendered: position again once the first frame is shown, then take keyboard focus.
        Dispatcher.UIThread.Post(() =>
        {
            PositionPhysical();
            if (_closing || _closed)
                return;
            // X11 window managers may not focus a new window on their own (focus stealing prevention);
            // the keys (arrows, Enter, Esc) need it, as WPF's ShowActivated gave it.
            if (!IsActive)
                Activate();
            _surface.Focus();
        }, DispatcherPriority.Background);
    }

#if WINDOWS
    private void AttachDisplayHooks() => Win32Properties.AddWndProcHookCallback(this, WindowMessage);

    private void DetachDisplayHooks() => Win32Properties.RemoveWndProcHookCallback(this, WindowMessage);

    private IntPtr WindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x02E0) // WM_DPICHANGED: Avalonia updates its scale before physical bounds are restored.
            Dispatcher.UIThread.Post(PositionPhysical, DispatcherPriority.Loaded);
        if (message == 0x007E) // WM_DISPLAYCHANGE: the frozen snapshot no longer matches the desktop.
            Dispatcher.UIThread.Post(CancelSelection, DispatcherPriority.Input);
        return IntPtr.Zero;
    }
#else
    private void AttachDisplayHooks() => Screens.Changed += OnScreensChanged;

    private void DetachDisplayHooks() => Screens.Changed -= OnScreensChanged;

    // XRandR change: the frozen snapshot no longer matches the desktop.
    private void OnScreensChanged(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(CancelSelection, DispatcherPriority.Input);
#endif

    private void PositionPhysical()
    {
        if (_handle == 0 || _positioning || _closing || _closed)
            return;
        _positioning = true;
        try
        {
            CaptureNative.PositionOverlay(this, _physicalBounds);
        }
        catch (Win32Exception)
        {
            Dispatcher.UIThread.Post(CancelSelection, DispatcherPriority.Input);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void OnDeactivated(object? sender, EventArgs args)
    {
        if (!_wasActivated || _closing || _closed)
            return;
        void CancelIfInactive()
        {
            if (!IsActive)
                CancelSelection();
        }
        if (DeactivationGrace == TimeSpan.Zero)
            Dispatcher.UIThread.Post(CancelIfInactive, DispatcherPriority.Input);
        else
            DispatcherTimer.RunOnce(CancelIfInactive, DeactivationGrace, DispatcherPriority.Input);
    }

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        _closing = true;
        if (!_confirmed)
            SelectedPixel = null;
        base.OnClosing(args);
        if (args.Cancel)
        {
            _closing = false;
            _confirmed = false;
            SelectedPixel = null;
        }
    }

    protected override void OnClosed(EventArgs args)
    {
        _closed = true;
        ThemeService.Changed -= OnThemeChanged;
        if (!_confirmed || DialogResult != true)
            SelectedPixel = null;
        if (_handle != 0)
            DetachDisplayHooks();
        _handle = 0;
        base.OnClosed(args);
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs args)
    {
        if (!_closing && UpdateFromCursor(args))
            _surface.InvalidateVisual();
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        var kind = args.GetCurrentPoint(this).Properties.PointerUpdateKind;
        args.Handled = true;
        if (kind == PointerUpdateKind.RightButtonPressed)
        {
            CancelSelection();
            return;
        }
        if (kind == PointerUpdateKind.LeftButtonPressed && !_closing && UpdateFromCursor(args))
            _surface.InvalidateVisual();
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (args.InitialPressMouseButton != MouseButton.Left)
            return;
        args.Handled = true;
        if (!_closing && UpdateFromCursor(args))
            ConfirmSelection();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (_closing)
            return;
        switch (args.Key)
        {
            case Key.Escape:
                args.Handled = true;
                CancelSelection();
                break;
            case Key.Enter:
                args.Handled = true;
                ConfirmSelection();
                break;
            case Key.Up:
            case Key.Down:
                args.Handled = true;
                var step = (args.KeyModifiers & KeyModifiers.Shift) != 0 ? 10 : 1;
                var delta = args.Key == Key.Up ? -step : step;
                _pixel = (int)Math.Clamp((long)_pixel + delta, 0, _maximumPixel);
                _surface.InvalidateVisual();
                break;
        }
    }

    /// <summary>
    /// Maps the pointer to a snapshot row. Windows polls the physical cursor like the WPF version; on
    /// Linux the event position is used, relative to wherever the window manager placed the window.
    /// </summary>
    private bool UpdateFromCursor(PointerEventArgs args)
    {
        try
        {
            double localX, localY, width, height;
            if (PlatformInfo.IsWindows)
            {
                var cursor = CaptureNative.CursorPosition();
                localX = (long)cursor.X - _physicalBounds.X;
                localY = (long)cursor.Y - _physicalBounds.Y;
                width = _physicalBounds.Width;
                height = _physicalBounds.Height;
            }
            else
            {
                var point = args.GetPosition(_surface);
                localX = point.X;
                localY = point.Y;
                width = _surface.Bounds.Width;
                height = _surface.Bounds.Height;
            }
            if (localX < 0 || localX >= width || localY < 0 || localY >= height || height <= 0)
                return false;
            var row = (long)Math.Floor(localY * _snapshot.PixelSize.Height / height);
            // The start includes the clicked row; the exclusive end includes it as its last row.
            _pixel = (int)Math.Clamp(row + (_isStart ? 0 : 1), 0, _maximumPixel);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            CancelSelection();
            return false;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs args)
    {
        void Redraw() { if (!_closed) _surface.InvalidateVisual(); }
        if (Dispatcher.UIThread.CheckAccess()) Redraw();
        else Dispatcher.UIThread.Post(Redraw);
    }

    private void ConfirmSelection()
    {
        if (_closing || _closed)
            return;
        _confirmed = true;
        _closing = true;
        SelectedPixel = _pixel;
        DialogResult = true;
    }

    private void CancelSelection()
    {
        if (_closing || _closed)
            return;
        _closing = true;
        _confirmed = false;
        SelectedPixel = null;
        Close();
    }

    private void RenderBoundary(DrawingContext drawing)
    {
        var size = _surface.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        var viewport = new Rect(size);
        // Lines keep their physical thickness; the boundary itself is a snapshot row.
        var scaleY = viewport.Height / _physicalBounds.Height;
        var y = _pixel * (viewport.Height / _snapshot.PixelSize.Height);
        using (drawing.PushClip(viewport))
        {
            drawing.DrawImage(_snapshot, viewport);
            var mask = _isStart ? new Rect(0, 0, viewport.Width, y)
                : new Rect(0, y, viewport.Width, Math.Max(0, viewport.Height - y));
            drawing.DrawRectangle(MaskBrush, null, mask);
            var from = new Point(0, y);
            var to = new Point(viewport.Width, y);
            drawing.DrawLine(new Pen(Brushes.Black, 5 * scaleY), from, to);
            drawing.DrawLine(new Pen(AccentBrush, 2 * scaleY), from, to);
            DrawHint(drawing, viewport, y);
        }
    }

    private void DrawHint(DrawingContext drawing, Rect viewport, double boundaryY)
    {
        var margin = Math.Min(10, Math.Min(viewport.Width, viewport.Height) / 4);
        var width = Math.Min(550, Math.Max(1, viewport.Width - margin * 2));
        var innerWidth = Math.Max(1, width - 20);
        var heading = Text(Title ?? "", 14, ThemeService.Brush("OverlayInk"));
        heading.MaxTextWidth = innerWidth;
        var help = Text(I18n.T("↑↓ 微調 1 px · Shift 10 px · Enter 確認 · Esc／右鍵取消"), 11, MutedBrush);
        help.MaxTextWidth = innerWidth;
        var height = Math.Min(heading.Height + help.Height + 22, Math.Max(1, viewport.Height - margin * 2));
        var left = (viewport.Width - width) / 2;
        var top = boundaryY >= viewport.Height / 2 ? margin : Math.Max(margin, viewport.Height - height - margin);
        var panel = new Rect(left, top, width, height);
        drawing.DrawRectangle(PanelBrush, null, panel, 7, 7);
        using (drawing.PushClip(panel))
        {
            drawing.DrawText(heading, new Point(left + 10, top + 8));
            drawing.DrawText(help, new Point(left + 10, top + 12 + heading.Height));
        }
    }

    private static FormattedText Text(string value, double size, IBrush brush) => new(value,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(UiThemes.AppFont), size, brush);

    private sealed class BoundarySurface(BoundaryPickWindow owner) : Control
    {
        public override void Render(DrawingContext drawing)
        {
            base.Render(drawing);
            owner.RenderBoundary(drawing);
        }
    }
}

using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SnipFlow.Capture;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;

namespace SnipFlow.Scroll;

/// <summary>Selects a physical pixel boundary on an already captured region.</summary>
public sealed class BoundaryPickWindow : Window
{
    private static readonly Brush MaskBrush = FrozenBrush(Color.FromArgb(142, 16, 16, 16));
    private static readonly Brush PanelBrush = FrozenBrush(Color.FromArgb(240, 32, 32, 32));
    private static readonly Brush AccentBrush = FrozenBrush(Color.FromRgb(92, 224, 180));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(201, 201, 201));
    private readonly DrawingRectangle _physicalBounds;
    private readonly BitmapSource _snapshot;
    private readonly bool _isStart;
    private readonly int _maximumPixel;
    private readonly BoundarySurface _surface;
    private HwndSource? _source;
    private IntPtr _handle;
    private int _pixel;
    private bool _positioning;
    private bool _wasActivated;
    private bool _closing;
    private bool _confirmed;
    private bool _closed;

    /// <summary>Start row [0, H - 1], or exclusive end boundary [0, H]; null on cancellation.</summary>
    public int? SelectedPixel { get; private set; }

    public BoundaryPickWindow(DrawingRectangle physicalBounds, BitmapSource frozenSnapshot,
        bool isStart, int? initialPixel = null, bool fromCollectedFrame = false)
    {
        ArgumentNullException.ThrowIfNull(frozenSnapshot);
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
            throw new ArgumentException("The selected region must have positive dimensions.", nameof(physicalBounds));
        if (frozenSnapshot.PixelWidth != physicalBounds.Width || frozenSnapshot.PixelHeight != physicalBounds.Height)
            throw new ArgumentException("The snapshot must match the selected region in physical pixels.", nameof(frozenSnapshot));

        _physicalBounds = physicalBounds;
        _snapshot = frozenSnapshot.IsFrozen ? frozenSnapshot : frozenSnapshot.CloneCurrentValue();
        if (!_snapshot.IsFrozen)
            _snapshot.Freeze();
        _isStart = isStart;
        _maximumPixel = isStart ? physicalBounds.Height - 1 : physicalBounds.Height;
        _pixel = Math.Clamp(initialPixel ?? physicalBounds.Height / 2, 0, _maximumPixel);
        _surface = new BoundarySurface(this) { Focusable = true, FocusVisualStyle = null };

        Title = I18n.T(fromCollectedFrame ? "從已收集末頁選終點"
            : isStart ? "點選第一則訊息的上緣" : "點選最後一則訊息的下緣");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;
        Focusable = true;
        Width = physicalBounds.Width;
        Height = physicalBounds.Height;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Content = _surface;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) =>
        {
            PositionPhysical();
            if (!_closing)
                _surface.Focus();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessage);
        PositionPhysical();
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x02E0) // WM_DPICHANGED: WPF updates its scale before physical bounds are restored.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionPhysical));
        if (message == 0x007E) // WM_DISPLAYCHANGE: the frozen snapshot no longer matches the desktop.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CancelSelection));
        return IntPtr.Zero;
    }

    private void PositionPhysical()
    {
        if (_handle == IntPtr.Zero || _positioning || _closing || _closed)
            return;
        _positioning = true;
        try
        {
            CaptureNative.PositionOverlay(_handle, _physicalBounds);
        }
        catch (Win32Exception)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CancelSelection));
        }
        finally
        {
            _positioning = false;
        }
    }

    protected override void OnActivated(EventArgs args)
    {
        base.OnActivated(args);
        _wasActivated = true;
    }

    protected override void OnDeactivated(EventArgs args)
    {
        base.OnDeactivated(args);
        if (_wasActivated && !_closing && !_closed)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!IsActive)
                    CancelSelection();
            }));
    }

    protected override void OnClosing(CancelEventArgs args)
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
        if (!_confirmed || DialogResult != true)
            SelectedPixel = null;
        _source?.RemoveHook(WindowMessage);
        _source = null;
        _handle = IntPtr.Zero;
        base.OnClosed(args);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs args)
    {
        base.OnPreviewMouseMove(args);
        if (!_closing && UpdateFromCursor())
            _surface.InvalidateVisual();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonDown(args);
        args.Handled = true;
        if (!_closing && UpdateFromCursor())
            _surface.InvalidateVisual();
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonUp(args);
        args.Handled = true;
        if (!_closing && UpdateFromCursor())
            ConfirmSelection();
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseRightButtonDown(args);
        args.Handled = true;
        CancelSelection();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args);
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
                var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                var delta = args.Key == Key.Up ? -step : step;
                _pixel = (int)Math.Clamp((long)_pixel + delta, 0, _maximumPixel);
                _surface.InvalidateVisual();
                break;
        }
    }

    private bool UpdateFromCursor()
    {
        try
        {
            var cursor = CaptureNative.CursorPosition();
            var localX = (long)cursor.X - _physicalBounds.X;
            var localY = (long)cursor.Y - _physicalBounds.Y;
            if (localX < 0 || localX >= _physicalBounds.Width || localY < 0 || localY >= _physicalBounds.Height)
                return false;
            // The start includes the clicked row; the exclusive end includes it as its last row.
            _pixel = (int)Math.Clamp(localY + (_isStart ? 0 : 1), 0, _maximumPixel);
            return true;
        }
        catch (Win32Exception)
        {
            CancelSelection();
            return false;
        }
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
        if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0)
            return;
        var viewport = new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight);
        var scaleY = viewport.Height / _physicalBounds.Height;
        var y = _pixel * scaleY;
        drawing.PushClip(new RectangleGeometry(viewport));
        drawing.DrawImage(_snapshot, viewport);
        var mask = _isStart ? new Rect(0, 0, viewport.Width, y)
            : new Rect(0, y, viewport.Width, Math.Max(0, viewport.Height - y));
        drawing.DrawRectangle(MaskBrush, null, mask);
        var from = new WpfPoint(0, y);
        var to = new WpfPoint(viewport.Width, y);
        drawing.DrawLine(new Pen(Brushes.Black, 5 * scaleY), from, to);
        drawing.DrawLine(new Pen(AccentBrush, 2 * scaleY), from, to);
        DrawHint(drawing, viewport, y);
        drawing.Pop();
    }

    private void DrawHint(DrawingContext drawing, Rect viewport, double boundaryY)
    {
        var margin = Math.Min(10, Math.Min(viewport.Width, viewport.Height) / 4);
        var width = Math.Min(550, Math.Max(1, viewport.Width - margin * 2));
        var innerWidth = Math.Max(1, width - 20);
        var heading = Text(Title, 14, Brushes.White);
        heading.MaxTextWidth = innerWidth;
        var help = Text(I18n.T("↑↓ 微調 1 px · Shift 10 px · Enter 確認 · Esc／右鍵取消"), 11, MutedBrush);
        help.MaxTextWidth = innerWidth;
        var height = Math.Min(heading.Height + help.Height + 22, Math.Max(1, viewport.Height - margin * 2));
        var left = (viewport.Width - width) / 2;
        var top = boundaryY >= viewport.Height / 2 ? margin : Math.Max(margin, viewport.Height - height - margin);
        var panel = new Rect(left, top, width, height);
        drawing.DrawRoundedRectangle(PanelBrush, null, panel, 7, 7);
        drawing.PushClip(new RectangleGeometry(panel));
        drawing.DrawText(heading, new WpfPoint(left + 10, top + 8));
        drawing.DrawText(help, new WpfPoint(left + 10, top + 12 + heading.Height));
        drawing.Pop();
    }

    private FormattedText Text(string value, double size, Brush brush) => new(value,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI, Microsoft JhengHei UI"), size, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class BoundarySurface(BoundaryPickWindow owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            owner.RenderBoundary(drawing);
        }
    }
}

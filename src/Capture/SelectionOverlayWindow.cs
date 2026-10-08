using System.Globalization;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;

namespace SnipFlow.Capture;

internal sealed class SelectionOverlayWindow : Window
{
    private static readonly Brush MaskBrush = FrozenBrush(Color.FromArgb(152, 7, 12, 20));
    private static readonly Brush PanelBrush = FrozenBrush(Color.FromArgb(244, 18, 24, 34));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(163, 163, 163));
    private static readonly Brush AccentBrush = FrozenBrush(Color.FromRgb(232, 232, 232));
    private static readonly Pen SelectionPen = FrozenPen(AccentBrush, 1.5);
    private static readonly Pen PanelPen = FrozenPen(FrozenBrush(Color.FromArgb(180, 73, 92, 116)), 1);
    private readonly CaptureSession _session;
    private readonly BitmapSource _snapshot;
    private readonly OverlaySurface _surface;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _positioning;
    private bool _closed;

    internal DrawingRectangle MonitorBounds { get; }

    internal SelectionOverlayWindow(CaptureSession session, CaptureMonitor monitor, BitmapSource snapshot)
    {
        _session = session;
        _snapshot = snapshot;
        _surface = new OverlaySurface(this);
        MonitorBounds = monitor.Bounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _surface;
        Cursor = Cursors.Cross;
        Width = Math.Max(1, monitor.Bounds.Width);
        Height = Math.Max(1, monitor.Bounds.Height);
        Focusable = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) => PositionPhysical();
        Closed += OnClosed;
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
        if (message == 0x02E0) // WM_DPICHANGED: let WPF update its scale, then restore physical monitor bounds.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionPhysical));
        if (message == 0x007E) // WM_DISPLAYCHANGE: the initial desktop snapshot no longer matches the monitors.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(_session.Cancel));
        return IntPtr.Zero;
    }

    private void PositionPhysical()
    {
        if (_closed || _handle == IntPtr.Zero || _positioning)
            return;

        _positioning = true;
        try
        {
            CaptureNative.PositionOverlay(_handle, MonitorBounds);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _source?.RemoveHook(WindowMessage);
        _source = null;
        _handle = IntPtr.Zero;
        _session.Cancel();
    }

    internal void RefreshSelection() => _surface.InvalidateVisual();

    internal void CloseOverlay()
    {
        if (!_closed)
            Close();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonDown(args);
        args.Handled = true;
        _session.BeginSelection(this);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseLeftButtonUp(args);
        args.Handled = true;
        _session.EndSelection();
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs args)
    {
        base.OnPreviewMouseRightButtonDown(args);
        args.Handled = true;
        _session.Cancel();
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        base.OnLostMouseCapture(args);
        _session.CaptureLost();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args);
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            _session.Cancel();
        }
        else if (args.Key == Key.Enter && _session.Mode != CaptureMode.Region)
        {
            args.Handled = true;
            _session.ConfirmChoice();
        }
        else if (_session.AllowModeSwitch && Keyboard.Modifiers == ModifierKeys.None)
        {
            var mode = args.Key switch
            {
                Key.R => CaptureMode.Region,
                Key.W => CaptureMode.Window,
                Key.S => CaptureMode.Monitor,
                Key.A => CaptureMode.AllMonitors,
                _ => (CaptureMode?)null
            };
            if (mode is { } selectedMode)
            {
                args.Handled = true;
                _session.SwitchMode(selectedMode);
            }
        }
    }

    private void RenderSelection(DrawingContext drawing)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.DrawImage(_snapshot, viewport);
        drawing.PushClip(new RectangleGeometry(viewport));
        if (_session.Selection is DrawingRectangle selection)
        {
            var localSelection = ToLocal(selection);
            var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
            mask.Children.Add(new RectangleGeometry(viewport));
            var intersection = Rect.Intersect(localSelection, viewport);
            if (!intersection.IsEmpty)
                mask.Children.Add(new RectangleGeometry(intersection));
            drawing.DrawGeometry(MaskBrush, null, mask);
            drawing.DrawRectangle(null, SelectionPen, localSelection);
            if (_session.Mode == CaptureMode.Region)
                DrawCorners(drawing, localSelection);
        }
        else
        {
            drawing.DrawRectangle(MaskBrush, null, viewport);
        }

        if (!_session.IsSelecting && MonitorBounds.Contains(_session.Cursor))
            DrawHint(drawing);

        if (_session.IsSelecting && MonitorBounds.Contains(_session.Cursor))
            DrawDimensions(drawing);
        drawing.Pop();
    }

    private Rect ToLocal(DrawingRectangle rectangle)
    {
        var scaleX = ActualWidth / MonitorBounds.Width;
        var scaleY = ActualHeight / MonitorBounds.Height;
        return new Rect((rectangle.X - MonitorBounds.X) * scaleX,
            (rectangle.Y - MonitorBounds.Y) * scaleY, rectangle.Width * scaleX, rectangle.Height * scaleY);
    }

    private void DrawHint(DrawingContext drawing)
    {
        var title = _session.Mode switch
        {
            CaptureMode.Window => I18n.T("點選視窗"),
            CaptureMode.Monitor => I18n.T("點選要擷取的螢幕"),
            CaptureMode.AllMonitors => I18n.T("點一下擷取所有螢幕"),
            _ => I18n.T("拖曳框選畫面")
        };
        var caption = _session.StatusHint;
        if (caption is null && _session.Mode == CaptureMode.Window && _session.HoveredWindow is { } target)
            caption = string.IsNullOrWhiteSpace(target.Title) ? I18n.T("視窗") : target.Title;
        if (caption is null && _session.Selection is { } bounds && _session.Mode != CaptureMode.Region)
            caption = I18n.F("{0:N0} × {1:N0} px", bounds.Width, bounds.Height);

        var width = _session.AllowModeSwitch ? 430 : 310;
        var height = 70 + (_session.AllowModeSwitch ? 25 : 0) + (caption is not null ? 25 : 0);
        var panel = new Rect(Math.Max(12, (ActualWidth - width) / 2), Math.Min(38, ActualHeight / 8),
            Math.Min(width, Math.Max(1, ActualWidth - 24)), Math.Min(height, Math.Max(1, ActualHeight - 24)));
        drawing.DrawRoundedRectangle(PanelBrush, PanelPen, panel, 13, 13);
        drawing.PushClip(new RectangleGeometry(panel));
        var textWidth = Math.Max(1, panel.Width - 40);
        void Line(string value, double size, Brush brush, double top)
        {
            var text = Text(value, size, brush);
            text.MaxTextWidth = textWidth;
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            drawing.DrawText(text, new WpfPoint(panel.Left + 20, top));
        }
        Line(title, 16, Brushes.White, panel.Top + 13);
        var lineY = panel.Top + 39;
        if (caption is not null)
        {
            Line(caption, 12, _session.StatusHint is null ? MutedBrush : Brushes.White, lineY);
            lineY += 25;
        }
        if (_session.AllowModeSwitch)
        {
            Line(I18n.T("R 框選   W 視窗   S 單一螢幕   A 所有螢幕"), 12, MutedBrush, lineY);
            lineY += 25;
        }
        Line(I18n.T("Esc 取消   ·   右鍵取消"), 12, MutedBrush, lineY);
        drawing.Pop();
    }

    private void DrawDimensions(DrawingContext drawing)
    {
        var selection = _session.Selection;
        var label = selection is DrawingRectangle bounds ? I18n.F("{0:N0} × {1:N0} px", bounds.Width, bounds.Height) : I18n.T("拖曳選取範圍");
        var text = Text(label, 13, Brushes.White);
        var width = Math.Max(146, text.Width + 28);
        const double height = 36;
        var cursorX = (_session.Cursor.X - MonitorBounds.X) * ActualWidth / MonitorBounds.Width;
        var cursorY = (_session.Cursor.Y - MonitorBounds.Y) * ActualHeight / MonitorBounds.Height;
        var left = Math.Clamp(cursorX + 20, 8, Math.Max(8, ActualWidth - width - 8));
        var top = Math.Clamp(cursorY + 22, 8, Math.Max(8, ActualHeight - height - 8));
        var panel = new Rect(left, top, width, height);
        drawing.DrawRoundedRectangle(PanelBrush, PanelPen, panel, 8, 8);
        drawing.DrawText(text, new WpfPoint(panel.Left + 14, panel.Top + (height - text.Height) / 2));
    }

    private static void DrawCorners(DrawingContext drawing, Rect rectangle)
    {
        var length = Math.Min(12, Math.Min(rectangle.Width, rectangle.Height) / 3);
        foreach (var corner in new[] { rectangle.TopLeft, rectangle.TopRight, rectangle.BottomLeft, rectangle.BottomRight })
        {
            var dx = corner.X == rectangle.Left ? length : -length;
            var dy = corner.Y == rectangle.Top ? length : -length;
            drawing.DrawLine(SelectionPen, corner, new WpfPoint(corner.X + dx, corner.Y));
            drawing.DrawLine(SelectionPen, corner, new WpfPoint(corner.X, corner.Y + dy));
        }
    }

    private FormattedText Text(string value, double size, Brush brush) => new(value,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }

    private sealed class OverlaySurface(SelectionOverlayWindow owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            owner.RenderSelection(drawing);
        }
    }
}

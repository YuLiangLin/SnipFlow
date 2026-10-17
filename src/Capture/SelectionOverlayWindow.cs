using System.Globalization;
using Avalonia.Input;
using Avalonia.Media.Immutable;

namespace SnipFlow.Capture;

/// <summary>
/// One frameless, topmost overlay per monitor. It shows the frozen desktop image 1:1 in physical
/// pixels, the dimming mask, the selection, window highlight and the mode hint.
/// </summary>
internal sealed class SelectionOverlayWindow : Window
{
    private static readonly IBrush MaskBrush = new ImmutableSolidColorBrush(Color.FromArgb(152, 7, 12, 20));
    private static IBrush PanelBrush => ThemeService.Brush("OverlayPanel");
    private static IBrush MutedBrush => ThemeService.Brush("OverlayMuted");
    private static IBrush InkBrush => ThemeService.Brush("OverlayInk");
    private static Pen SelectionPen => new(ThemeService.Brush("SelectionHandle"), 1.5);
    private static Pen PanelPen => new(ThemeService.Brush("OverlayLine"), 1);
    private readonly CaptureSession _session;
    private readonly Bitmap _snapshot;
    private readonly OverlaySurface _surface;
    private nint _handle;
    private bool _positioning;
    private bool _closed;

    internal PixelRect MonitorBounds { get; }
    internal nint Handle => _handle;
    internal Control Surface => _surface;

    internal SelectionOverlayWindow(CaptureSession session, CaptureMonitor monitor, Bitmap desktopImage)
    {
        _session = session;
        _snapshot = desktopImage;
        _surface = new OverlaySurface(this) { Focusable = true };
        MonitorBounds = monitor.Bounds;
        Title = "SnipFlow";
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _surface;
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;
        UseLayoutRounding = true;
        var exact = Math.Abs(session.ImageScale.X - 1) < 0.001 && Math.Abs(session.ImageScale.Y - 1) < 0.001;
        RenderOptions.SetBitmapInterpolationMode(this, exact ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        // Physical placement before the first map; the window manager then sees the final geometry.
        // Windows corrects the size with SetWindowPos once the window exists (Opened).
        if (PlatformInfo.IsWindows)
        {
            var scaling = Screens.ScreenFromBounds(MonitorBounds)?.Scaling is > 0 and var value ? value : 1;
            Position = MonitorBounds.Position;
            Width = Math.Max(1, MonitorBounds.Width / scaling);
            Height = Math.Max(1, MonitorBounds.Height / scaling);
        }
        else
            CaptureNative.PositionOverlay(this, MonitorBounds);
        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerMoved += (_, args) => _session.PointerMoved(EventPosition(args));
        _surface.PointerCaptureLost += (_, _) => _session.CaptureLost();
        ThemeService.Changed += OnThemeChanged;
    }

    private void OnOpened(object? sender, EventArgs args)
    {
        _handle = TryGetPlatformHandle()?.Handle ?? 0;
        AttachDisplayHooks();
        PositionPhysical();
        // WPF ContentRendered: position again once the first frame is shown.
        Dispatcher.UIThread.Post(PositionPhysical, DispatcherPriority.Background);
    }

#if WINDOWS
    private void AttachDisplayHooks() => Win32Properties.AddWndProcHookCallback(this, WindowMessage);

    private void DetachDisplayHooks() => Win32Properties.RemoveWndProcHookCallback(this, WindowMessage);

    private IntPtr WindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x02E0) // WM_DPICHANGED: let Avalonia update its scale, then restore physical monitor bounds.
            Dispatcher.UIThread.Post(PositionPhysical, DispatcherPriority.Loaded);
        if (message == 0x007E) // WM_DISPLAYCHANGE: the initial desktop snapshot no longer matches the monitors.
            Dispatcher.UIThread.Post(_session.Cancel, DispatcherPriority.Input);
        return IntPtr.Zero;
    }
#else
    private void AttachDisplayHooks() => Screens.Changed += OnScreensChanged;

    private void DetachDisplayHooks() => Screens.Changed -= OnScreensChanged;

    // XRandR change: the initial desktop snapshot no longer matches the monitors.
    private void OnScreensChanged(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(_session.Cancel, DispatcherPriority.Input);
#endif

    private void PositionPhysical()
    {
        if (_closed || _handle == 0 || _positioning)
            return;

        _positioning = true;
        try
        {
            CaptureNative.PositionOverlay(this, MonitorBounds);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        ThemeService.Changed -= OnThemeChanged;
        if (_handle != 0)
            DetachDisplayHooks();
        _handle = 0;
        _session.Cancel();
    }

    internal void RefreshSelection() => _surface.InvalidateVisual();

    private void OnThemeChanged(object? sender, EventArgs args)
    {
        void Redraw() { if (!_closed) _surface.InvalidateVisual(); }
        if (Dispatcher.UIThread.CheckAccess()) Redraw();
        else Dispatcher.UIThread.Post(Redraw);
    }

    internal void CloseOverlay()
    {
        if (!_closed)
            Close();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        var kind = args.GetCurrentPoint(_surface).Properties.PointerUpdateKind;
        args.Handled = true;
        if (kind == PointerUpdateKind.LeftButtonPressed)
            _session.BeginSelection(this, args.Pointer, EventPosition(args));
        else if (kind == PointerUpdateKind.RightButtonPressed)
            _session.Cancel();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        args.Handled = true;
        if (args.InitialPressMouseButton == MouseButton.Left)
            _session.EndSelection(EventPosition(args));
    }

    /// <summary>
    /// The event's position in physical desktop pixels. On Linux the input event is used, because
    /// polling the pointer when the event is handled can already see a later position. Windows keeps
    /// the original physical cursor polling (null).
    /// </summary>
    private PixelPoint? EventPosition(PointerEventArgs args)
    {
        if (PlatformInfo.IsWindows)
            return null;
        var point = args.GetPosition(_surface);
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        return new PixelPoint(Position.X + (int)Math.Round(point.X * scaling), Position.Y + (int)Math.Round(point.Y * scaling));
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
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
        else if (_session.AllowModeSwitch && args.KeyModifiers == KeyModifiers.None)
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

    /// <summary>
    /// The desktop rectangle this window shows, in physical pixels. Windows positions the overlay
    /// exactly on its monitor; on Linux the window manager places full-screen windows, so the
    /// actual placement is used and the frozen image stays aligned with the real desktop.
    /// </summary>
    private PixelRect ShownBounds(Size viewport)
    {
        if (PlatformInfo.IsWindows)
            return MonitorBounds;
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        return new PixelRect(Position, new PixelSize(Math.Max(1, (int)Math.Round(viewport.Width * scaling)),
            Math.Max(1, (int)Math.Round(viewport.Height * scaling))));
    }

    private void RenderSelection(DrawingContext drawing, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var viewport = new Rect(size);
        var shown = ShownBounds(size);
        var scaleX = size.Width / shown.Width;
        var scaleY = size.Height / shown.Height;
        Rect ToLocal(PixelRect rectangle) => new((rectangle.X - shown.X) * scaleX,
            (rectangle.Y - shown.Y) * scaleY, rectangle.Width * scaleX, rectangle.Height * scaleY);

        var imageScale = _session.ImageScale;
        var desktop = _session.DesktopBounds;
        var source = new Rect((shown.X - desktop.X) * imageScale.X, (shown.Y - desktop.Y) * imageScale.Y,
            shown.Width * imageScale.X, shown.Height * imageScale.Y);
        var image = new Rect(0, 0, _snapshot.PixelSize.Width, _snapshot.PixelSize.Height);
        var visibleSource = source.Intersect(image);
        if (visibleSource.Width > 0 && visibleSource.Height > 0)
        {
            var destination = new Rect((visibleSource.X - source.X) / imageScale.X * scaleX,
                (visibleSource.Y - source.Y) / imageScale.Y * scaleY,
                visibleSource.Width / imageScale.X * scaleX, visibleSource.Height / imageScale.Y * scaleY);
            drawing.DrawImage(_snapshot, visibleSource, destination);
        }

        using (drawing.PushClip(viewport))
        {
            if (_session.Selection is PixelRect selection)
            {
                var localSelection = ToLocal(selection);
                var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
                mask.Children.Add(new RectangleGeometry(viewport));
                var intersection = localSelection.Intersect(viewport);
                if (intersection.Width > 0 && intersection.Height > 0)
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

            var cursorHere = CaptureGeometry.Contains(MonitorBounds, _session.Cursor);
            if (!_session.IsSelecting && cursorHere)
                DrawHint(drawing, size);

            if (_session.IsSelecting && cursorHere)
                DrawDimensions(drawing, size, shown, scaleX, scaleY);
        }
    }

    private void DrawHint(DrawingContext drawing, Size size)
    {
        var title = _session.Mode switch
        {
            CaptureMode.Window when _session.SelectWindowOnly => I18n.T("先選取要捲動的視窗"),
            CaptureMode.Window when PlatformInfo.IsWayland => I18n.T("點一下，用系統工具選取視窗"),
            CaptureMode.Window => I18n.T("點選視窗"),
            CaptureMode.Monitor => I18n.T("點選要擷取的螢幕"),
            CaptureMode.AllMonitors => I18n.T("點一下擷取所有螢幕"),
            _ => I18n.T(_session.RestrictsToWindow ? "在選定視窗內框選對話或程式碼區" : "拖曳框選畫面")
        };
        var caption = _session.StatusHint;
        if (caption is null && _session.Mode == CaptureMode.Window && PlatformInfo.IsWayland)
            caption = I18n.T("Wayland 不允許程式直接讀取其他視窗，將開啟桌面的截圖選擇器。");
        if (caption is null && _session.Mode == CaptureMode.Window && _session.HoveredWindow is { } target)
            caption = string.IsNullOrWhiteSpace(target.Title) ? I18n.T("視窗") : target.Title;
        if (caption is null && _session.Selection is { } bounds && _session.Mode != CaptureMode.Region)
            caption = I18n.F("{0:N0} × {1:N0} px", bounds.Width, bounds.Height);

        var width = _session.AllowModeSwitch || _session.RestrictsToWindow ? 430.0 : 310.0;
        // Longer platform notes (Wayland) widen the panel instead of being cut off.
        if (caption is not null)
            width = Math.Max(width, Math.Min(640, Text(caption, 12, MutedBrush).Width + 44));
        var height = 70 + (_session.AllowModeSwitch ? 25 : 0) + (caption is not null ? 25 : 0);
        var panel = new Rect(Math.Max(12, (size.Width - width) / 2), Math.Min(38, size.Height / 8),
            Math.Min(width, Math.Max(1, size.Width - 24)), Math.Min(height, Math.Max(1, size.Height - 24)));
        drawing.DrawRectangle(PanelBrush, PanelPen, panel, 13, 13);
        using (drawing.PushClip(panel))
        {
            var textWidth = Math.Max(1, panel.Width - 40);
            void Line(string value, double fontSize, IBrush brush, double top)
            {
                var text = Text(value, fontSize, brush);
                text.MaxTextWidth = textWidth;
                text.MaxLineCount = 1;
                text.Trimming = TextTrimming.CharacterEllipsis;
                drawing.DrawText(text, new Point(panel.Left + 20, top));
            }
            Line(title, 16, InkBrush, panel.Top + 13);
            var lineY = panel.Top + 39;
            if (caption is not null)
            {
                Line(caption, 12, _session.StatusHint is null ? MutedBrush : InkBrush, lineY);
                lineY += 25;
            }
            if (_session.AllowModeSwitch)
            {
                Line(I18n.T("R 框選   W 視窗   S 單一螢幕   A 所有螢幕"), 12, MutedBrush, lineY);
                lineY += 25;
            }
            Line(I18n.T("Esc 取消   ·   右鍵取消"), 12, MutedBrush, lineY);
        }
    }

    private void DrawDimensions(DrawingContext drawing, Size size, PixelRect shown, double scaleX, double scaleY)
    {
        var selection = _session.Selection;
        var label = selection is PixelRect bounds ? I18n.F("{0:N0} × {1:N0} px", bounds.Width, bounds.Height) : I18n.T("拖曳選取範圍");
        var text = Text(label, 13, InkBrush);
        var width = Math.Max(146, text.Width + 28);
        const double height = 36;
        var cursorX = (_session.Cursor.X - shown.X) * scaleX;
        var cursorY = (_session.Cursor.Y - shown.Y) * scaleY;
        var left = Math.Clamp(cursorX + 20, 8, Math.Max(8, size.Width - width - 8));
        var top = Math.Clamp(cursorY + 22, 8, Math.Max(8, size.Height - height - 8));
        var panel = new Rect(left, top, width, height);
        drawing.DrawRectangle(PanelBrush, PanelPen, panel, 8, 8);
        drawing.DrawText(text, new Point(panel.Left + 14, panel.Top + (height - text.Height) / 2));
    }

    private static void DrawCorners(DrawingContext drawing, Rect rectangle)
    {
        var length = Math.Min(12, Math.Min(rectangle.Width, rectangle.Height) / 3);
        var pen = SelectionPen;
        foreach (var corner in new[] { rectangle.TopLeft, rectangle.TopRight, rectangle.BottomLeft, rectangle.BottomRight })
        {
            var dx = corner.X == rectangle.Left ? length : -length;
            var dy = corner.Y == rectangle.Top ? length : -length;
            drawing.DrawLine(pen, corner, new Point(corner.X + dx, corner.Y));
            drawing.DrawLine(pen, corner, new Point(corner.X, corner.Y + dy));
        }
    }

    private static FormattedText Text(string value, double size, IBrush brush) => new(value,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(UiThemes.AppFont), size, brush);

    private sealed class OverlaySurface(SelectionOverlayWindow owner) : Control, Avalonia.Rendering.ICustomHitTest
    {
        // The whole monitor accepts input, including the undimmed selection.
        public bool HitTest(Point point) => true;

        public override void Render(DrawingContext drawing)
        {
            base.Render(drawing);
            owner.RenderSelection(drawing, Bounds.Size);
        }
    }
}

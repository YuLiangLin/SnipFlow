using Avalonia.Input;
using SnipFlow.Services;

namespace SnipFlow.Video;

/// <summary>The preview uses source-pixel coordinates and the export renderer's visuals.</summary>
internal sealed class VideoEditorCanvas : Canvas
{
    sealed class ItemVisual(Border host, Control visual)
    {
        internal Border Host { get; } = host;
        internal Control Visual { get; set; } = visual;
    }

    readonly Dictionary<Guid, ItemVisual> visuals = [];
    readonly Border outline = new() { Background = Brushes.Transparent, IsHitTestVisible = false };
    readonly List<Border> handles = [];
    VideoEditDocument? document;
    VideoOverlay? selected, dragging, editing;
    TextBox? textEditor;
    IPointer? capturedPointer;
    string originalText = "";
    Point dragOrigin;
    Rect originalBounds;
    TimeSpan position;
    bool editingEnabled = true, endingText;
    double screenScale = 1, originalFontSize;
    int resizingCorner = -1;

    public event Action<VideoOverlay?>? SelectionChanged;
    public event Action? OverlayEdited;
    public VideoOverlay? Selected => selected;
    public bool IsEditingText => textEditor is not null;

    public VideoEditorCanvas()
    {
        Background = Brushes.Transparent; ClipToBounds = true; Focusable = true;
        outline.BorderBrush = ThemeService.Brush("Accent");
        outline.BorderThickness = new Thickness(1); outline.ZIndex = 100;
        Children.Add(outline);
        foreach (var corner in new[] { 0, 1, 2, 3 })
        {
            var handle = new Border
            {
                Tag = corner, Width = 8, Height = 8, Focusable = false,
                Background = ThemeService.Brush("Accent"), BorderBrush = ThemeService.Brush("Panel"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1), ZIndex = 101,
                Cursor = new Cursor(corner switch
                {
                    0 => StandardCursorType.TopLeftCorner, 1 => StandardCursorType.TopRightCorner,
                    2 => StandardCursorType.BottomLeftCorner, _ => StandardCursorType.BottomRightCorner,
                })
            };
            handle.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                EndTextEditing(true);
                args.Handled = true;
                if (selected is null || !editingEnabled) return;
                dragOrigin = args.GetPosition(this);
                originalBounds = OverlayBounds(selected);
                originalFontSize = selected.FontSize;
                resizingCorner = corner;
                capturedPointer = args.Pointer; args.Pointer.Capture(handle);
            };
            handle.PointerMoved += (_, args) =>
            {
                if (resizingCorner != corner) return;
                ResizeSelected(corner, args.GetPosition(this)); args.Handled = true;
            };
            handle.PointerReleased += (_, args) =>
            {
                if (resizingCorner != corner) return;
                resizingCorner = -1; ReleaseCapture(); OverlayEdited?.Invoke(); args.Handled = true;
            };
            handle.PointerCaptureLost += (_, _) =>
            {
                if (resizingCorner != corner) return;
                resizingCorner = -1; capturedPointer = null; OverlayEdited?.Invoke();
            };
            handles.Add(handle); Children.Add(handle);
        }
        PointerPressed += (_, args) =>
        {
            if (!editingEnabled || args.Handled || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            EndTextEditing(true); Select(null); Focus(); args.Handled = true;
        };
        UpdateAdornments();
    }

    public void SetDocument(VideoEditDocument value)
    {
        // Decoder failures must not discard the current project's visible objects.
        var prepared = value.Overlays.ToDictionary(item => item.Id, CreateItemVisual);
        EndTextEditing(true); document = value; selected = null; dragging = null; resizingCorner = -1;
        ReleaseCapture();
        Width = value.Source.Width; Height = value.Source.Height;
        foreach (var visual in visuals.Values) Children.Remove(visual.Host);
        visuals.Clear();
        foreach (var (id, visual) in prepared) { visuals.Add(id, visual); Children.Insert(0, visual.Host); }
        Refresh(); SelectionChanged?.Invoke(null);
    }

    public void SetPlaybackPosition(TimeSpan value, bool canEdit)
    {
        position = value; editingEnabled = canEdit;
        if (!canEdit) EndTextEditing(true);
        Refresh();
    }

    public void SetScreenScale(double scale)
    {
        screenScale = Math.Max(.01, scale); UpdateAdornments();
    }

    public void Select(VideoOverlay? value)
    {
        if (value != selected) EndTextEditing(true);
        selected = value; UpdateAdornments(); SelectionChanged?.Invoke(selected);
    }

    public void Refresh(bool recreate = false)
    {
        if (document is null) return;
        var current = document.Overlays.Select(item => item.Id).ToHashSet();
        foreach (var id in visuals.Keys.Where(id => !current.Contains(id)).ToArray())
        {
            Children.Remove(visuals[id].Host); visuals.Remove(id);
        }
        if (selected is not null && !current.Contains(selected.Id)) Select(null);
        foreach (var overlay in document.Overlays)
        {
            // Immutable PNG visuals can retain their decoded bitmap when only geometry changes.
            if (recreate && overlay.Kind == VideoOverlayKind.Text && overlay != editing
                && visuals.Remove(overlay.Id, out var previous))
                Children.Remove(previous.Host);
            if (!visuals.TryGetValue(overlay.Id, out var item))
            {
                item = CreateItemVisual(overlay); visuals.Add(overlay.Id, item); Children.Insert(0, item.Host);
            }
            PositionVisual(overlay, item);
            item.Host.IsVisible = IsVisibleAtPosition(overlay);
            item.Host.IsHitTestVisible = editingEnabled;
            item.Host.ZIndex = document.Overlays.IndexOf(overlay);
        }
        UpdateAdornments();
    }

    ItemVisual CreateItemVisual(VideoOverlay overlay)
    {
        var visual = VideoOverlayRenderer.CreateVisual(overlay);
        var host = new Border { Background = Brushes.Transparent, Child = visual,
            Cursor = new Cursor(StandardCursorType.SizeAll), Focusable = false };
        host.PointerPressed += (_, args) => BeginDrag(overlay, host, args);
        host.PointerMoved += (_, args) => ContinueDrag(overlay, args);
        host.PointerReleased += (_, args) =>
        {
            if (dragging != overlay) return;
            dragging = null; ReleaseCapture(); OverlayEdited?.Invoke(); args.Handled = true;
        };
        host.PointerCaptureLost += (_, _) => { if (dragging == overlay) { dragging = null; capturedPointer = null; } };
        return new(host, visual);
    }

    public void BeginTextEditing()
    {
        if (!editingEnabled || selected?.Kind != VideoOverlayKind.Text
            || !visuals.TryGetValue(selected.Id, out var item) || !IsVisibleAtPosition(selected)) return;
        EndTextEditing(true);
        // The drawn text is swapped for a text box with the same font, size, wrapping and origin.
        var box = VideoOverlayRenderer.CreateTextEditor(selected);
        editing = selected; textEditor = box; originalText = selected.Text;
        item.Visual = box; item.Host.Child = box; item.Host.Cursor = null;
        ToolTip.SetTip(box, I18n.T("Ctrl + Enter 完成，Esc 回復"));
        box.TextChanged += TextChanged;
        box.AddHandler(KeyDownEvent, TextKeyDown, RoutingStrategies.Tunnel);
        box.LostFocus += TextLostFocus;
        // Focus once the box is attached and templated.
        Dispatcher.UIThread.Post(() =>
        {
            if (textEditor != box) return;
            box.Focus(); box.SelectAll();
        }, DispatcherPriority.Loaded);
        UpdateAdornments();
    }

    public void EndTextEditing(bool commit)
    {
        if (textEditor is not { } box || endingText) return;
        endingText = true;
        try
        {
            box.TextChanged -= TextChanged; box.RemoveHandler(KeyDownEvent, TextKeyDown);
            box.LostFocus -= TextLostFocus;
            if (!commit && editing is not null) editing.Text = originalText;
            ToolTip.SetTip(box, null);
            if (editing is not null && visuals.TryGetValue(editing.Id, out var item) && item.Visual == box)
            {
                var visual = VideoOverlayRenderer.CreateVisual(editing);
                item.Visual = visual; item.Host.Child = visual;
                item.Host.Cursor = new Cursor(StandardCursorType.SizeAll);
                PositionVisual(editing, item);
            }
            textEditor = null; editing = null;
        }
        finally { endingText = false; }
        UpdateAdornments(); OverlayEdited?.Invoke();
    }

    public void DeleteSelected()
    {
        if (!editingEnabled || selected is null || document is null) return;
        EndTextEditing(true); var removed = selected;
        Select(null); document.Overlays.Remove(removed); Refresh(); OverlayEdited?.Invoke();
    }

    void TextChanged(object? sender, TextChangedEventArgs args)
    {
        if (editing is not null && textEditor is not null)
        {
            editing.Text = textEditor.Text ?? ""; OverlayEdited?.Invoke();
        }
    }

    void TextKeyDown(object? sender, KeyEventArgs args)
    {
        // Enter remains available to the text box and IME and inserts a new line.
        if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control))
        { EndTextEditing(true); Focus(); args.Handled = true; }
        else if (args.Key == Key.Escape)
        { EndTextEditing(false); Focus(); args.Handled = true; }
    }

    void TextLostFocus(object? sender, RoutedEventArgs args)
    {
        // Focus moving into the text box's own context menu is not the end of editing.
        if (textEditor is { IsKeyboardFocusWithin: true }) return;
        EndTextEditing(true);
    }

    void BeginDrag(VideoOverlay overlay, Border host, PointerPressedEventArgs args)
    {
        if (!editingEnabled || editing == overlay && textEditor?.IsKeyboardFocusWithin == true) return;
        if (!args.GetCurrentPoint(host).Properties.IsLeftButtonPressed) return;
        EndTextEditing(true); Select(overlay); Focus(); args.Handled = true;
        if (args.ClickCount == 2 && overlay.Kind == VideoOverlayKind.Text)
        { BeginTextEditing(); return; }
        dragging = overlay; dragOrigin = args.GetPosition(this); originalBounds = OverlayBounds(overlay);
        capturedPointer = args.Pointer; args.Pointer.Capture(host);
    }

    void ContinueDrag(VideoOverlay overlay, PointerEventArgs args)
    {
        if (dragging != overlay || capturedPointer is null || document is null) return;
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { dragging = null; ReleaseCapture(); return; }
        var point = args.GetPosition(this);
        overlay.X = Math.Clamp(originalBounds.X + point.X - dragOrigin.X, 0, Math.Max(0, document.Source.Width - overlay.Width));
        overlay.Y = Math.Clamp(originalBounds.Y + point.Y - dragOrigin.Y, 0, Math.Max(0, document.Source.Height - overlay.Height));
        PositionVisual(overlay, visuals[overlay.Id]); UpdateAdornments(); OverlayEdited?.Invoke();
        args.Handled = true;
    }

    void ResizeSelected(int corner, Point point)
    {
        if (!editingEnabled || selected is not { } overlay || document is null) return;
        var delta = point - dragOrigin;
        var moveRight = corner is 1 or 3; var moveBottom = corner is 2 or 3;
        var anchorX = moveRight ? originalBounds.Left : originalBounds.Right;
        var anchorY = moveBottom ? originalBounds.Top : originalBounds.Bottom;
        var widthRatio = (originalBounds.Width + (moveRight ? delta.X : -delta.X)) / originalBounds.Width;
        var heightRatio = (originalBounds.Height + (moveBottom ? delta.Y : -delta.Y)) / originalBounds.Height;
        var requestedScale = Math.Abs(widthRatio - 1) >= Math.Abs(heightRatio - 1) ? widthRatio : heightRatio;
        var availableWidth = moveRight ? document.Source.Width - anchorX : anchorX;
        var availableHeight = moveBottom ? document.Source.Height - anchorY : anchorY;
        var maximumScale = Math.Min(availableWidth / originalBounds.Width, availableHeight / originalBounds.Height);
        var minimumScale = Math.Max(1 / originalBounds.Width, 1 / originalBounds.Height);
        if (overlay.Kind == VideoOverlayKind.Text)
        {
            minimumScale = Math.Max(minimumScale, 8 / originalFontSize);
            maximumScale = Math.Min(maximumScale, 240 / originalFontSize);
        }
        if (maximumScale <= 0 || maximumScale < minimumScale) return;
        var scale = Math.Clamp(requestedScale, minimumScale, maximumScale);
        var width = originalBounds.Width * scale; var height = originalBounds.Height * scale;
        overlay.X = Math.Clamp(moveRight ? anchorX : anchorX - width, 0, document.Source.Width - 1);
        overlay.Y = Math.Clamp(moveBottom ? anchorY : anchorY - height, 0, document.Source.Height - 1);
        overlay.Width = Math.Clamp(width, 1, document.Source.Width - overlay.X);
        overlay.Height = Math.Clamp(height, 1, document.Source.Height - overlay.Y);
        if (overlay.Kind == VideoOverlayKind.Text)
        {
            overlay.FontSize = Math.Clamp(originalFontSize * scale, 8, 240);
            if (visuals[overlay.Id].Visual is TextBox box) box.FontSize = overlay.FontSize;
        }
        PositionVisual(overlay, visuals[overlay.Id]); UpdateAdornments(); OverlayEdited?.Invoke();
    }

    void ReleaseCapture()
    {
        var pointer = capturedPointer; capturedPointer = null;
        pointer?.Capture(null);
    }

    void PositionVisual(VideoOverlay overlay, ItemVisual item)
    {
        SetLeft(item.Host, overlay.X); SetTop(item.Host, overlay.Y);
        item.Host.Width = overlay.Width; item.Host.Height = overlay.Height;
        item.Visual.Width = overlay.Width; item.Visual.Height = overlay.Height;
        // The drawn visual reads the overlay when rendering; font or color changes need a repaint.
        item.Visual.InvalidateVisual();
    }

    bool IsVisibleAtPosition(VideoOverlay overlay) => document is not null
        && position >= document.TrimStart && position < document.TrimEnd
        && (overlay.ShowEntireVideo || (position >= overlay.Start && position < overlay.End));

    void UpdateAdornments()
    {
        var show = editingEnabled && selected is not null && IsVisibleAtPosition(selected);
        outline.IsVisible = show;
        foreach (var handle in handles) handle.IsVisible = show && !IsEditingText;
        if (!show || selected is null) return;
        var size = 8 / screenScale;
        outline.BorderThickness = new Thickness(1 / screenScale);
        SetLeft(outline, selected.X); SetTop(outline, selected.Y);
        outline.Width = selected.Width; outline.Height = selected.Height;
        foreach (var handle in handles)
        {
            var corner = (int)handle.Tag!;
            handle.Width = size; handle.Height = size;
            handle.BorderThickness = new Thickness(1 / screenScale);
            var x = selected.X + (corner is 1 or 3 ? selected.Width : 0);
            var y = selected.Y + (corner is 2 or 3 ? selected.Height : 0);
            SetLeft(handle, Math.Clamp(x - size / 2, 0, Math.Max(0, Width - size)));
            SetTop(handle, Math.Clamp(y - size / 2, 0, Math.Max(0, Height - size)));
        }
    }

    static Rect OverlayBounds(VideoOverlay overlay) => new(overlay.X, overlay.Y, overlay.Width, overlay.Height);
}

using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SnipFlow.Services;

namespace SnipFlow.Video;

/// <summary>The preview uses source-pixel coordinates and the export renderer's visuals.</summary>
internal sealed class VideoEditorCanvas : Canvas
{
    sealed class ItemVisual(Border host, FrameworkElement visual)
    {
        internal Border Host { get; } = host;
        internal FrameworkElement Visual { get; } = visual;
    }

    readonly Dictionary<Guid, ItemVisual> visuals = [];
    readonly Border outline = new() { Background = Brushes.Transparent, IsHitTestVisible = false };
    readonly List<Thumb> handles = [];
    VideoEditDocument? document;
    VideoOverlay? selected, dragging, editing;
    TextBox? textEditor;
    string originalText = "";
    Point dragOrigin;
    Rect originalBounds;
    TimeSpan position;
    bool editingEnabled = true, endingText;
    double screenScale = 1, originalFontSize;

    public event Action<VideoOverlay?>? SelectionChanged;
    public event Action? OverlayEdited;
    public VideoOverlay? Selected => selected;
    public bool IsEditingText => textEditor is not null;

    public VideoEditorCanvas()
    {
        Background = Brushes.Transparent; ClipToBounds = true; Focusable = true;
        outline.BorderBrush = BrushResource("Accent", "#E8E8E8");
        outline.BorderThickness = new Thickness(1); SetZIndex(outline, 100);
        Children.Add(outline);
        foreach (var corner in new[] { 0, 1, 2, 3 })
        {
            var thumb = new Thumb { Tag = corner, Width = 8, Height = 8, Focusable = false,
                Cursor = corner is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, BrushResource("Accent", "#E8E8E8"));
            border.SetValue(Border.BorderBrushProperty, BrushResource("Panel", "#202020"));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
            thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            thumb.DragStarted += (_, _) =>
            {
                EndTextEditing(true);
                if (selected is null || !editingEnabled) return;
                dragOrigin = Mouse.GetPosition(this);
                originalBounds = Bounds(selected);
                originalFontSize = selected.FontSize;
            };
            thumb.DragDelta += (_, _) => ResizeSelected(corner);
            thumb.DragCompleted += (_, _) => OverlayEdited?.Invoke();
            SetZIndex(thumb, 101); handles.Add(thumb); Children.Add(thumb);
        }
        MouseLeftButtonDown += (_, args) =>
        {
            if (!editingEnabled || args.Handled) return;
            EndTextEditing(true); Select(null); Focus(); args.Handled = true;
        };
        LostMouseCapture += (_, _) => dragging = null;
        UpdateAdornments();
    }

    public void SetDocument(VideoEditDocument value)
    {
        // Decoder failures must not discard the current project's visible objects.
        var prepared = value.Overlays.ToDictionary(item => item.Id, CreateItemVisual);
        EndTextEditing(true); document = value; selected = null; dragging = null;
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
            item.Host.Visibility = IsVisibleAtPosition(overlay) ? Visibility.Visible : Visibility.Collapsed;
            item.Host.IsHitTestVisible = editingEnabled;
            SetZIndex(item.Host, document.Overlays.IndexOf(overlay));
        }
        UpdateAdornments();
    }

    ItemVisual CreateItemVisual(VideoOverlay overlay)
    {
        var visual = VideoOverlayRenderer.CreateVisual(overlay);
        visual.IsHitTestVisible = false;
        var host = new Border { Background = Brushes.Transparent, Child = visual,
            Cursor = Cursors.SizeAll, Focusable = false };
        host.MouseLeftButtonDown += (_, args) => BeginDrag(overlay, host, args);
        host.MouseMove += (_, args) => ContinueDrag(overlay, host, args);
        host.MouseLeftButtonUp += (_, args) =>
        {
            if (dragging != overlay) return;
            dragging = null; host.ReleaseMouseCapture(); OverlayEdited?.Invoke(); args.Handled = true;
        };
        return new(host, visual);
    }

    public void BeginTextEditing()
    {
        if (!editingEnabled || selected?.Kind != VideoOverlayKind.Text
            || !visuals.TryGetValue(selected.Id, out var item)
            || item.Visual is not TextBox box || !IsVisibleAtPosition(selected)) return;
        EndTextEditing(true);
        editing = selected; textEditor = box; originalText = selected.Text;
        box.IsReadOnly = false; box.IsHitTestVisible = true; box.Focusable = true;
        box.CaretBrush = box.Foreground; box.MaxLength = 8000;
        box.ToolTip = I18n.T("Ctrl + Enter 完成，Esc 回復");
        box.TextChanged += TextChanged;
        box.PreviewKeyDown += TextKeyDown;
        box.LostKeyboardFocus += TextLostFocus;
        box.Focus(); box.SelectAll();
        UpdateAdornments();
    }

    public void EndTextEditing(bool commit)
    {
        if (textEditor is not { } box || endingText) return;
        endingText = true;
        try
        {
            box.TextChanged -= TextChanged; box.PreviewKeyDown -= TextKeyDown;
            box.LostKeyboardFocus -= TextLostFocus;
            if (!commit && editing is not null) { editing.Text = originalText; box.Text = originalText; }
            box.IsReadOnly = true; box.IsHitTestVisible = false; box.Focusable = false;
            box.Select(0, 0); box.ScrollToHome(); box.ToolTip = null;
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

    void TextChanged(object sender, TextChangedEventArgs args)
    {
        if (editing is not null && textEditor is not null)
        {
            editing.Text = textEditor.Text; OverlayEdited?.Invoke();
        }
    }

    void TextKeyDown(object sender, KeyEventArgs args)
    {
        // Enter remains available to WPF/IME and inserts a new line.
        if (args.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { EndTextEditing(true); Focus(); args.Handled = true; }
        else if (args.Key == Key.Escape)
        { EndTextEditing(false); Focus(); args.Handled = true; }
    }

    void TextLostFocus(object sender, KeyboardFocusChangedEventArgs args) => EndTextEditing(true);

    void BeginDrag(VideoOverlay overlay, Border host, MouseButtonEventArgs args)
    {
        if (!editingEnabled || editing == overlay && textEditor?.IsKeyboardFocusWithin == true) return;
        EndTextEditing(true); Select(overlay); Focus(); args.Handled = true;
        if (args.ClickCount == 2 && overlay.Kind == VideoOverlayKind.Text)
        { BeginTextEditing(); return; }
        dragging = overlay; dragOrigin = args.GetPosition(this); originalBounds = Bounds(overlay);
        host.CaptureMouse();
    }

    void ContinueDrag(VideoOverlay overlay, Border host, MouseEventArgs args)
    {
        if (dragging != overlay || !host.IsMouseCaptured || document is null) return;
        if (args.LeftButton != MouseButtonState.Pressed) { dragging = null; host.ReleaseMouseCapture(); return; }
        var point = args.GetPosition(this);
        overlay.X = Math.Clamp(originalBounds.X + point.X - dragOrigin.X, 0, Math.Max(0, document.Source.Width - overlay.Width));
        overlay.Y = Math.Clamp(originalBounds.Y + point.Y - dragOrigin.Y, 0, Math.Max(0, document.Source.Height - overlay.Height));
        PositionVisual(overlay, visuals[overlay.Id]); UpdateAdornments(); OverlayEdited?.Invoke();
        args.Handled = true;
    }

    void ResizeSelected(int corner)
    {
        if (!editingEnabled || selected is not { } overlay || document is null) return;
        var delta = Mouse.GetPosition(this) - dragOrigin;
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

    void PositionVisual(VideoOverlay overlay, ItemVisual item)
    {
        SetLeft(item.Host, overlay.X); SetTop(item.Host, overlay.Y);
        item.Host.Width = overlay.Width; item.Host.Height = overlay.Height;
        item.Visual.Width = overlay.Width; item.Visual.Height = overlay.Height;
    }

    bool IsVisibleAtPosition(VideoOverlay overlay) => document is not null
        && position >= document.TrimStart && position < document.TrimEnd
        && (overlay.ShowEntireVideo || (position >= overlay.Start && position < overlay.End));

    void UpdateAdornments()
    {
        var show = editingEnabled && selected is not null && IsVisibleAtPosition(selected);
        outline.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var handle in handles) handle.Visibility = show && !IsEditingText ? Visibility.Visible : Visibility.Collapsed;
        if (!show || selected is null) return;
        var size = 8 / screenScale;
        outline.BorderThickness = new Thickness(1 / screenScale);
        SetLeft(outline, selected.X); SetTop(outline, selected.Y);
        outline.Width = selected.Width; outline.Height = selected.Height;
        foreach (var handle in handles)
        {
            var corner = (int)handle.Tag;
            handle.Width = size; handle.Height = size;
            var x = selected.X + (corner is 1 or 3 ? selected.Width : 0);
            var y = selected.Y + (corner is 2 or 3 ? selected.Height : 0);
            SetLeft(handle, Math.Clamp(x - size / 2, 0, Math.Max(0, Width - size)));
            SetTop(handle, Math.Clamp(y - size / 2, 0, Math.Max(0, Height - size)));
        }
    }

    static Rect Bounds(VideoOverlay overlay) => new(overlay.X, overlay.Y, overlay.Width, overlay.Height);
    static Brush BrushResource(string key, string fallback)
        => Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
}

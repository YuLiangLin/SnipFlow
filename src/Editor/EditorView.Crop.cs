using Avalonia.Automation;
using Avalonia.Input;

namespace SnipFlow.Editor;

public sealed partial class EditorView
{
    private const string CropHint = "拖曳黑色邊界裁剪 · 拖曳圖片調整內容 · Ctrl + 滾輪縮放圖片 · Enter 完成 · Esc 取消";
    private AnnotationItem? _cropTarget;
    private AnnotationItem? _cropDraft;
    private AnnotationItem? _cropDragSource;
    private EditorState? _beforeCrop;
    private SelectionHandle _cropHandle;
    private Point _cropAnchor;
    private Border? _cropToolbar;
    private readonly TextBlock _cropSizeLabel = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(Button Button, string Label)> _cropButtons = new();
    private Slider? _cropScaleSlider;
    private Button? _cropZoomOut;
    private Button? _cropZoomIn;
    private readonly TextBlock _cropZoomLabel = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _cropZoomPercent = new() { FontSize = 11, Width = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private bool _syncCropScale;
    private double _cropMinimumZoom = 1;
    private double _cropMaximumZoom = 1600;

    public bool IsImageCropping => _cropDraft is not null;
    internal double ImageCropZoom
    {
        get
        {
            var viewport = _cropDraft?.ImageCrop ?? ImageCropGeometry.Full;
            var percentage = 100 / Math.Max(viewport.Width, viewport.Height);
            return double.IsFinite(percentage) && percentage > 0 ? percentage : 100;
        }
    }
    private bool CropHasChanges => _cropDraft is not null && _cropTarget is not null && !_cropDraft.ContentEquals(_cropTarget);

    private bool TryBeginImageCropAt(Point point, int clickCount, KeyModifiers modifiers)
    {
        if (!IsCollage || _tool != AnnotationTool.Select || clickCount != 2 || modifiers != KeyModifiers.None)
            return false;
        var hit = _annotations.LastOrDefault(candidate => candidate.HitTest(point, 6 / _zoom));
        if (hit is not { Tool: AnnotationTool.Image, Image: not null }) return false;
        SelectItem(hit);
        return BeginImageCrop();
    }

    internal bool BeginImageCrop()
    {
        VerifyAccess();
        if (IsImageCropping) return true;
        if (!IsCollage || _selection.Count != 1 || _selected is not { Tool: AnnotationTool.Image, Image: not null } target)
            return false;
        var frame = target.Bounds;
        if (!ImageCropGeometry.IsValid(target.ImageCrop ?? ImageCropGeometry.Full)
            || !double.IsFinite(frame.X) || !double.IsFinite(frame.Y) || !double.IsFinite(frame.Width) || !double.IsFinite(frame.Height)
            || frame.Width <= 0 || frame.Height <= 0)
            return false;
        var full = FullImageBounds(target);
        var visible = frame.Intersect(new Rect(0, 0, PixelWidth, PixelHeight));
        if (!double.IsFinite(full.X) || !double.IsFinite(full.Y) || !double.IsFinite(full.Width) || !double.IsFinite(full.Height)
            || full.Width <= 0 || full.Height <= 0 || visible.Width <= 0 || visible.Height <= 0)
            return false;
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
        ClearImageDropPreview();
        _beforeCrop = Snapshot();
        _cropTarget = target;
        _cropDraft = target.Clone();
        if (visible != frame)
        {
            _cropDraft.Start = visible.TopLeft;
            _cropDraft.End = visible.BottomRight;
            _cropDraft.ImageCrop = SourceCropForFrame(visible, full);
        }
        RefreshCropUi();
        RaiseChanged();
        return true;
    }

    internal void CommitImageCrop()
    {
        VerifyAccess();
        if (_cropDraft is null || _cropTarget is null) return;
        var target = _cropTarget;
        var draft = _cropDraft;
        var before = _beforeCrop;
        var changed = CropHasChanges;
        EndImageCrop();
        if (changed && before is not null)
        {
            target.CopyFrom(draft);
            RefreshMosaics();
            _undo.Push(before);
            _redo.Clear();
            FinishMutation();
            RaiseSelectionChanged();
        }
        else RaiseChanged();
    }

    internal void CancelImageCrop()
    {
        VerifyAccess();
        if (!IsImageCropping) return;
        EndImageCrop();
        RaiseChanged();
    }

    internal void ResetImageCrop()
    {
        VerifyAccess();
        if (_cropDraft is not { Image: not null } draft) return;
        CompleteGesture();
        var frame = draft.Bounds;
        var relativeAspect = (frame.Width / frame.Height) / ((double)draft.Image.PixelSize.Width / draft.Image.PixelSize.Height);
        var viewport = relativeAspect >= 1
            ? new Rect((1 - relativeAspect) / 2, 0, relativeAspect, 1)
            : new Rect(0, (1 - 1 / relativeAspect) / 2, 1, 1 / relativeAspect);
        if (!ImageCropGeometry.IsValid(viewport)) return;
        draft.ImageCrop = viewport == ImageCropGeometry.Full ? null : viewport;
        RefreshCropUi();
        RaiseChanged();
    }

    internal void SetImageCropZoom(double percentage)
    {
        VerifyAccess();
        if (!double.IsFinite(percentage) || percentage <= 0)
            throw new ArgumentOutOfRangeException(nameof(percentage));
        if (_cropDraft is not { } draft) return;
        CompleteGesture();
        var current = draft.ImageCrop ?? ImageCropGeometry.Full;
        var range = ImageCropZoomRange();
        percentage = Math.Clamp(percentage, range.Minimum, range.Maximum);
        if (Math.Abs(percentage - ImageCropZoom) <= Math.Max(1, ImageCropZoom) * 1e-12)
        {
            RefreshCropUi();
            return;
        }
        var fill = Math.Max(current.Width, current.Height);
        var width = (current.Width / fill) * (100 / percentage);
        var height = (current.Height / fill) * (100 / percentage);
        var viewport = new Rect(current.X + (current.Width - width) / 2,
            current.Y + (current.Height - height) / 2, width, height);
        viewport = ClampImagePosition(viewport, draft.Bounds);
        if (!ImageCropGeometry.IsValid(viewport)) return;
        draft.ImageCrop = viewport == ImageCropGeometry.Full ? null : viewport;
        RefreshCropUi();
        RaiseChanged();
    }

    private (double Minimum, double Maximum) ImageCropZoomRange() => (_cropMinimumZoom, _cropMaximumZoom);

    private static Rect ClampImagePosition(Rect viewport, Rect frame)
    {
        // Keep at least one document pixel (or the whole image when smaller) reachable for dragging.
        var visibleX = Math.Min(1, Math.Min(viewport.Width, viewport.Width / Math.Max(1, frame.Width)));
        var visibleY = Math.Min(1, Math.Min(viewport.Height, viewport.Height / Math.Max(1, frame.Height)));
        return new Rect(Math.Clamp(viewport.X, -viewport.Width + visibleX, 1 - visibleX),
            Math.Clamp(viewport.Y, -viewport.Height + visibleY, 1 - visibleY), viewport.Width, viewport.Height);
    }

    private void EndImageCrop()
    {
        EndImageCropGesture();
        _cropTarget = _cropDraft = null;
        _beforeCrop = null;
        _hoverHandle = SelectionHandle.None;
        _cropMinimumZoom = 1;
        _cropMaximumZoom = 1600;
        RefreshCropUi();
    }

    private Border CreateCropToolbar()
    {
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        _cropSizeLabel.Margin = new Thickness(8, 0, 12, 0);
        actions.Children.Add(_cropSizeLabel);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        zoom.Children.Add(_cropZoomLabel);
        Button ZoomButton(string content, Action action)
        {
            var button = new Button { Content = content, Width = 26, Padding = new Thickness(0), Margin = new Thickness(2), Foreground = ThemeService.Brush("Ink") };
            button.Click += (_, _) => action();
            return button;
        }
        _cropZoomOut = ZoomButton("−", () => SetImageCropZoom(ImageCropZoom / 1.1));
        _cropZoomIn = ZoomButton("+", () => SetImageCropZoom(ImageCropZoom * 1.1));
        _cropScaleSlider = new Slider
        {
            Width = 80, Minimum = Math.Log2(0.01), Maximum = 4, SmallChange = 0.1, LargeChange = 0.5,
            Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center
        };
        _cropScaleSlider.PropertyChanged += (_, e) =>
        {
            if (!_syncCropScale && IsImageCropping && e.Property == Slider.ValueProperty)
                SetImageCropZoom(100 * Math.Pow(2, _cropScaleSlider.Value));
        };
        zoom.Children.Add(_cropZoomOut);
        zoom.Children.Add(_cropScaleSlider);
        zoom.Children.Add(_cropZoomIn);
        zoom.Children.Add(_cropZoomPercent);
        actions.Children.Add(zoom);
        void Add(string label, Action action, bool primary = false)
        {
            var button = new Button
            {
                Content = I18n.T(label), Padding = new Thickness(10, 6), Margin = new Thickness(2),
                Background = ThemeService.Brush(primary ? "Accent" : "Raised"),
                Foreground = ThemeService.Brush(primary ? "AccentInk" : "Ink")
            };
            AutomationProperties.SetName(button, I18n.T(label));
            button.Click += (_, _) => { action(); Focus(); };
            actions.Children.Add(button);
            _cropButtons.Add((button, label));
        }
        Add("重設圖片", ResetImageCrop);
        Add("取消裁剪", CancelImageCrop);
        Add("完成裁剪", CommitImageCrop, primary: true);
        return new Border
        {
            Child = actions, Background = ThemeService.Brush("Panel"), BorderBrush = ThemeService.Brush("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 12, 12, 0), IsVisible = false
        };
    }

    private void RefreshCropUi()
    {
        if (IsImageCropping)
        {
            _cropMinimumZoom = Math.Min(_cropMinimumZoom, ImageCropZoom);
            _cropMaximumZoom = Math.Max(_cropMaximumZoom, ImageCropZoom);
        }
        if (_cropToolbar is not null) _cropToolbar.IsVisible = IsImageCropping;
        _imageBadge.IsVisible = _zoomBadge.IsVisible = HasImage && !IsImageCropping;
        if (_cropScaleSlider is not null)
        {
            _syncCropScale = true;
            try
            {
                var range = ImageCropZoomRange();
                _cropScaleSlider.Maximum = Math.Log2(range.Maximum / 100);
                _cropScaleSlider.Minimum = Math.Log2(range.Minimum / 100);
                _cropScaleSlider.Value = Math.Log2(ImageCropZoom / 100);
                _cropZoomOut!.IsEnabled = ImageCropZoom > range.Minimum + 0.000001;
                _cropZoomIn!.IsEnabled = ImageCropZoom < range.Maximum - 0.000001;
            }
            finally { _syncCropScale = false; }
        }
        RefreshLanguage();
        UpdateCursor();
        InvalidateScene();
    }

    private void RefreshImageCropLanguage()
    {
        _cropSizeLabel.Foreground = ThemeService.Brush("Muted");
        _cropZoomLabel.Foreground = _cropZoomPercent.Foreground = ThemeService.Brush("Ink");
        _cropZoomLabel.Text = I18n.T("圖片縮放");
        _cropZoomPercent.Text = ImageCropZoom.ToString(ImageCropZoom < 10 ? "0.#'%'" : "0'%'");
        if (_cropScaleSlider is not null)
        {
            AutomationProperties.SetName(_cropScaleSlider, I18n.T("圖片縮放"));
            AutomationProperties.SetName(_cropZoomOut!, I18n.T("縮小圖片"));
            AutomationProperties.SetName(_cropZoomIn!, I18n.T("放大圖片"));
            ToolTip.SetTip(_cropScaleSlider, I18n.T("裁剪框固定，縮小圖片可留白；Ctrl + 滾輪也可縮放"));
            ToolTip.SetTip(_cropZoomOut!, I18n.T("縮小圖片"));
            ToolTip.SetTip(_cropZoomIn!, I18n.T("放大圖片"));
        }
        foreach (var (button, label) in _cropButtons)
        {
            button.Content = I18n.T(label);
            AutomationProperties.SetName(button, I18n.T(label));
        }
        if (_cropDraft is { Image: not null } draft)
        {
            _cropSizeLabel.Text = I18n.F("裁剪框 {0} × {1} px",
                Math.Max(1, (int)Math.Round(draft.Bounds.Width)),
                Math.Max(1, (int)Math.Round(draft.Bounds.Height)));
        }
    }

    private static Rect FullImageBounds(AnnotationItem item)
    {
        var crop = item.ImageCrop ?? ImageCropGeometry.Full;
        var frame = item.Bounds;
        var width = frame.Width / crop.Width;
        var height = frame.Height / crop.Height;
        return new Rect(frame.X - crop.X * width, frame.Y - crop.Y * height, width, height);
    }

    private static Rect SourceCropForFrame(Rect frame, Rect full)
    {
        return new Rect((frame.Left - full.X) / full.Width, (frame.Top - full.Y) / full.Height,
            frame.Width / full.Width, frame.Height / full.Height);
    }

    private static IEnumerable<(SelectionHandle Handle, Point Position)> CropHandles(Rect frame)
    {
        var x = frame.X + frame.Width / 2;
        var y = frame.Y + frame.Height / 2;
        yield return (SelectionHandle.TopLeft, frame.TopLeft);
        yield return (SelectionHandle.Top, new Point(x, frame.Top));
        yield return (SelectionHandle.TopRight, frame.TopRight);
        yield return (SelectionHandle.Right, new Point(frame.Right, y));
        yield return (SelectionHandle.BottomRight, frame.BottomRight);
        yield return (SelectionHandle.Bottom, new Point(x, frame.Bottom));
        yield return (SelectionHandle.BottomLeft, frame.BottomLeft);
        yield return (SelectionHandle.Left, new Point(frame.Left, y));
    }

    private SelectionHandle HitImageCrop(Point point)
    {
        if (_cropDraft is null) return SelectionHandle.None;
        var nearest = CropHandles(_cropDraft.Bounds).OrderBy(handle => ((Vector)(handle.Position - point)).SquaredLength).First();
        if (Point.Distance(nearest.Position, point) <= 10 / _zoom) return nearest.Handle;
        return _cropDraft.Bounds.Contains(point) ? SelectionHandle.Move : SelectionHandle.None;
    }

    private void BeginImageCropGesture(SelectionHandle handle, Point point)
    {
        _cropDragSource = _cropDraft?.Clone();
        _cropHandle = handle;
        _cropAnchor = point;
    }

    private void UpdateImageCropGesture(Point point)
    {
        if (_cropDraft is null || _cropDragSource is not { Image: not null } source) return;
        var delta = point - _cropAnchor;
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return;
        var crop = source.ImageCrop ?? ImageCropGeometry.Full;
        var frame = source.Bounds;
        if (_cropHandle == SelectionHandle.Move)
        {
            var viewport = ClampImagePosition(new Rect(crop.X - delta.X * crop.Width / frame.Width,
                crop.Y - delta.Y * crop.Height / frame.Height, crop.Width, crop.Height), frame);
            if (!ImageCropGeometry.IsValid(viewport)) return;
            _cropDraft.ImageCrop = viewport;
        }
        else
        {
            var full = FullImageBounds(source);
            var limits = new Rect(0, 0, PixelWidth, PixelHeight);
            var minimumWidth = Math.Min(frame.Width, Math.Max(2, full.Width / source.Image.PixelSize.Width));
            var minimumHeight = Math.Min(frame.Height, Math.Max(2, full.Height / source.Image.PixelSize.Height));
            var left = frame.Left; var top = frame.Top; var right = frame.Right; var bottom = frame.Bottom;
            static double ClampEdge(double value, double min, double max) => Math.Clamp(value, min, Math.Max(min, max));
            if (_cropHandle is SelectionHandle.Left or SelectionHandle.TopLeft or SelectionHandle.BottomLeft)
                left = ClampEdge(left + delta.X, limits.Left, right - minimumWidth);
            if (_cropHandle is SelectionHandle.Right or SelectionHandle.TopRight or SelectionHandle.BottomRight)
                right = ClampEdge(right + delta.X, left + minimumWidth, limits.Right);
            if (_cropHandle is SelectionHandle.Top or SelectionHandle.TopLeft or SelectionHandle.TopRight)
                top = ClampEdge(top + delta.Y, limits.Top, bottom - minimumHeight);
            if (_cropHandle is SelectionHandle.Bottom or SelectionHandle.BottomLeft or SelectionHandle.BottomRight)
                bottom = ClampEdge(bottom + delta.Y, top + minimumHeight, limits.Bottom);
            var nextFrame = new Rect(new Point(left, top), new Point(right, bottom));
            var viewport = SourceCropForFrame(nextFrame, full);
            if (!ImageCropGeometry.IsValid(viewport)) return;
            _cropDraft.Start = nextFrame.TopLeft;
            _cropDraft.End = nextFrame.BottomRight;
            _cropDraft.ImageCrop = viewport;
        }
        if (_cropDraft.ImageCrop == ImageCropGeometry.Full) _cropDraft.ImageCrop = null;
        RefreshCropUi();
        RaiseChanged();
    }

    private void EndImageCropGesture()
    {
        _cropDragSource = null;
        _cropHandle = SelectionHandle.None;
        ReleaseViewportCapture();
    }

    internal bool HandleImageCropKey(KeyEventArgs e)
    {
        if (!IsImageCropping) return false;
        if (_cropScaleSlider is not null && IsWithin(_cropScaleSlider, e.Source)
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            return false;
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape) CancelImageCrop();
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter) CommitImageCrop();
        else if ((e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = e.KeyModifiers == KeyModifiers.Shift ? 10 : 1;
            BeginImageCropGesture(SelectionHandle.Move, default);
            UpdateImageCropGesture(new Point(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0));
            EndImageCropGesture();
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Delete or Key.Back) { }
        else return false;
        e.Handled = true;
        return true;
    }

    private void DrawImageCrop(DrawingContext context, Rect canvas)
    {
        if (_cropDraft is not { Image: not null } draft) return;
        using var clip = context.PushClip(canvas);
        var frame = draft.Bounds;
        var dim = new SolidColorBrush(Color.FromArgb(85, 0, 0, 0));
        context.DrawRectangle(dim, null, new Rect(canvas.Left, canvas.Top, canvas.Width, Math.Max(0, frame.Top - canvas.Top)));
        context.DrawRectangle(dim, null, new Rect(canvas.Left, frame.Bottom, canvas.Width, Math.Max(0, canvas.Bottom - frame.Bottom)));
        context.DrawRectangle(dim, null, new Rect(canvas.Left, frame.Top, Math.Max(0, frame.Left - canvas.Left), frame.Height));
        context.DrawRectangle(dim, null, new Rect(frame.Right, frame.Top, Math.Max(0, canvas.Right - frame.Right), frame.Height));
        var full = FullImageBounds(draft);
        void Ghost(Rect area)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            using (context.PushClip(area))
            using (context.PushOpacity(0.35)) context.DrawImage(draft.Image, ImageCropGeometry.SourceRect(draft.Image, null), full);
        }
        Ghost(new Rect(full.Left, full.Top, full.Width, Math.Max(0, frame.Top - full.Top)));
        Ghost(new Rect(full.Left, frame.Bottom, full.Width, Math.Max(0, full.Bottom - frame.Bottom)));
        Ghost(new Rect(full.Left, frame.Top, Math.Max(0, frame.Left - full.Left), frame.Height));
        Ghost(new Rect(frame.Right, frame.Top, Math.Max(0, full.Right - frame.Right), frame.Height));
        var white = new Pen(Brushes.White, 3 / _zoom);
        var black = new Pen(Brushes.Black, 1 / _zoom);
        context.DrawRectangle(null, white, frame);
        context.DrawRectangle(null, black, frame);
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1 / _zoom);
        for (var i = 1; i < 3; i++)
        {
            var x = frame.X + frame.Width * i / 3;
            var y = frame.Y + frame.Height * i / 3;
            context.DrawLine(guide, new Point(x, frame.Top), new Point(x, frame.Bottom));
            context.DrawLine(guide, new Point(frame.Left, y), new Point(frame.Right, y));
        }
        foreach (var (handle, position) in CropHandles(frame))
        {
            var horizontal = handle is SelectionHandle.Top or SelectionHandle.Bottom;
            var vertical = handle is SelectionHandle.Left or SelectionHandle.Right;
            var size = new Size((vertical ? 4 : 12) / _zoom, (horizontal ? 4 : 12) / _zoom);
            var rect = new Rect(position.X - size.Width / 2, position.Y - size.Height / 2, size.Width, size.Height);
            context.DrawRectangle(Brushes.Black, new Pen(Brushes.White, 1 / _zoom), rect);
        }
    }
}

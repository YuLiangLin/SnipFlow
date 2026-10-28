using Avalonia.Automation;
using Avalonia.Input;

namespace SnipFlow.Editor;

public sealed partial class EditorView
{
    private const string CropHint = "拖曳黑色邊界裁剪 · 拖曳圖片調整內容 · Enter 完成 · Esc 取消";
    private AnnotationItem? _cropTarget;
    private AnnotationItem? _cropDraft;
    private AnnotationItem? _cropDragSource;
    private EditorState? _beforeCrop;
    private SelectionHandle _cropHandle;
    private Point _cropAnchor;
    private Border? _cropToolbar;
    private readonly TextBlock _cropSizeLabel = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(Button Button, string Label)> _cropButtons = new();

    public bool IsImageCropping => _cropDraft is not null;
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
        EndImageCropGesture();
        var full = FullImageBounds(draft);
        var scale = Math.Min(1, Math.Min(PixelWidth / full.Width, PixelHeight / full.Height));
        var width = full.Width * scale;
        var height = full.Height * scale;
        var left = Math.Clamp(full.X, 0, Math.Max(0, PixelWidth - width));
        var top = Math.Clamp(full.Y, 0, Math.Max(0, PixelHeight - height));
        draft.Start = new Point(left, top);
        draft.End = new Point(left + width, top + height);
        draft.ImageCrop = null;
        RefreshCropUi();
        RaiseChanged();
    }

    private void EndImageCrop()
    {
        EndImageCropGesture();
        _cropTarget = _cropDraft = null;
        _beforeCrop = null;
        _hoverHandle = SelectionHandle.None;
        RefreshCropUi();
    }

    private Border CreateCropToolbar()
    {
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        _cropSizeLabel.Margin = new Thickness(8, 0, 12, 0);
        actions.Children.Add(_cropSizeLabel);
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
        if (_cropToolbar is not null) _cropToolbar.IsVisible = IsImageCropping;
        _imageBadge.IsVisible = _zoomBadge.IsVisible = HasImage && !IsImageCropping;
        RefreshLanguage();
        UpdateCursor();
        InvalidateScene();
    }

    private void RefreshImageCropLanguage()
    {
        _cropSizeLabel.Foreground = ThemeService.Brush("Muted");
        foreach (var (button, label) in _cropButtons)
        {
            button.Content = I18n.T(label);
            AutomationProperties.SetName(button, I18n.T(label));
        }
        if (_cropDraft is { Image: not null } draft)
        {
            var crop = draft.ImageCrop ?? ImageCropGeometry.Full;
            _cropSizeLabel.Text = I18n.F("可見範圍 {0} × {1} px",
                Math.Max(1, (int)Math.Round(crop.Width * draft.Image.PixelSize.Width)),
                Math.Max(1, (int)Math.Round(crop.Height * draft.Image.PixelSize.Height)));
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
        var x = Math.Clamp((frame.Left - full.X) / full.Width, 0, 1);
        var y = Math.Clamp((frame.Top - full.Y) / full.Height, 0, 1);
        return new Rect(x, y, Math.Min(frame.Width / full.Width, 1 - x), Math.Min(frame.Height / full.Height, 1 - y));
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
            var left = Math.Clamp(crop.X - delta.X * crop.Width / frame.Width, 0, 1 - crop.Width);
            var top = Math.Clamp(crop.Y - delta.Y * crop.Height / frame.Height, 0, 1 - crop.Height);
            _cropDraft.ImageCrop = new Rect(left, top, crop.Width, crop.Height);
        }
        else
        {
            var full = FullImageBounds(source);
            var limits = full.Intersect(new Rect(0, 0, PixelWidth, PixelHeight));
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
            _cropDraft.Start = new Point(left, top);
            _cropDraft.End = new Point(right, bottom);
            _cropDraft.ImageCrop = SourceCropForFrame(_cropDraft.Bounds, full);
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

using System.Windows.Input;
using WpfUserControl = System.Windows.Controls.UserControl;
using WpfCanvas = System.Windows.Controls.Canvas;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfVector = System.Windows.Vector;
using WpfColor = System.Windows.Media.Color;

namespace SnipFlow.Editor;

/// <summary>
/// A pixel-coordinate annotation canvas. Display zoom never changes exported dimensions.
/// </summary>
public sealed class EditorView : WpfUserControl
{
    private readonly WpfCanvas _viewport;
    private readonly ImageSurface _surface;
    private readonly FrameworkElement _emptyState;
    private readonly Border _imageBadge;
    private readonly Border _zoomBadge;
    private readonly TextBlock _imageLabel;
    private readonly TextBlock _zoomLabel;
    private readonly List<AnnotationItem> _annotations = new();
    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();
    private BitmapSource? _image;
    private BitmapSource? _pixelSource;
    private AnnotationItem? _pending;
    private AnnotationItem? _selected;
    private EditorState? _beforeMove;
    private AnnotationTool _tool = AnnotationTool.Arrow;
    private WpfColor _color = WpfColor.FromRgb(99, 213, 197);
    private double _strokeWidth = 4;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private long _revision;
    private long _nextRevision;
    private long _savedRevision;
    private bool _fitMode = true;
    private bool _spaceDown;
    private bool _panning;
    private bool _didMove;
    private WpfPoint _panStart;
    private WpfPoint _panOrigin;
    private WpfPoint _lastImagePoint;

    public event EventHandler? Changed;

    public bool HasImage => _image is not null;
    public bool HasEdits => HasImage && (_revision != _savedRevision || _pending is not null || _didMove);
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public double ZoomFactor => _zoom;

    public double StrokeWidth
    {
        get => _strokeWidth;
        set
        {
            VerifyAccess();
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _strokeWidth = Math.Clamp(value, 1, 32);
        }
    }

    public EditorView()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = BrushFor("#0C111B");
        _viewport = new WpfCanvas
        {
            Background = BrushFor("#0C111B"),
            ClipToBounds = true
        };
        _surface = new ImageSurface(this) { Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        _viewport.Children.Add(_surface);

        var layout = new Grid { ClipToBounds = true };
        layout.Children.Add(_viewport);
        _emptyState = CreateEmptyState();
        layout.Children.Add(_emptyState);

        _imageLabel = new TextBlock { FontSize = 11, Foreground = BrushFor("#B6C2D1") };
        _imageBadge = CreateBadge(_imageLabel, HorizontalAlignment.Left);
        layout.Children.Add(_imageBadge);
        _zoomLabel = new TextBlock { FontSize = 11, Foreground = BrushFor("#EEF3F9") };
        _zoomBadge = CreateBadge(_zoomLabel, HorizontalAlignment.Right);
        layout.Children.Add(_zoomBadge);

        layout.Children.Add(new TextBlock
        {
            Text = "滾輪縮放 · 空白鍵拖曳 · Delete 刪除標註",
            FontSize = 11,
            Foreground = BrushFor("#B6C2D1"),
            Margin = new Thickness(20, 0, 20, 15),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });
        Content = layout;

        _viewport.PreviewMouseDown += OnMouseDown;
        _viewport.PreviewMouseMove += OnMouseMove;
        _viewport.PreviewMouseUp += OnMouseUp;
        _viewport.PreviewMouseWheel += OnMouseWheel;
        _viewport.LostMouseCapture += (_, _) => CompleteGesture();
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        LostKeyboardFocus += (_, _) =>
        {
            _spaceDown = false;
            UpdateCursor();
        };
        _viewport.SizeChanged += (_, _) =>
        {
            if (HasImage && _fitMode)
                FitToView();
        };
        Loaded += (_, _) =>
        {
            if (HasImage && _fitMode)
                FitToView();
        };
        UpdateCursor();
    }

    public void LoadImage(BitmapSource image)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelWidth < 1 || image.PixelHeight < 1)
            throw new ArgumentException("影像尺寸不得為零。", nameof(image));

        CancelGesture();
        _image = image.CloneCurrentValue();
        _image.Freeze();
        _pixelSource = null;
        _annotations.Clear();
        _undo.Clear();
        _redo.Clear();
        _selected = null;
        _revision = _nextRevision = _savedRevision = 0;
        _surface.Width = image.PixelWidth;
        _surface.Height = image.PixelHeight;
        _surface.Visibility = Visibility.Visible;
        _emptyState.Visibility = Visibility.Collapsed;
        _imageBadge.Visibility = _zoomBadge.Visibility = Visibility.Visible;
        _imageLabel.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px";
        _fitMode = true;
        FitToView();
        RaiseChanged();
    }

    /// <summary>Returns only image pixels and committed annotations, at the original pixel size.</summary>
    public BitmapSource ExportImage()
    {
        VerifyAccess();
        if (_image is null)
            throw new InvalidOperationException("請先開啟或擷取影像。");

        CompleteGesture();
        if (_annotations.Count == 0)
            return _image;

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
            DrawScene(context, includeSelection: false);
        // At 96 DPI, one drawing unit is one output pixel. Source DPI is deliberately
        // not used for layout; a 150% desktop capture must not shrink on export.
        var result = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    public void MarkSaved()
    {
        VerifyAccess();
        CompleteGesture();
        _savedRevision = _revision;
        RaiseChanged();
    }

    public void Undo()
    {
        VerifyAccess();
        CancelGesture();
        if (_undo.Count == 0)
            return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
        RaiseChanged();
    }

    public void Redo()
    {
        VerifyAccess();
        CancelGesture();
        if (_redo.Count == 0)
            return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
        RaiseChanged();
    }

    public void SetTool(AnnotationTool tool)
    {
        VerifyAccess();
        CancelGesture();
        _tool = tool;
        if (tool != AnnotationTool.Select)
            _selected = null;
        UpdateCursor();
        _surface.InvalidateVisual();
    }

    public void SetColor(WpfColor color)
    {
        VerifyAccess();
        _color = color;
    }

    public void DeleteSelection()
    {
        VerifyAccess();
        CancelGesture();
        if (_selected is null)
            return;
        RememberMutation();
        _annotations.Remove(_selected);
        _selected = null;
        FinishMutation();
    }

    public void FitToView()
    {
        VerifyAccess();
        if (_image is null || _viewport.ActualWidth <= 0 || _viewport.ActualHeight <= 0)
            return;
        _fitMode = true;
        _zoom = Math.Clamp(Math.Min((_viewport.ActualWidth - 80) / _image.PixelWidth, (_viewport.ActualHeight - 80) / _image.PixelHeight), 0.005, 1);
        _panX = (_viewport.ActualWidth - _image.PixelWidth * _zoom) / 2;
        _panY = (_viewport.ActualHeight - _image.PixelHeight * _zoom) / 2;
        RefreshView();
    }

    public void ActualSize()
    {
        VerifyAccess();
        if (!HasImage)
            return;
        CompleteGesture();
        ZoomAt(new WpfPoint(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2), 1);
    }

    public void ZoomIn() => ZoomFromCenter(1.2);
    public void ZoomOut() => ZoomFromCenter(1 / 1.2);

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasImage)
            return;
        Focus();
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown))
        {
            CompleteGesture();
            _panning = true;
            _panStart = e.GetPosition(_viewport);
            _panOrigin = new WpfPoint(_panX, _panY);
            _fitMode = false;
            _viewport.CaptureMouse();
            UpdateCursor();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
            return;

        WpfPoint point = e.GetPosition(_surface);
        if (!InsideImage(point))
        {
            _selected = null;
            _surface.InvalidateVisual();
            return;
        }
        point = ClampPoint(point);
        if (_tool == AnnotationTool.Select)
        {
            _selected = _annotations.LastOrDefault(item => item.HitTest(point, 6 / _zoom));
            if (_selected?.Tool == AnnotationTool.Text && e.ClickCount == 2)
            {
                EditText(_selected, point);
                e.Handled = true;
                return;
            }
            if (_selected is not null)
            {
                _beforeMove = Snapshot();
                _lastImagePoint = point;
                _didMove = false;
                _viewport.CaptureMouse();
            }
        }
        else if (_tool == AnnotationTool.Text)
        {
            EditText(null, point);
        }
        else
        {
            _pending = new AnnotationItem { Tool = _tool, Color = _color, Width = _strokeWidth, Start = point, End = point };
            if (_tool is AnnotationTool.Pen or AnnotationTool.Highlight)
                _pending.Points.Add(point);
            _viewport.CaptureMouse();
        }
        _surface.InvalidateVisual();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            WpfVector delta = e.GetPosition(_viewport) - _panStart;
            _panX = _panOrigin.X + delta.X;
            _panY = _panOrigin.Y + delta.Y;
            ClampPan();
            RefreshView();
            e.Handled = true;
            return;
        }
        if (!HasImage)
            return;

        WpfPoint point = ClampPoint(e.GetPosition(_surface));
        if (_pending is not null)
        {
            _pending.End = point;
            if (_pending.Tool is AnnotationTool.Pen or AnnotationTool.Highlight)
            {
                if ((_pending.Points[^1] - point).Length >= 0.6)
                    _pending.Points.Add(point);
            }
            _surface.InvalidateVisual();
            e.Handled = true;
        }
        else if (_beforeMove is not null && _selected is not null)
        {
            WpfVector delta = point - _lastImagePoint;
            WpfRect bounds = _selected.Bounds;
            double maxX = Math.Max(0, _image!.PixelWidth - bounds.Width);
            double maxY = Math.Max(0, _image.PixelHeight - bounds.Height);
            double targetX = Math.Clamp(bounds.X + delta.X, 0, maxX);
            double targetY = Math.Clamp(bounds.Y + delta.Y, 0, maxY);
            WpfVector move = new(targetX - bounds.X, targetY - bounds.Y);
            if (move.Length > 0.001)
            {
                _selected.Offset(move);
                _didMove = true;
            }
            _lastImagePoint = point;
            _surface.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle))
            return;
        if (_pending is not null)
        {
            WpfPoint point = ClampPoint(e.GetPosition(_surface));
            _pending.End = point;
            if ((_pending.Tool is AnnotationTool.Pen or AnnotationTool.Highlight) && (_pending.Points[^1] - point).Length >= 0.6)
                _pending.Points.Add(point);
        }
        if (_panning || _pending is not null || _beforeMove is not null)
        {
            CompleteGesture();
            e.Handled = true;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasImage)
            return;
        CompleteGesture();
        double factor = Math.Pow(1.12, e.Delta / 120.0);
        ZoomAt(e.GetPosition(_viewport), _zoom * factor);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && e.Key == Key.Z)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                Redo();
            else
                Undo();
            e.Handled = true;
        }
        else if (control && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            DeleteSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelGesture();
            _selected = null;
            _surface.InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && !control)
        {
            _spaceDown = true;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void EditText(AnnotationItem? annotation, WpfPoint position)
    {
        var dialog = new TextAnnotationDialog(annotation?.Text ?? string.Empty, annotation?.FontSize ?? Math.Max(24, _strokeWidth * 6));
        Window? owner = Window.GetWindow(this);
        if (owner is not null)
            dialog.Owner = owner;
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.AnnotationText))
            return;

        RememberMutation();
        if (annotation is not null)
        {
            annotation.Text = dialog.AnnotationText;
            annotation.FontSize = dialog.AnnotationFontSize;
            _selected = annotation;
        }
        else
        {
            var item = new AnnotationItem
            {
                Tool = AnnotationTool.Text,
                Start = position,
                End = position,
                Color = _color,
                Width = _strokeWidth,
                Text = dialog.AnnotationText,
                FontSize = dialog.AnnotationFontSize
            };
            _annotations.Add(item);
            _selected = item;
        }
        FinishMutation();
        Focus();
    }

    private void CompleteGesture()
    {
        AnnotationItem? pending = _pending;
        EditorState? beforeMove = _beforeMove;
        bool didMove = _didMove;
        _pending = null;
        _beforeMove = null;
        _didMove = false;
        _panning = false;

        if (pending is not null && IsUseful(pending))
        {
            if (pending.Tool == AnnotationTool.Mosaic)
                PrepareMosaic(pending);
            RememberMutation();
            _annotations.Add(pending);
            FinishMutation();
        }
        else if (beforeMove is not null && didMove)
        {
            if (_selected?.Tool == AnnotationTool.Mosaic)
                PrepareMosaic(_selected);
            _undo.Push(beforeMove);
            _redo.Clear();
            _revision = ++_nextRevision;
            RaiseChanged();
        }

        if (_viewport.IsMouseCaptured)
            _viewport.ReleaseMouseCapture();
        UpdateCursor();
        _surface.InvalidateVisual();
    }

    private void CancelGesture()
    {
        EditorState? beforeMove = _beforeMove;
        _pending = null;
        _beforeMove = null;
        _didMove = false;
        _panning = false;
        if (beforeMove is not null)
            Restore(beforeMove);
        if (_viewport.IsMouseCaptured)
            _viewport.ReleaseMouseCapture();
        UpdateCursor();
        _surface.InvalidateVisual();
    }

    private static bool IsUseful(AnnotationItem item)
    {
        if (item.Tool is AnnotationTool.Pen or AnnotationTool.Highlight)
            return item.Points.Count != 0;
        if (item.Tool == AnnotationTool.Arrow)
            return (item.End - item.Start).Length >= 2;
        return item.Bounds.Width >= 1 && item.Bounds.Height >= 1;
    }

    private void RememberMutation()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
    }

    private void FinishMutation()
    {
        _revision = ++_nextRevision;
        RaiseChanged();
        _surface.InvalidateVisual();
    }

    private EditorState Snapshot() => new(_revision, _annotations.Select(item => item.Clone()).ToList());

    private void Restore(EditorState state)
    {
        _annotations.Clear();
        _annotations.AddRange(state.Items.Select(item => item.Clone()));
        _revision = state.Revision;
        _selected = null;
        _surface.InvalidateVisual();
    }

    private void PrepareMosaic(AnnotationItem item)
    {
        if (_image is null)
            return;
        WpfRect bounds = item.Bounds;
        int left = Math.Clamp((int)Math.Floor(bounds.Left), 0, _image.PixelWidth - 1);
        int top = Math.Clamp((int)Math.Floor(bounds.Top), 0, _image.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, _image.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), top + 1, _image.PixelHeight);
        int width = right - left;
        int height = bottom - top;
        item.Start = new WpfPoint(left, top);
        item.End = new WpfPoint(right, bottom);

        if (_pixelSource is null)
        {
            var source = new FormatConvertedBitmap(_image, PixelFormats.Pbgra32, null, 0);
            source.Freeze();
            _pixelSource = source;
        }
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        _pixelSource.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);
        int blockSize = Math.Clamp((int)Math.Round(item.Width * 6), 16, 192);
        for (int blockY = 0; blockY < height; blockY += blockSize)
        {
            int endY = Math.Min(blockY + blockSize, height);
            for (int blockX = 0; blockX < width; blockX += blockSize)
            {
                int endX = Math.Min(blockX + blockSize, width);
                long blue = 0, green = 0, red = 0, alpha = 0;
                for (int y = blockY; y < endY; y++)
                    for (int x = blockX; x < endX; x++)
                    {
                        int offset = y * stride + x * 4;
                        blue += pixels[offset];
                        green += pixels[offset + 1];
                        red += pixels[offset + 2];
                        alpha += pixels[offset + 3];
                    }
                int count = (endX - blockX) * (endY - blockY);
                byte averageBlue = (byte)(blue / count);
                byte averageGreen = (byte)(green / count);
                byte averageRed = (byte)(red / count);
                byte averageAlpha = (byte)(alpha / count);
                for (int y = blockY; y < endY; y++)
                    for (int x = blockX; x < endX; x++)
                    {
                        int offset = y * stride + x * 4;
                        pixels[offset] = averageBlue;
                        pixels[offset + 1] = averageGreen;
                        pixels[offset + 2] = averageRed;
                        pixels[offset + 3] = averageAlpha;
                    }
            }
        }
        item.Mosaic = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        item.Mosaic.Freeze();
    }

    private void DrawScene(DrawingContext context, bool includeSelection)
    {
        if (_image is null)
            return;
        var imageBounds = new WpfRect(0, 0, _image.PixelWidth, _image.PixelHeight);
        context.PushClip(new RectangleGeometry(imageBounds));
        context.DrawImage(_image, imageBounds);
        foreach (AnnotationItem annotation in _annotations)
            annotation.Draw(context);
        if (includeSelection)
        {
            _pending?.Draw(context);
            if (_selected is not null)
            {
                WpfRect bounds = _selected.Bounds;
                double padding = Math.Max(5 / _zoom, _selected.Width * (_selected.Tool == AnnotationTool.Highlight ? 3 : 1));
                bounds.Inflate(padding, padding);
                var pen = new Pen(BrushFor("#63D5C5"), 1 / _zoom) { DashStyle = DashStyles.Dash };
                context.DrawRectangle(null, pen, bounds);
            }
        }
        context.Pop();
    }

    private void ZoomFromCenter(double factor)
    {
        VerifyAccess();
        if (HasImage)
        {
            CompleteGesture();
            ZoomAt(new WpfPoint(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2), _zoom * factor);
        }
    }

    private void ZoomAt(WpfPoint viewportPoint, double zoom)
    {
        if (!HasImage)
            return;
        double pixelX = (viewportPoint.X - _panX) / _zoom;
        double pixelY = (viewportPoint.Y - _panY) / _zoom;
        _zoom = Math.Clamp(zoom, 0.005, 8);
        _panX = viewportPoint.X - pixelX * _zoom;
        _panY = viewportPoint.Y - pixelY * _zoom;
        _fitMode = false;
        ClampPan();
        RefreshView();
    }

    private void ClampPan()
    {
        if (_image is null)
            return;
        double width = _image.PixelWidth * _zoom;
        double height = _image.PixelHeight * _zoom;
        double visibleX = Math.Min(60, width / 2);
        double visibleY = Math.Min(60, height / 2);
        _panX = Math.Clamp(_panX, visibleX - width, Math.Max(visibleX - width, _viewport.ActualWidth - visibleX));
        _panY = Math.Clamp(_panY, visibleY - height, Math.Max(visibleY - height, _viewport.ActualHeight - visibleY));
    }

    private bool InsideImage(WpfPoint point) => _image is not null && point.X >= 0 && point.Y >= 0 && point.X <= _image.PixelWidth && point.Y <= _image.PixelHeight;

    private WpfPoint ClampPoint(WpfPoint point) => _image is null ? point : new WpfPoint(Math.Clamp(point.X, 0, _image.PixelWidth), Math.Clamp(point.Y, 0, _image.PixelHeight));

    private void RefreshView()
    {
        _surface.RenderTransform = new MatrixTransform(_zoom, 0, 0, _zoom, _panX, _panY);
        _zoomLabel.Text = $"{_zoom:P0}";
        _surface.InvalidateVisual();
    }

    private void UpdateCursor() => _viewport.Cursor = _panning || _spaceDown ? Cursors.Hand : _tool == AnnotationTool.Select ? Cursors.Arrow : _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static SolidColorBrush BrushFor(string value)
    {
        var brush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static Border CreateBadge(TextBlock label, HorizontalAlignment alignment) => new()
    {
        Child = label,
        Background = BrushFor("#D9151D2B"),
        BorderBrush = BrushFor("#293448"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(10, 6, 10, 6),
        Margin = new Thickness(16),
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    private static FrameworkElement CreateEmptyState() => new Grid { IsHitTestVisible = false };

    private sealed record EditorState(long Revision, List<AnnotationItem> Items);

    private sealed class ImageSurface(EditorView editor) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) => editor.DrawScene(drawingContext, includeSelection: true);
    }
}

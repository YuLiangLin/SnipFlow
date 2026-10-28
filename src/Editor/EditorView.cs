using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using SnipFlow.Services;

namespace SnipFlow.Editor;

/// <summary>
/// A pixel-coordinate annotation canvas. Display zoom never changes exported dimensions.
/// </summary>
public sealed partial class EditorView : UserControl
{
    private const string CanvasHint = "滾輪縮放 · 空白鍵拖曳 · Delete 刪除標註";
    private const string TextHint = "Ctrl + Enter 完成 · Esc 取消 · 點選外部完成";
    private static readonly RenderOptions SceneRenderOptions = new() { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality };
    private readonly Canvas _viewport;
    private readonly Canvas _documentLayer;
    // The scene is split into retained layers so a pointer move only re-renders what changed:
    // image pixels, committed annotations, and the live gesture (pending shape, selection, marquee).
    private readonly ImageSurface _imageSurface;
    private readonly ImageSurface _surface;
    private readonly ImageSurface _overlay;
    private readonly TextBox _textEditor;
    private readonly Border _textEditBorder;
    private readonly TextBlock _hintLabel;
    private readonly Control _emptyState;
    private readonly Border _imageBadge;
    private readonly Border _zoomBadge;
    private readonly TextBlock _imageLabel;
    private readonly TextBlock _zoomLabel;
    private readonly List<AnnotationItem> _annotations = new();
    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();
    private Bitmap? _image;
    private PixelBuffer? _pixelSource;
    private AnnotationItem? _pending;
    private AnnotationItem? _selected;
    private EditorState? _beforeTransform;
    private AnnotationItem? _transformSource;
    private SelectionHandle _transformHandle;
    private Point _transformAnchor;
    private AnnotationItem? _textTarget;
    private AnnotationItem? _textDraft;
    private EditorState? _beforeText;
    private EditorState? _beforeProperty;
    private AnnotationItem? _propertySource;
    private AnnotationItem? _propertyTarget;
    private TopLevel? _hostWindow;
    private IPointer? _capturedPointer;
    private SolidColorBrush? _textSelectionBrush;
    private bool _syncTextUi;
    private bool _outsideTextClick;
    private bool _languageSubscribed;
    private bool _suppressEvents;
    private AnnotationTool _tool = AnnotationTool.Arrow;
    private Color _color = Color.FromRgb(99, 213, 197);
    private double _strokeWidth = 4;
    private double _textSize = 26;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private long _revision;
    private long _nextRevision;
    private long _savedRevision;
    private bool _fitMode = true;
    private bool _spaceDown;
    private bool _panning;
    private Point _panStart;
    private Point _panOrigin;
    private Point _gestureStart;
    private SelectionHandle _hoverHandle;

    public event EventHandler? Changed;
    public event EventHandler? SelectionChanged;

    public bool HasImage => _image is not null;
    public bool HasEdits => HasImage && (_revision != _savedRevision || _pending is not null || TransformHasChanges || TextHasChanges || PropertyHasChanges || CropHasChanges);
    public bool CanUndo => _undo.Count != 0 || TextHasChanges || PropertyHasChanges || CropHasChanges;
    public bool CanRedo => _redo.Count != 0 && !TextHasChanges && !PropertyHasChanges && !IsImageCropping;
    public double ZoomFactor => _zoom;
    public bool IsTextEditing => _textDraft is not null;
    public long Revision => _revision;
    public bool IsInteracting => _textDraft is not null || _pending is not null || _transformSource is not null || _propertySource is not null || _marqueeStart is not null || IsImageCropping;
    public AnnotationTool? SelectedTool => SelectedItem?.Tool;
    public Color? SelectedColor => SelectedItem?.Color;
    public double? SelectedStrokeWidth => SelectedItem?.Width;
    public double? SelectedTextSize => SelectedItem?.Tool == AnnotationTool.Text ? SelectedItem.FontSize : null;

    private AnnotationItem? SelectedItem => _textDraft ?? _selected;
    private bool TextHasChanges => _textDraft is not null && (_textTarget is null ? !string.IsNullOrWhiteSpace(_textDraft.Text) : !_textDraft.ContentEquals(_textTarget));
    private bool TransformHasChanges => _transformSources.Any(pair => !pair.Key.ContentEquals(pair.Value));
    private bool PropertyHasChanges => _propertySources.Any(pair => !pair.Key.ContentEquals(pair.Value));

    public double StrokeWidth
    {
        get => _strokeWidth;
        set
        {
            VerifyAccess();
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _strokeWidth = Math.Clamp(value, 1, 32);
            ChangeSelectedStyle(item =>
            {
                if (item.Tool is not (AnnotationTool.Text or AnnotationTool.Image))
                    item.Width = _strokeWidth;
            });
        }
    }

    public double TextSize
    {
        get => _textSize;
        set
        {
            VerifyAccess();
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _textSize = Math.Clamp(value, 12, 240);
            ChangeSelectedStyle(item =>
            {
                if (item.Tool == AnnotationTool.Text)
                    item.FontSize = _textSize;
            });
        }
    }

    public EditorView()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = ThemeService.Brush("Bg");
        _viewport = new Canvas
        {
            Background = ThemeService.Brush("Bg"),
            ClipToBounds = true
        };
        _documentLayer = new Canvas
        {
            IsVisible = false,
            UseLayoutRounding = false,
            // WPF render transforms start at the top-left corner; Avalonia's default origin is the center.
            RenderTransformOrigin = RelativePoint.TopLeft
        };
        _imageSurface = new ImageSurface(this, SceneLayers.Image);
        _surface = new ImageSurface(this, SceneLayers.Annotations);
        _overlay = new ImageSurface(this, SceneLayers.Interaction);
        foreach (ImageSurface layer in new[] { _imageSurface, _surface, _overlay })
        {
            RenderOptions.SetBitmapInterpolationMode(layer, BitmapInterpolationMode.HighQuality);
            _documentLayer.Children.Add(layer);
        }
        _textEditor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(_color),
            // Selected text keeps the annotation color; only the highlight behind it is tinted.
            SelectionForegroundBrush = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = default,
            FontFamily = AnnotationItem.TextTypeface.FontFamily,
            FontWeight = AnnotationItem.TextTypeface.Weight,
            FontStyle = AnnotationItem.TextTypeface.Style,
            FontStretch = AnnotationItem.TextTypeface.Stretch,
            TextAlignment = TextAlignment.Left,
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            UseLayoutRounding = false,
            Cursor = EditorCursor(StandardCursorType.Ibeam),
            UndoLimit = 100,
            // A bare presenter: no scroll viewer, padding or caret inset between the box edge and
            // the text origin, so the text wraps at exactly the annotation's width.
            Template = new FuncControlTemplate<TextBox>(CreateTextEditorPresenter)
        };
        _textEditor.CaretBrush = _textEditor.Foreground;
        UpdateTextSelectionBrush();
        _textEditBorder = new Border
        {
            Child = _textEditor,
            BorderBrush = ThemeService.Brush("SelectionBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            UseLayoutRounding = false,
            RenderTransformOrigin = RelativePoint.TopLeft,
            IsVisible = false
        };
        _documentLayer.Children.Add(_textEditBorder);
        _viewport.Children.Add(_documentLayer);
        _textEditor.TextChanged += (_, _) => TextEditorChanged();
        _textEditor.LostFocus += (_, e) =>
        {
            if (IsTextEditing && !IsWithin(_textEditor, e.NewFocusedElement) && _textEditor.ContextFlyout?.IsOpen != true)
                CommitTextEdit();
        };

        var layout = new Grid { ClipToBounds = true };
        layout.Children.Add(_viewport);
        _emptyState = CreateEmptyState();
        layout.Children.Add(_emptyState);

        _imageLabel = new TextBlock { FontSize = 11, Foreground = ThemeService.Brush("Muted") };
        _imageBadge = CreateBadge(_imageLabel, HorizontalAlignment.Left);
        layout.Children.Add(_imageBadge);
        _zoomLabel = new TextBlock { FontSize = 11, Foreground = ThemeService.Brush("Ink") };
        _zoomBadge = CreateBadge(_zoomLabel, HorizontalAlignment.Right);
        layout.Children.Add(_zoomBadge);

        _hintLabel = new TextBlock
        {
            Text = I18n.T(CanvasHint),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = ThemeService.Brush("Muted"),
            Margin = new Thickness(20, 0, 20, 15),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        layout.Children.Add(_hintLabel);
        _cropToolbar = CreateCropToolbar();
        layout.Children.Add(_cropToolbar);
        Content = layout;

        _viewport.AddHandler(PointerPressedEvent, OnMouseDown, RoutingStrategies.Tunnel);
        _viewport.AddHandler(PointerMovedEvent, OnMouseMove, RoutingStrategies.Tunnel);
        _viewport.AddHandler(PointerReleasedEvent, OnMouseUp, RoutingStrategies.Tunnel);
        _viewport.AddHandler(PointerWheelChangedEvent, OnMouseWheel, RoutingStrategies.Tunnel);
        _viewport.PointerCaptureLost += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _viewport) && ReferenceEquals(e.Pointer, _capturedPointer))
            {
                _capturedPointer = null;
                EndImageCropGesture();
                CompleteGesture();
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        LostFocus += (_, _) =>
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
            AttachHostWindow();
            if (!_languageSubscribed)
            {
                I18n.Changed += OnLanguageChanged;
                ThemeService.Changed += OnThemeChanged;
                _languageSubscribed = true;
            }
            RefreshLanguage();
            if (HasImage && _fitMode)
                FitToView();
        };
        Unloaded += (_, _) =>
        {
            DetachHostWindow();
            if (_languageSubscribed)
            {
                I18n.Changed -= OnLanguageChanged;
                ThemeService.Changed -= OnThemeChanged;
                _languageSubscribed = false;
            }
        };
        UpdateCursor();
    }

    public void LoadImage(Bitmap image)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelSize.Width < 1 || image.PixelSize.Height < 1)
            throw new ArgumentException(I18n.T("影像尺寸不得為零。"), nameof(image));

        CancelImageCrop();
        CancelTextEdit();
        CancelGesture();
        EndPropertyEdit();
        // One document unit is one source pixel; a 150% desktop capture must not shrink.
        _image = AtDocumentDpi(image);
        _pixelSource = null;
        _annotations.Clear();
        _selection.Clear();
        IsCollage = false;
        _undo.Clear();
        _redo.Clear();
        _selected = null;
        _revision = _nextRevision = _savedRevision = 0;
        SizeDocument(_image);
        _documentLayer.IsVisible = true;
        _emptyState.IsVisible = false;
        _imageBadge.IsVisible = _zoomBadge.IsVisible = true;
        _fitMode = true;
        FitToView();
        RefreshLanguage();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    internal EditorSessionSnapshot CaptureSession()
    {
        VerifyAccess();
        if (_image is null)
            throw new InvalidOperationException(I18n.T("請先開啟或擷取影像。"));
        CommitImageCrop();
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
        return new(_image, _annotations.Select(item => item.Clone()).ToList(), HasEdits, IsCollage);
    }

    internal void RestoreSession(Bitmap image, IReadOnlyList<AnnotationItem> annotations, bool hasEdits, bool isCollage = false)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(annotations);
        var restored = annotations.Select(item => item.Clone()).ToList();
        var suppressed = _suppressEvents;
        _suppressEvents = true;
        try
        {
            LoadImage(isCollage ? AsBoard(image) : image);
            IsCollage = isCollage;
            RefreshLanguage();
            _annotations.AddRange(restored);
            _revision = _nextRevision = restored.Count > 0 || hasEdits ? 1 : 0;
            _savedRevision = hasEdits ? -1 : _revision;
            EnsureRenderCache();
            InvalidateScene();
        }
        finally
        {
            _suppressEvents = suppressed;
        }
        // Subscribers see the complete document, never the raw-image intermediate state.
        RaiseSelectionChanged();
        RaiseChanged();
    }

    /// <summary>Returns only image pixels and committed annotations, at the original pixel size.</summary>
    public Bitmap ExportImage()
    {
        VerifyAccess();
        if (_image is null)
            throw new InvalidOperationException(I18n.T("請先開啟或擷取影像。"));

        CommitImageCrop();
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
        if (_annotations.Count == 0 && !IsBoard(_image))
            return _image;

        // At 96 DPI, one drawing unit is one output pixel. Source DPI is deliberately
        // not used for layout; a 150% desktop capture must not shrink on export.
        return ImageTools.Render(new PixelSize(PixelWidth, PixelHeight), context => DrawScene(context, SceneLayers.Document, onScreen: false));
    }

    public void MarkSaved()
    {
        VerifyAccess();
        CommitImageCrop();
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
        _savedRevision = _revision;
        RaiseChanged();
    }

    public void MarkUnsaved()
    {
        VerifyAccess();
        if (_savedRevision == -1) return;
        _savedRevision = -1;
        RaiseChanged();
    }

    public void Undo()
    {
        VerifyAccess();
        if (IsImageCropping) { CancelImageCrop(); return; }
        CommitTextEdit();
        EndPropertyEdit();
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
        if (IsImageCropping) { CancelImageCrop(); return; }
        CommitTextEdit();
        EndPropertyEdit();
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
        CommitImageCrop();
        CommitTextEdit();
        EndPropertyEdit();
        CancelGesture();
        _tool = tool;
        if (tool != AnnotationTool.Select)
            SelectItem(null);
        UpdateCursor();
        InvalidateScene();
    }

    public void SetColor(Color color)
    {
        VerifyAccess();
        _color = color;
        ChangeSelectedStyle(item =>
        {
            if (item.Tool is not (AnnotationTool.Mosaic or AnnotationTool.Image))
                item.Color = color;
        });
    }

    public void DeleteSelection()
    {
        VerifyAccess();
        CommitImageCrop();
        CommitTextEdit();
        EndPropertyEdit();
        CancelGesture();
        if (_selection.Count == 0)
            return;
        RememberMutation();
        _annotations.RemoveAll(_selection.Contains);
        SelectItem(null);
        RefreshMosaics();
        FinishMutation();
    }

    public void FitToView()
    {
        VerifyAccess();
        if (_image is null || _viewport.Bounds.Width <= 0 || _viewport.Bounds.Height <= 0)
            return;
        _fitMode = true;
        _zoom = Math.Clamp(Math.Min((_viewport.Bounds.Width - 80) / PixelWidth, (_viewport.Bounds.Height - 80) / PixelHeight), 0.005, 1);
        _panX = (_viewport.Bounds.Width - PixelWidth * _zoom) / 2;
        _panY = (_viewport.Bounds.Height - PixelHeight * _zoom) / 2;
        RefreshView();
    }

    public void ActualSize()
    {
        VerifyAccess();
        if (!HasImage)
            return;
        CompleteGesture();
        ZoomAt(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2), 1);
    }

    public void ZoomIn() => ZoomFromCenter(1.2);
    public void ZoomOut() => ZoomFromCenter(1 / 1.2);

    private void OnMouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (!HasImage)
            return;
        if (IsTextEditing && IsWithin(_textEditor, e.Source))
            return;
        MouseButton button = ChangedButton(e);
        if (button == MouseButton.Right)
        {
            ShowObjectMenu(ViewportToImage(e.GetPosition(_viewport)));
            e.Handled = true;
            return;
        }
        if (button == MouseButton.Middle || (button == MouseButton.Left && _spaceDown))
        {
            CompleteGesture();
            _panning = true;
            _panStart = e.GetPosition(_viewport);
            _panOrigin = new Point(_panX, _panY);
            _fitMode = false;
            CaptureViewport(e.Pointer);
            UpdateCursor();
            e.Handled = true;
            return;
        }
        if (button != MouseButton.Left)
            return;

        bool finishedText = _outsideTextClick;
        _outsideTextClick = false;
        if (IsTextEditing)
        {
            CommitTextEdit();
            finishedText = true;
        }
        EndPropertyEdit();
        Focus();
        Point point = ViewportToImage(e.GetPosition(_viewport));
        if (IsImageCropping)
        {
            var cropHandle = HitImageCrop(point);
            if (cropHandle != SelectionHandle.None)
            {
                BeginImageCropGesture(cropHandle, point);
                CaptureViewport(e.Pointer);
                UpdateCursor();
                e.Handled = true;
                return;
            }
            CommitImageCrop();
        }
        if (TryBeginImageCropAt(point, e.ClickCount, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        SelectionHandle handle = HitSelectionHandle(point);
        if (_selected is not null && handle != SelectionHandle.None)
        {
            BeginTransform(handle, point);
            CaptureViewport(e.Pointer);
            e.Handled = true;
            return;
        }
        if (!InsideImage(point))
        {
            SelectItem(null);
            return;
        }
        point = ClampPoint(point);
        if (_tool == AnnotationTool.Select || finishedText)
        {
            AnnotationItem? item = _annotations.LastOrDefault(candidate => candidate.HitTest(point, 6 / _zoom));
            if (item is null && _selected is { Tool: AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Text or AnnotationTool.Mosaic } && _selected.Bounds.Contains(point))
                item = _selected;
            bool extend = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
            if (item is null)
            {
                _marqueeOriginal = extend ? new HashSet<AnnotationItem>(_selection) : new();
                if (!extend) SelectItem(null);
                _marqueeStart = point;
                _marquee = new Rect(point, point);
                CaptureViewport(e.Pointer);
            }
            else if (extend)
            {
                var selected = new HashSet<AnnotationItem>(_selection);
                if (!selected.Add(item)) selected.Remove(item);
                SetSelection(_annotations.Where(selected.Contains));
            }
            else if (!_selection.Contains(item))
                SelectItem(item);
            if (item?.Tool == AnnotationTool.Text && e.ClickCount == 2 && !extend)
            {
                SelectItem(item);
                BeginTextEdit(item, null, point);
                e.Handled = true;
                return;
            }
            if (item is not null && _selection.Contains(item) && !extend)
            {
                BeginTransform(SelectionHandle.Move, point);
                CaptureViewport(e.Pointer);
            }
        }
        else if (_tool == AnnotationTool.Text)
        {
            AnnotationItem? text = _annotations.LastOrDefault(item => item.Tool == AnnotationTool.Text && item.HitTest(point, 3 / _zoom));
            if (text is not null)
                BeginTextEdit(text, null, point);
            else
            {
                SelectItem(null);
                _gestureStart = point;
                _pending = new AnnotationItem { Tool = AnnotationTool.Text, Color = _color, Width = _strokeWidth, FontSize = _textSize, Start = point, End = point };
                CaptureViewport(e.Pointer);
            }
        }
        else
        {
            SelectItem(null);
            _gestureStart = point;
            _pending = new AnnotationItem { Tool = _tool, Color = _color, Width = _strokeWidth, Start = point, End = point };
            if (_tool is AnnotationTool.Pen or AnnotationTool.Highlight)
                _pending.Points.Add(point);
            CaptureViewport(e.Pointer);
        }
        InvalidateScene();
        e.Handled = true;
    }

    private void OnMouseMove(object? sender, PointerEventArgs e)
    {
        if (_panning)
        {
            Vector delta = e.GetPosition(_viewport) - _panStart;
            _panX = _panOrigin.X + delta.X;
            _panY = _panOrigin.Y + delta.Y;
            ClampPan();
            RefreshView();
            e.Handled = true;
            return;
        }
        if (!HasImage)
            return;

        Point point = ClampPoint(ViewportToImage(e.GetPosition(_viewport)));
        if (IsImageCropping)
        {
            if (_cropDragSource is not null) UpdateImageCropGesture(point);
            else { _hoverHandle = HitImageCrop(point); UpdateCursor(); }
            e.Handled = true;
            return;
        }
        if (_marqueeStart is not null)
        {
            _marquee = AnnotationItem.Span(_marqueeStart.Value, point);
            _overlay.InvalidateVisual();
            e.Handled = true;
        }
        else if (_pending is not null)
        {
            UpdatePending(point);
            _overlay.InvalidateVisual();
            e.Handled = true;
        }
        else if (_beforeTransform is not null && _selected is not null)
        {
            UpdateTransform(point);
            e.Handled = true;
        }
        else
        {
            _hoverHandle = HitSelectionHandle(point);
            UpdateCursor();
        }
    }

    private void OnMouseUp(object? sender, PointerReleasedEventArgs e)
    {
        if (ChangedButton(e) is not (MouseButton.Left or MouseButton.Middle))
            return;
        if (_cropDragSource is not null)
        {
            UpdateImageCropGesture(ClampPoint(ViewportToImage(e.GetPosition(_viewport))));
            EndImageCropGesture();
            UpdateCursor();
            e.Handled = true;
            return;
        }
        if (_pending is not null)
        {
            Point point = ClampPoint(ViewportToImage(e.GetPosition(_viewport)));
            UpdatePending(point);
        }
        else if (_beforeTransform is not null)
            UpdateTransform(ClampPoint(ViewportToImage(e.GetPosition(_viewport))));
        if (_panning || _pending is not null || _beforeTransform is not null || _marqueeStart is not null)
        {
            CompleteGesture();
            e.Handled = true;
        }
    }

    private void OnMouseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!HasImage)
            return;
        CompleteGesture();
        // One wheel notch is Delta.Y = 1 (WPF reported 120).
        double factor = Math.Pow(1.12, e.Delta.Y);
        ZoomAt(e.GetPosition(_viewport), _zoom * factor);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        KeyModifiers modifiers = e.KeyModifiers;
        if (HandleImageCropKey(e)) return;
        if (IsImageCropping)
        {
            if (modifiers == KeyModifiers.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
            else if ((modifiers == KeyModifiers.Control && e.Key == Key.Y)
                || (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Z)) { Redo(); e.Handled = true; }
            else if (modifiers == KeyModifiers.None && e.Key == Key.Space)
            { _spaceDown = true; UpdateCursor(); e.Handled = true; }
            return;
        }
        if (IsTextEditing)
        {
            if (modifiers == KeyModifiers.Control && e.Key == Key.Enter)
            {
                CommitTextEdit();
                e.Handled = true;
            }
            else if (modifiers == KeyModifiers.None && e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
            // Native TextBox caret, selection, IME, Delete, and local undo stay intact.
            return;
        }
        if (modifiers == KeyModifiers.Control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if ((modifiers == KeyModifiers.Control && e.Key == Key.Y)
            || (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Z))
        {
            Redo();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.Control && e.Key == Key.D)
        {
            try { DuplicateSelection(); }
            catch (Exception error) { AppDialog.Show(error.Message, I18n.T("無法複製物件"), MessageBoxButton.OK, MessageBoxImage.Warning); }
            e.Handled = true;
        }
        else if ((modifiers is KeyModifiers.None or KeyModifiers.Shift) && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = modifiers == KeyModifiers.Shift ? 10 : 1;
            NudgeSelection(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.None && (e.Key is Key.Delete or Key.Back))
        {
            DeleteSelection();
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.None && e.Key == Key.Escape)
        {
            CancelGesture();
            SelectItem(null);
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.None && (e.Key is Key.Enter or Key.F2) && _selected?.Tool == AnnotationTool.Text)
        {
            BeginTextEdit(_selected, null, null);
            e.Handled = true;
        }
        else if (modifiers == KeyModifiers.None && e.Key == Key.Space)
        {
            _spaceDown = true;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (IsTextEditing)
            return;
        if (_spaceDown && e.Key == Key.Space)
        {
            _spaceDown = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void BeginTextEdit(AnnotationItem? annotation, Rect? placement, Point? caretPosition)
    {
        CommitTextEdit();
        EndPropertyEdit();
        if (_image is null)
            return;
        _beforeText = Snapshot();
        _textTarget = annotation;
        if (annotation is not null)
            _textDraft = annotation.Clone();
        else
        {
            Point start = ClampPoint(placement?.TopLeft ?? caretPosition ?? new Point(0, 0));
            double availableWidth = Math.Max(1, PixelWidth - start.X);
            _textDraft = new AnnotationItem
            {
                Tool = AnnotationTool.Text,
                Start = start,
                End = start,
                Color = _color,
                Width = _strokeWidth,
                FontSize = _textSize,
                TextWidth = Math.Min(placement?.Width > 1 ? placement.Value.Width : 360, availableWidth),
                TextBoxHeight = placement?.Height ?? 0
            };
        }
        SelectItem(annotation);
        UpdateTextEditor();
        _syncTextUi = true;
        _textEditor.IsUndoEnabled = false;
        _textEditor.Text = _textDraft.Text;
        _textEditor.IsUndoEnabled = true;
        _syncTextUi = false;
        _textEditBorder.IsVisible = true;
        _spaceDown = false;
        _hintLabel.Text = I18n.T(TextHint);
        UpdateTextEditor();
        AttachHostWindow();
        _textEditor.Focus();
        _textEditor.CaretIndex = _textEditor.Text?.Length ?? 0;
        if (annotation is not null && caretPosition is Point position)
        {
            // Hit-test the same layout the text box shows, in the text's own (unrotated) frame.
            Matrix toText = _textDraft.TextToDocumentMatrix.Invert();
            Point local = toText.Transform(position);
            TextHitTestResult hit = _textDraft.FormatText().HitTestPoint(local);
            int index = hit.TextPosition;
            if (index >= 0)
                _textEditor.CaretIndex = Math.Min(index, _textEditor.Text?.Length ?? 0);
        }
        InvalidateScene();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    public void CommitTextEdit()
    {
        VerifyAccess();
        if (_textDraft is null)
            return;
        AnnotationItem draft = _textDraft;
        AnnotationItem? target = _textTarget;
        EditorState before = _beforeText!;
        draft.Text = _textEditor.Text ?? string.Empty;
        bool empty = string.IsNullOrWhiteSpace(draft.Text);
        bool changed = target is null ? !empty : empty || !draft.ContentEquals(target);
        EndTextUi();
        if (changed)
        {
            _undo.Push(before);
            _redo.Clear();
            if (target is not null)
            {
                if (empty)
                    _annotations.Remove(target);
                else
                    target.CopyFrom(draft);
                SelectItem(empty ? null : target);
            }
            else
            {
                _annotations.Add(draft);
                SelectItem(draft);
            }
            RefreshMosaics();
            FinishMutation();
        }
        else
            SelectItem(target);
        InvalidateScene();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    public void CancelTextEdit()
    {
        VerifyAccess();
        if (_textDraft is null)
            return;
        AnnotationItem? target = _textTarget;
        EndTextUi();
        SelectItem(target);
        InvalidateScene();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    private void EndTextUi()
    {
        bool hadFocus = _textEditor.IsKeyboardFocusWithin;
        _textDraft = null;
        _textTarget = null;
        _beforeText = null;
        _textEditBorder.IsVisible = false;
        RefreshLanguage();
        if (hadFocus)
            Focus();
        UpdateCursor();
    }

    private void TextEditorChanged()
    {
        if (_syncTextUi || _textDraft is null)
            return;
        _textDraft.Text = _textEditor.Text ?? string.Empty;
        UpdateTextEditor();
        RaiseChanged();
    }

    private void UpdateTextEditor()
    {
        if (_textDraft is null)
            return;
        Size size = _textDraft.TextLayoutSize;
        double border = 1 / _zoom;
        _textEditBorder.BorderThickness = new Thickness(border);
        _textEditor.FontSize = _textDraft.FontSize;
        if ((_textEditor.Foreground as ISolidColorBrush)?.Color != _textDraft.Color)
        {
            var foreground = new SolidColorBrush(_textDraft.Color);
            _textEditor.Foreground = foreground;
            _textEditor.CaretBrush = foreground;
        }
        _textEditor.Background = Brushes.Transparent;
        // The editor's presenter has no content margin, so the text box is exactly the
        // annotation's text width and its text origin is the annotation's origin.
        _textEditor.Width = Math.Max(1, size.Width);
        // The input viewport grows with all lines. Its extra caret room is UI only.
        _textEditor.Height = Math.Max(size.Height, _textDraft.FontSize * 1.4) + _textDraft.FontSize * 0.2;
        // Apply the same glyph transform as DrawText, keeping the border outside its origin.
        var matrix = _textDraft.TextToDocumentMatrix;
        var origin = matrix.Transform(new Point(-border, -border));
        Canvas.SetLeft(_textEditBorder, 0);
        Canvas.SetTop(_textEditBorder, 0);
        _textEditBorder.RenderTransform = new MatrixTransform(new Matrix(matrix.M11, matrix.M12, matrix.M21, matrix.M22, origin.X, origin.Y));
    }

    private static Control CreateTextEditorPresenter(TextBox box, INameScope scope)
    {
        var presenter = new TextPresenter
        {
            Name = "PART_TextPresenter",
            Background = Brushes.Transparent,
            UseLayoutRounding = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        presenter.Bind(TextPresenter.TextProperty, new TemplateBinding(TextBox.TextProperty) { Mode = BindingMode.TwoWay });
        presenter.Bind(TextPresenter.CaretBlinkIntervalProperty, new TemplateBinding(TextBox.CaretBlinkIntervalProperty));
        presenter.Bind(TextPresenter.CaretIndexProperty, new TemplateBinding(TextBox.CaretIndexProperty));
        presenter.Bind(TextPresenter.SelectionStartProperty, new TemplateBinding(TextBox.SelectionStartProperty));
        presenter.Bind(TextPresenter.SelectionEndProperty, new TemplateBinding(TextBox.SelectionEndProperty));
        presenter.Bind(TextPresenter.TextAlignmentProperty, new TemplateBinding(TextBox.TextAlignmentProperty));
        presenter.Bind(TextPresenter.TextWrappingProperty, new TemplateBinding(TextBox.TextWrappingProperty));
        presenter.Bind(TextPresenter.LineHeightProperty, new TemplateBinding(TextBox.LineHeightProperty));
        presenter.Bind(TextPresenter.PasswordCharProperty, new TemplateBinding(TextBox.PasswordCharProperty));
        presenter.Bind(TextPresenter.RevealPasswordProperty, new TemplateBinding(TextBox.RevealPasswordProperty));
        presenter.Bind(TextPresenter.SelectionBrushProperty, new TemplateBinding(TextBox.SelectionBrushProperty));
        presenter.Bind(TextPresenter.SelectionForegroundBrushProperty, new TemplateBinding(TextBox.SelectionForegroundBrushProperty));
        presenter.Bind(TextPresenter.CaretBrushProperty, new TemplateBinding(TextBox.CaretBrushProperty));
        return presenter.RegisterInNameScope(scope);
    }

    private void UpdateTextSelectionBrush()
    {
        // WPF drew the theme's selection border color at 35% opacity behind the selected glyphs.
        _textSelectionBrush ??= new SolidColorBrush(default, 0.35);
        _textSelectionBrush.Color = ThemeService.GetColor("SelectionBorder");
        _textEditor.SelectionBrush = _textSelectionBrush;
    }

    private void AttachHostWindow()
    {
        TopLevel? window = TopLevel.GetTopLevel(this);
        if (ReferenceEquals(window, _hostWindow))
            return;
        DetachHostWindow();
        _hostWindow = window;
        window?.AddHandler(PointerPressedEvent, OnHostMouseDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void DetachHostWindow()
    {
        _hostWindow?.RemoveHandler(PointerPressedEvent, OnHostMouseDown);
        _hostWindow = null;
    }

    private void OnHostMouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && IsImageCropping && !IsWithin(this, e.Source))
            CommitImageCrop();
        if (e.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && IsTextEditing && !IsWithin(_textEditor, e.Source))
        {
            _outsideTextClick = IsWithin(_viewport, e.Source);
            CommitTextEdit();
        }
    }

    private static bool IsWithin(StyledElement parent, object? child)
    {
        var current = child as StyledElement;
        // The visual root's logical owner can lead back into the tree, so stop at any repeat.
        var visited = new HashSet<StyledElement>(ReferenceEqualityComparer.Instance);
        while (current is not null && visited.Add(current))
        {
            if (ReferenceEquals(parent, current))
                return true;
            // Popups (the text box's Cut/Copy/Paste menu) continue through their owner and target.
            current = current is TopLevel ? current.Parent
                : (current as Visual)?.GetVisualParent() as StyledElement
                ?? (current as Avalonia.Controls.Primitives.Popup)?.PlacementTarget
                ?? current.Parent;
        }
        return false;
    }

    private void CaptureViewport(IPointer pointer)
    {
        _capturedPointer = pointer;
        pointer.Capture(_viewport);
    }

    private void ReleaseViewportCapture()
    {
        IPointer? pointer = _capturedPointer;
        _capturedPointer = null;
        if (pointer is not null && ReferenceEquals(pointer.Captured, _viewport))
            pointer.Capture(null);
    }

    private MouseButton ChangedButton(PointerEventArgs e) => e.GetCurrentPoint(_viewport).Properties.PointerUpdateKind.GetMouseButton();

    private void CompleteGesture()
    {
        EndImageCropGesture();
        Rect? marquee = _marquee;
        HashSet<AnnotationItem>? marqueeOriginal = _marqueeOriginal;
        _marquee = null;
        _marqueeStart = null;
        _marqueeOriginal = null;
        AnnotationItem? pending = _pending;
        EditorState? before = _beforeTransform;
        bool transformed = TransformHasChanges;
        _pending = null;
        _beforeTransform = null;
        _transformSource = null;
        _transformSources.Clear();
        _transformHandle = SelectionHandle.None;
        _panning = false;
        ReleaseViewportCapture();

        if (marquee is not null)
        {
            var selected = marqueeOriginal ?? new HashSet<AnnotationItem>();
            if (marquee.Value.Width * _zoom >= 3 || marquee.Value.Height * _zoom >= 3)
                selected.UnionWith(_annotations.Where(item => marquee.Value.Contains(item.Bounds)));
            SetSelection(_annotations.Where(selected.Contains));
        }
        else if (pending?.Tool == AnnotationTool.Text)
        {
            Rect? placement = pending.TextWidth > 0 ? pending.Bounds : null;
            BeginTextEdit(null, placement, pending.Start);
        }
        else if (pending is not null && IsUseful(pending))
        {
            if (pending.Tool == AnnotationTool.Mosaic)
                PrepareMosaic(pending);
            RememberMutation();
            _annotations.Add(pending);
            SelectItem(pending);
            FinishMutation();
        }
        else if (before is not null && transformed)
        {
            RefreshMosaics();
            _undo.Push(before);
            _redo.Clear();
            FinishMutation();
            RaiseSelectionChanged();
        }

        UpdateCursor();
        InvalidateScene();
    }

    private void CancelGesture()
    {
        EditorState? before = _beforeTransform;
        _pending = null;
        _beforeTransform = null;
        _transformSource = null;
        _transformSources.Clear();
        _marquee = null;
        _marqueeStart = null;
        if (_marqueeOriginal is not null) SetSelection(_annotations.Where(_marqueeOriginal.Contains));
        _marqueeOriginal = null;
        _transformHandle = SelectionHandle.None;
        _panning = false;
        if (before is not null)
            Restore(before);
        ReleaseViewportCapture();
        UpdateCursor();
        InvalidateScene();
    }

    private void UpdatePending(Point point)
    {
        if (_pending is null)
            return;
        if (_pending.Tool == AnnotationTool.Text)
        {
            if (Point.Distance(point, _gestureStart) * _zoom >= 4)
            {
                var bounds = AnnotationItem.Span(_gestureStart, point);
                _pending.Start = bounds.TopLeft;
                _pending.End = bounds.TopLeft;
                _pending.TextWidth = Math.Min(Math.Max(24, bounds.Width), Math.Max(1, PixelWidth - bounds.Left));
                _pending.TextBoxHeight = bounds.Height;
            }
            else
            {
                _pending.Start = _gestureStart;
                _pending.End = _gestureStart;
                _pending.TextWidth = 0;
                _pending.TextBoxHeight = 0;
            }
            return;
        }
        _pending.End = point;
        if ((_pending.Tool is AnnotationTool.Pen or AnnotationTool.Highlight) && Point.Distance(_pending.Points[^1], point) >= 0.6)
            _pending.Points.Add(point);
    }

    private void BeginTransform(SelectionHandle handle, Point point)
    {
        if (_selected is null)
            return;
        EndPropertyEdit();
        _beforeTransform = Snapshot();
        _transformSource = _selected.Clone();
        _transformSources.Clear();
        foreach (AnnotationItem item in _selection) _transformSources.Add(item, item.Clone());
        _transformHandle = handle;
        _transformAnchor = point;
        _hoverHandle = handle;
        UpdateCursor();
    }

    private void UpdateTransform(Point point)
    {
        if (_selected is null || _transformSource is null || _image is null)
            return;
        AnnotationItem source = _transformSource;
        foreach (var pair in _transformSources) pair.Key.CopyFrom(pair.Value);
        Vector delta = point - _transformAnchor;
        if (delta.SquaredLength < 0.0000000001)
        {
            InvalidateScene();
            return;
        }
        if (_transformHandle == SelectionHandle.Move)
        {
            delta = ClampOffset(BoundsOf(_transformSources.Values), delta);
            foreach (var pair in _transformSources)
            {
                pair.Key.CopyFrom(pair.Value);
                pair.Key.Offset(delta);
            }
        }
        else if (_transformHandle is SelectionHandle.StartPoint or SelectionHandle.EndPoint)
        {
            if (_transformHandle == SelectionHandle.StartPoint)
                _selected.Start = ClampPoint(source.Start + delta);
            else
                _selected.End = ClampPoint(source.End + delta);
        }
        else
        {
            Rect original = source.Tool == AnnotationTool.Image ? source.Bounds : EditableBounds(source);
            Rect resized = _selected.Tool == AnnotationTool.Image
                ? ResizeImage(original, _transformHandle, delta)
                : ResizeBox(original, _transformHandle, delta, _selected.Tool == AnnotationTool.Text ? 12 : 2);
            ApplyResize(_selected, source, original, resized);
        }
        InvalidateScene();
    }

    private Rect ResizeBox(Rect original, SelectionHandle handle, Vector delta, double minimum)
    {
        double left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;
        static double ClampRange(double value, double min, double max) => Math.Clamp(value, min, Math.Max(min, max));
        if (handle is SelectionHandle.TopLeft or SelectionHandle.Left or SelectionHandle.BottomLeft)
            left = ClampRange(left + delta.X, 0, right - minimum);
        if (handle is SelectionHandle.TopRight or SelectionHandle.Right or SelectionHandle.BottomRight)
            right = ClampRange(right + delta.X, left + minimum, PixelWidth);
        if (handle is SelectionHandle.TopLeft or SelectionHandle.Top or SelectionHandle.TopRight)
            top = ClampRange(top + delta.Y, 0, bottom - minimum);
        if (handle is SelectionHandle.BottomLeft or SelectionHandle.Bottom or SelectionHandle.BottomRight)
            bottom = ClampRange(bottom + delta.Y, top + minimum, PixelHeight);
        return AnnotationItem.Span(new Point(left, top), new Point(right, bottom));
    }

    private static void ApplyResize(AnnotationItem item, AnnotationItem source, Rect original, Rect resized)
    {
        if (item.Tool == AnnotationTool.Text)
        {
            item.Start = resized.TopLeft;
            item.End = resized.TopLeft;
            var swapsAxes = ImageOrientation.SwapsAxes(item.TextOrientation);
            item.TextWidth = swapsAxes ? resized.Height : resized.Width;
            item.TextBoxHeight = swapsAxes ? resized.Width : resized.Height;
        }
        else if (item.Tool is AnnotationTool.Pen or AnnotationTool.Highlight)
        {
            Point Map(Point point) => new(resized.X + (point.X - original.X) * resized.Width / original.Width, resized.Y + (point.Y - original.Y) * resized.Height / original.Height);
            item.Start = Map(source.Start);
            item.End = Map(source.End);
            for (int i = 0; i < item.Points.Count; i++)
                item.Points[i] = Map(source.Points[i]);
        }
        else
        {
            item.Start = resized.TopLeft;
            item.End = resized.BottomRight;
        }
    }

    private static Rect EditableBounds(AnnotationItem item)
    {
        Rect bounds = item.Bounds;
        if (bounds.Width < 2)
            bounds = new Rect(bounds.X - (2 - bounds.Width) / 2, bounds.Y, 2, bounds.Height);
        if (bounds.Height < 2)
            bounds = new Rect(bounds.X, bounds.Y - (2 - bounds.Height) / 2, bounds.Width, 2);
        return bounds;
    }

    private static IEnumerable<(SelectionHandle Handle, Point Position)> HandlePoints(AnnotationItem item)
    {
        if (item.Tool == AnnotationTool.Arrow)
        {
            yield return (SelectionHandle.StartPoint, item.Start);
            yield return (SelectionHandle.EndPoint, item.End);
            yield break;
        }
        if (item.Tool == AnnotationTool.Image)
        {
            Rect imageBounds = item.Bounds;
            yield return (SelectionHandle.TopLeft, imageBounds.TopLeft);
            yield return (SelectionHandle.TopRight, imageBounds.TopRight);
            yield return (SelectionHandle.BottomRight, imageBounds.BottomRight);
            yield return (SelectionHandle.BottomLeft, imageBounds.BottomLeft);
            yield break;
        }
        Rect bounds = EditableBounds(item);
        double middleX = bounds.X + bounds.Width / 2;
        double middleY = bounds.Y + bounds.Height / 2;
        yield return (SelectionHandle.TopLeft, bounds.TopLeft);
        yield return (SelectionHandle.Top, new Point(middleX, bounds.Top));
        yield return (SelectionHandle.TopRight, bounds.TopRight);
        yield return (SelectionHandle.Right, new Point(bounds.Right, middleY));
        yield return (SelectionHandle.BottomRight, bounds.BottomRight);
        yield return (SelectionHandle.Bottom, new Point(middleX, bounds.Bottom));
        yield return (SelectionHandle.BottomLeft, bounds.BottomLeft);
        yield return (SelectionHandle.Left, new Point(bounds.Left, middleY));
    }

    private SelectionHandle HitSelectionHandle(Point point)
    {
        if (_selected is null || IsTextEditing || _selection.Count != 1)
            return SelectionHandle.None;
        var nearest = HandlePoints(_selected).OrderBy(handle => ((Vector)(handle.Position - point)).SquaredLength).First();
        return Point.Distance(nearest.Position, point) <= 8 / _zoom ? nearest.Handle : SelectionHandle.None;
    }

    public void BeginPropertyEdit()
    {
        VerifyAccess();
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
        if (_selected is null)
            return;
        _beforeProperty = Snapshot();
        _propertySource = _selected.Clone();
        _propertyTarget = _selected;
        _propertySources.Clear();
        foreach (AnnotationItem item in _selection) _propertySources.Add(item, item.Clone());
    }

    public void EndPropertyEdit()
    {
        VerifyAccess();
        EditorState? before = _beforeProperty;
        bool changed = PropertyHasChanges;
        _beforeProperty = null;
        _propertySource = null;
        _propertyTarget = null;
        _propertySources.Clear();
        if (before is not null && changed)
        {
            _undo.Push(before);
            _redo.Clear();
            FinishMutation();
        }
    }

    private void ChangeSelectedStyle(Action<AnnotationItem> change)
    {
        if (!IsTextEditing && _selection.Count > 1)
        {
            EditorState before = Snapshot();
            var previousSelection = _selection.ToDictionary(item => item, item => item.Clone());
            foreach (AnnotationItem selected in _selection) change(selected);
            if (!previousSelection.Any(pair => !pair.Key.ContentEquals(pair.Value))) return;
            RefreshMosaics();
            if (_beforeProperty is null)
            {
                _undo.Push(before);
                _redo.Clear();
                FinishMutation();
            }
            InvalidateScene();
            RaiseSelectionChanged();
            RaiseChanged();
            return;
        }
        AnnotationItem? item = SelectedItem;
        if (item is null)
            return;
        AnnotationItem previous = item.Clone();
        change(item);
        if (item.ContentEquals(previous))
            return;
        if (IsTextEditing)
            UpdateTextEditor();
        else if (_beforeProperty is null)
        {
            // The snapshot must precede the property assignment.
            AnnotationItem next = item.Clone();
            item.CopyFrom(previous);
            RememberMutation();
            item.CopyFrom(next);
            RefreshMosaics();
            FinishMutation();
        }
        else
            RefreshMosaics();
        InvalidateScene();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    private static bool IsUseful(AnnotationItem item)
    {
        if (item.Tool is AnnotationTool.Pen or AnnotationTool.Highlight)
            return item.Points.Count != 0;
        if (item.Tool == AnnotationTool.Arrow)
            return Point.Distance(item.End, item.Start) >= 2;
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
        InvalidateScene();
    }

    private EditorState Snapshot() => new(_revision, _annotations.Select(item => item.Clone()).ToList(), _annotations.Select((item, index) => (item, index)).Where(pair => _selection.Contains(pair.item)).Select(pair => pair.index).ToArray(), _image, IsCollage);

    private void Restore(EditorState state)
    {
        _annotations.Clear();
        _annotations.AddRange(state.Items.Select(item => item.Clone()));
        IsCollage = state.IsCollage;
        if (state.Image is not null) SetBoard(state.Image);
        _revision = state.Revision;
        SetSelection(state.SelectedIndices.Where(index => index >= 0 && index < _annotations.Count).Select(index => _annotations[index]));
        if (_fitMode) FitToView();
        else { ClampPan(); RefreshView(); }
        RefreshLanguage();
        InvalidateScene();
    }

    private void PrepareMosaic(AnnotationItem item)
    {
        if (_image is not null) PrepareMosaic(item, _image, _annotations, ref _pixelSource);
    }

    private static void PrepareMosaic(AnnotationItem item, Bitmap image, IReadOnlyList<AnnotationItem> annotations,
        ref PixelBuffer? pixelCache)
    {
        Rect bounds = item.Bounds;
        int imageWidth = image.PixelSize.Width, imageHeight = image.PixelSize.Height;
        int left = Math.Clamp((int)Math.Floor(bounds.Left), 0, imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(bounds.Top), 0, imageHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, imageWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), top + 1, imageHeight);
        int width = right - left;
        int height = bottom - top;
        item.Start = new Point(left, top);
        item.End = new Point(right, bottom);

        byte[] pixels;
        int stride = checked(width * 4);
        bool composite = annotations.Any(annotation => annotation.Tool == AnnotationTool.Image);
        if (composite)
        {
            // Photographs (and the annotations below this mask) are part of what gets pixelated.
            using var source = ImageTools.Render(new PixelSize(width, height), context =>
            {
                using (context.PushRenderOptions(SceneRenderOptions))
                using (context.PushTransform(Matrix.CreateTranslation(-left, -top)))
                using (context.PushClip(new Rect(left, top, width, height)))
                {
                    context.DrawImage(image, new Rect(0, 0, imageWidth, imageHeight));
                    foreach (AnnotationItem annotation in annotations)
                    {
                        if (ReferenceEquals(annotation, item)) break;
                        annotation.Draw(context);
                    }
                }
            });
            pixels = PixelBuffer.FromBitmap(source).Pixels;
        }
        else
            pixels = ReadSourcePixels(image, new PixelRect(left, top, width, height), ref pixelCache);
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
        item.Mosaic = new PixelBuffer(width, height, pixels).ToBitmap();
    }

    /// <summary>Copies source pixels (BGRA premultiplied) without converting the whole image when possible.</summary>
    private static byte[] ReadSourcePixels(Bitmap image, PixelRect area, ref PixelBuffer? pixelCache)
    {
        var pixels = new byte[checked(area.Width * area.Height * 4)];
        if (IsBoard(image))
        {
            // An empty collage board is white everywhere.
            Array.Fill(pixels, (byte)255);
            return pixels;
        }
        if (image.Format == Avalonia.Platform.PixelFormat.Bgra8888
            && image.AlphaFormat is Avalonia.Platform.AlphaFormat.Premul or Avalonia.Platform.AlphaFormat.Opaque)
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { image.CopyPixels(area, handle.AddrOfPinnedObject(), pixels.Length, area.Width * 4); }
            finally { handle.Free(); }
            if (image.AlphaFormat == Avalonia.Platform.AlphaFormat.Opaque) ImageOrientation.SetOpaque(pixels);
            return pixels;
        }
        pixelCache ??= ImageOrientation.ReadPixels(image);
        return pixelCache.Crop(area).Pixels;
    }

    [Flags]
    private enum SceneLayers
    {
        Image = 1,
        Annotations = 2,
        Interaction = 4,
        Document = Image | Annotations
    }

    private void DrawScene(DrawingContext context, SceneLayers layers, bool onScreen)
    {
        if (_image is null)
            return;
        using var options = context.PushRenderOptions(SceneRenderOptions);
        var imageBounds = new Rect(0, 0, PixelWidth, PixelHeight);
        if ((layers & SceneLayers.Document) != 0)
        {
            using (context.PushClip(imageBounds))
            {
                if ((layers & SceneLayers.Image) != 0)
                    context.DrawImage(_image, imageBounds);
                if ((layers & SceneLayers.Annotations) != 0)
                    foreach (AnnotationItem annotation in _annotations)
                        if (onScreen && IsImageCropping && ReferenceEquals(annotation, _cropTarget))
                            _cropDraft!.Draw(context);
                        else if (!onScreen || !IsTextEditing || !ReferenceEquals(annotation, _textTarget))
                            annotation.Draw(context);
            }
        }
        if ((layers & SceneLayers.Interaction) != 0)
        {
            if (onScreen && IsImageCropping)
            {
                DrawImageCrop(context, imageBounds);
                return;
            }
            if (_pending?.Tool == AnnotationTool.Text)
            {
                Rect bounds = _pending.Bounds;
                if (_pending.TextWidth == 0)
                    bounds = new Rect(_pending.Start, new Size(Math.Min(360, Math.Max(1, PixelWidth - _pending.Start.X)), _pending.FontSize * 1.4));
                context.DrawRectangle(null, new Pen(ThemeService.Brush("SelectionBorder"), 1 / _zoom), bounds);
            }
            else
                _pending?.Draw(context);
            if (!IsTextEditing)
                foreach (AnnotationItem item in _selection) DrawSelection(context, item);
            if (_marquee is not null)
                context.DrawRectangle(ThemeService.Brush("SelectionFill"), new Pen(ThemeService.Brush("SelectionBorder"), 1 / _zoom), _marquee.Value);
        }
    }

    private void DrawSelection(DrawingContext context, AnnotationItem item)
    {
        var outline = new Pen(ThemeService.Brush("SelectionBorder"), 1 / _zoom, DashStyle.Dash);
        if (item.Tool != AnnotationTool.Arrow)
            context.DrawRectangle(null, outline, EditableBounds(item));
        var handlePen = new Pen(ThemeService.Brush("SelectionBorder"), 1.5 / _zoom);
        if (_selection.Count == 1)
            foreach (var handle in HandlePoints(item))
                context.DrawEllipse(ThemeService.Brush("SelectionHandle"), handlePen, handle.Position, 4 / _zoom, 4 / _zoom);
    }

    private void ZoomFromCenter(double factor)
    {
        VerifyAccess();
        if (HasImage)
        {
            CompleteGesture();
            ZoomAt(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2), _zoom * factor);
        }
    }

    private void ZoomAt(Point viewportPoint, double zoom)
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
        double width = PixelWidth * _zoom;
        double height = PixelHeight * _zoom;
        double visibleX = Math.Min(60, width / 2);
        double visibleY = Math.Min(60, height / 2);
        _panX = Math.Clamp(_panX, visibleX - width, Math.Max(visibleX - width, _viewport.Bounds.Width - visibleX));
        _panY = Math.Clamp(_panY, visibleY - height, Math.Max(visibleY - height, _viewport.Bounds.Height - visibleY));
    }

    private bool InsideImage(Point point) => _image is not null && point.X >= 0 && point.Y >= 0 && point.X <= PixelWidth && point.Y <= PixelHeight;

    private Point ClampPoint(Point point) => _image is null ? point : new Point(Math.Clamp(point.X, 0, PixelWidth), Math.Clamp(point.Y, 0, PixelHeight));

    private Point ViewportToImage(Point point) => new((point.X - _panX) / _zoom, (point.Y - _panY) / _zoom);

    private void RefreshView()
    {
        _documentLayer.RenderTransform = new MatrixTransform(new Matrix(_zoom, 0, 0, _zoom, _panX, _panY));
        _zoomLabel.Text = $"{_zoom:P0}";
        UpdateTextEditor();
        // Committed annotations do not depend on zoom; handles and outlines keep a constant screen size.
        _overlay.InvalidateVisual();
    }

    private void UpdateCursor()
    {
        _viewport.Cursor = _panning || _spaceDown ? EditorCursor(StandardCursorType.Hand) : (_cropHandle != SelectionHandle.None ? _cropHandle : _transformHandle != SelectionHandle.None ? _transformHandle : _hoverHandle) switch
        {
            SelectionHandle.TopLeft => EditorCursor(StandardCursorType.TopLeftCorner),
            SelectionHandle.BottomRight => EditorCursor(StandardCursorType.BottomRightCorner),
            SelectionHandle.TopRight => EditorCursor(StandardCursorType.TopRightCorner),
            SelectionHandle.BottomLeft => EditorCursor(StandardCursorType.BottomLeftCorner),
            SelectionHandle.Left or SelectionHandle.Right => EditorCursor(StandardCursorType.SizeWestEast),
            SelectionHandle.Top or SelectionHandle.Bottom => EditorCursor(StandardCursorType.SizeNorthSouth),
            SelectionHandle.StartPoint or SelectionHandle.EndPoint or SelectionHandle.Move => EditorCursor(StandardCursorType.SizeAll),
            _ => EditorCursor(_tool == AnnotationTool.Select ? StandardCursorType.Arrow : _tool == AnnotationTool.Text ? StandardCursorType.Ibeam : StandardCursorType.Cross)
        };
        _textEditor.Cursor = EditorCursor(_panning ? StandardCursorType.Hand : StandardCursorType.Ibeam);
    }

    private static readonly Dictionary<StandardCursorType, Cursor> Cursors = new();
    private static Cursor EditorCursor(StandardCursorType type)
    {
        if (!Cursors.TryGetValue(type, out Cursor? cursor))
            Cursors[type] = cursor = new Cursor(type);
        return cursor;
    }

    private void SelectItem(AnnotationItem? item)
    {
        SetSelection(item is null ? Array.Empty<AnnotationItem>() : new[] { item });
    }

    private void InvalidateScene()
    {
        _surface.InvalidateVisual();
        _overlay.InvalidateVisual();
    }

    private void SizeDocument(Bitmap image)
    {
        foreach (ImageSurface layer in new[] { _imageSurface, _surface, _overlay })
        {
            layer.Width = image.PixelSize.Width;
            layer.Height = image.PixelSize.Height;
        }
        _documentLayer.Width = image.PixelSize.Width;
        _documentLayer.Height = image.PixelSize.Height;
        _imageLabel.Text = $"{image.PixelSize.Width:N0} × {image.PixelSize.Height:N0} px";
        EnsureRenderCache();
        _imageSurface.InvalidateVisual();
        InvalidateScene();
    }

    /// <summary>
    /// Skia keeps the mipmaps used to draw a zoomed-out photo in its resource cache (32 MB by
    /// default). The mipmaps of a large capture or collage do not fit, and would then be rebuilt
    /// on every frame while drawing; this grows the limit (never shrinks it) to hold them.
    /// </summary>
    private void EnsureRenderCache()
    {
        long pixels = (long)PixelWidth * PixelHeight
            + _annotations.Where(item => item.Image is not null).Sum(item => (long)item.Image!.PixelSize.Width * item.Image.PixelSize.Height);
        // Mipmaps take a third of the 32-bit pixels; keep room for a second set while zooming.
        long wanted = Math.Min(pixels * 4 / 3 * 2 + (32L << 20), 1L << 30);
        try
        {
            if (SkiaSharp.SKGraphics.GetResourceCacheTotalByteLimit() < wanted)
                SkiaSharp.SKGraphics.SetResourceCacheTotalByteLimit(wanted);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            SettingsStore.Log(ex);
        }
    }

    /// <summary>Re-tags the bitmap at 96 DPI (pixels unchanged) so one document unit is one pixel.</summary>
    private static Bitmap AtDocumentDpi(Bitmap image) => image.Dpi == ImageTools.Dpi ? image : ImageOrientation.ReadPixels(image).ToBitmap();

    private void RaiseChanged()
    {
        if (!_suppressEvents) Changed?.Invoke(this, EventArgs.Empty);
    }
    private void RaiseSelectionChanged()
    {
        if (!_suppressEvents) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            RefreshLanguage();
        else
            Dispatcher.UIThread.Post(RefreshLanguage);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        void Redraw()
        {
            if (!IsLoaded) return;
            UpdateTextSelectionBrush();
            InvalidateScene();
            _imageDropPreview?.InvalidateVisual();
        }
        if (Dispatcher.UIThread.CheckAccess()) Redraw();
        else Dispatcher.UIThread.Post(Redraw);
    }

    private void RefreshLanguage()
    {
        _hintLabel.Text = I18n.T(IsImageCropping ? CropHint : IsTextEditing ? TextHint : IsCollage
            ? "Ctrl / Shift 多選 · 雙擊圖片裁剪 · 拖曳空白處框選 · 方向鍵微調" : CanvasHint);
        RefreshImageCropLanguage();
    }

    private static Border CreateBadge(TextBlock label, HorizontalAlignment alignment) => new()
    {
        Child = label,
        Background = ThemeService.Brush("Badge"),
        BorderBrush = ThemeService.Brush("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(10, 6, 10, 6),
        Margin = new Thickness(16),
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Top,
        IsVisible = false,
        IsHitTestVisible = false
    };

    private static Control CreateEmptyState() => new Grid { IsHitTestVisible = false };

    private sealed record EditorState(long Revision, List<AnnotationItem> Items, int[] SelectedIndices, Bitmap? Image, bool IsCollage);

    private sealed class ImageSurface : Control
    {
        private readonly EditorView _editor;
        private readonly SceneLayers _layers;

        internal ImageSurface(EditorView editor, SceneLayers layers)
        {
            _editor = editor;
            _layers = layers;
            UseLayoutRounding = false;
            // Pointer input is handled by the viewport; the layers only draw.
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => _editor.DrawScene(context, _layers, onScreen: true);
    }
}

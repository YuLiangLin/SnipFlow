using System.Windows.Input;
using SnipFlow.Services;
using WpfUserControl = System.Windows.Controls.UserControl;
using WpfCanvas = System.Windows.Controls.Canvas;
using WpfTextBox = System.Windows.Controls.TextBox;
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
    private const string CanvasHint = "滾輪縮放 · 空白鍵拖曳 · Delete 刪除標註";
    private const string TextHint = "Ctrl + Enter 完成 · Esc 取消 · 點選外部完成";
    private readonly WpfCanvas _viewport;
    private readonly WpfCanvas _documentLayer;
    private readonly ImageSurface _surface;
    private readonly WpfTextBox _textEditor;
    private readonly Border _textEditBorder;
    private readonly TextBlock _hintLabel;
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
    private EditorState? _beforeTransform;
    private AnnotationItem? _transformSource;
    private SelectionHandle _transformHandle;
    private WpfPoint _transformAnchor;
    private AnnotationItem? _textTarget;
    private AnnotationItem? _textDraft;
    private EditorState? _beforeText;
    private EditorState? _beforeProperty;
    private AnnotationItem? _propertySource;
    private AnnotationItem? _propertyTarget;
    private Window? _hostWindow;
    private bool _syncTextUi;
    private bool _outsideTextClick;
    private bool _languageSubscribed;
    private AnnotationTool _tool = AnnotationTool.Arrow;
    private WpfColor _color = WpfColor.FromRgb(99, 213, 197);
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
    private WpfPoint _panStart;
    private WpfPoint _panOrigin;
    private WpfPoint _gestureStart;
    private SelectionHandle _hoverHandle;

    public event EventHandler? Changed;
    public event EventHandler? SelectionChanged;

    public bool HasImage => _image is not null;
    public bool HasEdits => HasImage && (_revision != _savedRevision || _pending is not null || TransformHasChanges || TextHasChanges || PropertyHasChanges);
    public bool CanUndo => _undo.Count != 0 || TextHasChanges || PropertyHasChanges;
    public bool CanRedo => _redo.Count != 0 && !TextHasChanges && !PropertyHasChanges;
    public double ZoomFactor => _zoom;
    public bool IsTextEditing => _textDraft is not null;
    public AnnotationTool? SelectedTool => SelectedItem?.Tool;
    public WpfColor? SelectedColor => SelectedItem?.Color;
    public double? SelectedStrokeWidth => SelectedItem?.Width;
    public double? SelectedTextSize => SelectedItem?.Tool == AnnotationTool.Text ? SelectedItem.FontSize : null;

    private AnnotationItem? SelectedItem => _textDraft ?? _selected;
    private bool TextHasChanges => _textDraft is not null && (_textTarget is null ? !string.IsNullOrWhiteSpace(_textDraft.Text) : !_textDraft.ContentEquals(_textTarget));
    private bool TransformHasChanges => _transformSource is not null && _selected is not null && !_selected.ContentEquals(_transformSource);
    private bool PropertyHasChanges => _propertySource is not null && _propertyTarget is not null && !_propertyTarget.ContentEquals(_propertySource);

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
                if (item.Tool != AnnotationTool.Text)
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
        Background = BrushFor("#0C111B");
        _viewport = new WpfCanvas
        {
            Background = BrushFor("#0C111B"),
            ClipToBounds = true
        };
        _documentLayer = new WpfCanvas { Visibility = Visibility.Collapsed };
        _surface = new ImageSurface(this);
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        _documentLayer.Children.Add(_surface);
        _textEditor = new WpfTextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Background = BrushFor("#F2151D2B"),
            Foreground = new SolidColorBrush(_color),
            CaretBrush = BrushFor("#EEF3F9"),
            SelectionBrush = BrushFor("#63D5C5"),
            SelectionOpacity = 0.35,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.IBeam,
            UndoLimit = 100,
            SpellCheck = { IsEnabled = false }
        };
        TextOptions.SetTextFormattingMode(_textEditor, TextFormattingMode.Ideal);
        _textEditBorder = new Border
        {
            Child = _textEditor,
            BorderBrush = BrushFor("#63D5C5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Visibility = Visibility.Collapsed
        };
        _documentLayer.Children.Add(_textEditBorder);
        _viewport.Children.Add(_documentLayer);
        _textEditor.TextChanged += (_, _) => TextEditorChanged();
        _textEditor.LostKeyboardFocus += (_, e) =>
        {
            if (IsTextEditing && !IsWithin(_textEditor, e.NewFocus as DependencyObject))
                CommitTextEdit();
        };

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

        _hintLabel = new TextBlock
        {
            Text = I18n.T(CanvasHint),
            FontSize = 12,
            Foreground = BrushFor("#B6C2D1"),
            Margin = new Thickness(20, 0, 20, 15),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        layout.Children.Add(_hintLabel);
        Content = layout;

        _viewport.PreviewMouseDown += OnMouseDown;
        _viewport.PreviewMouseMove += OnMouseMove;
        _viewport.PreviewMouseUp += OnMouseUp;
        _viewport.PreviewMouseWheel += OnMouseWheel;
        _viewport.LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _viewport))
                CompleteGesture();
        };
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
            AttachHostWindow();
            if (!_languageSubscribed)
            {
                I18n.Changed += OnLanguageChanged;
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
                _languageSubscribed = false;
            }
        };
        UpdateCursor();
    }

    public void LoadImage(BitmapSource image)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelWidth < 1 || image.PixelHeight < 1)
            throw new ArgumentException(I18n.T("影像尺寸不得為零。"), nameof(image));

        CancelTextEdit();
        CancelGesture();
        EndPropertyEdit();
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
        _documentLayer.Width = image.PixelWidth;
        _documentLayer.Height = image.PixelHeight;
        _documentLayer.Visibility = Visibility.Visible;
        _emptyState.Visibility = Visibility.Collapsed;
        _imageBadge.Visibility = _zoomBadge.Visibility = Visibility.Visible;
        _imageLabel.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px";
        _fitMode = true;
        FitToView();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    /// <summary>Returns only image pixels and committed annotations, at the original pixel size.</summary>
    public BitmapSource ExportImage()
    {
        VerifyAccess();
        if (_image is null)
            throw new InvalidOperationException(I18n.T("請先開啟或擷取影像。"));

        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
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
        CommitTextEdit();
        EndPropertyEdit();
        _savedRevision = _revision;
        RaiseChanged();
    }

    public void Undo()
    {
        VerifyAccess();
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
        CommitTextEdit();
        EndPropertyEdit();
        CancelGesture();
        _tool = tool;
        if (tool != AnnotationTool.Select)
            SelectItem(null);
        UpdateCursor();
        _surface.InvalidateVisual();
    }

    public void SetColor(WpfColor color)
    {
        VerifyAccess();
        _color = color;
        ChangeSelectedStyle(item =>
        {
            if (item.Tool != AnnotationTool.Mosaic)
                item.Color = color;
        });
    }

    public void DeleteSelection()
    {
        VerifyAccess();
        CommitTextEdit();
        EndPropertyEdit();
        CancelGesture();
        if (_selected is null)
            return;
        RememberMutation();
        _annotations.Remove(_selected);
        SelectItem(null);
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
        if (e.ChangedButton == MouseButton.Left && IsTextEditing && IsWithin(_textEditor, e.OriginalSource as DependencyObject))
            return;

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

        bool finishedText = _outsideTextClick;
        _outsideTextClick = false;
        if (IsTextEditing)
        {
            CommitTextEdit();
            finishedText = true;
        }
        EndPropertyEdit();
        Focus();
        WpfPoint point = ViewportToImage(e.GetPosition(_viewport));
        SelectionHandle handle = HitSelectionHandle(point);
        if (_selected is not null && handle != SelectionHandle.None)
        {
            BeginTransform(handle, point);
            _viewport.CaptureMouse();
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
            SelectItem(item);
            if (_selected?.Tool == AnnotationTool.Text && e.ClickCount == 2)
            {
                BeginTextEdit(_selected, null, point);
                e.Handled = true;
                return;
            }
            if (_selected is not null)
            {
                BeginTransform(SelectionHandle.Move, point);
                _viewport.CaptureMouse();
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
                _viewport.CaptureMouse();
            }
        }
        else
        {
            SelectItem(null);
            _gestureStart = point;
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

        WpfPoint point = ClampPoint(ViewportToImage(e.GetPosition(_viewport)));
        if (_pending is not null)
        {
            UpdatePending(point);
            _surface.InvalidateVisual();
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

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle))
            return;
        if (_pending is not null)
        {
            WpfPoint point = ClampPoint(ViewportToImage(e.GetPosition(_viewport)));
            UpdatePending(point);
        }
        else if (_beforeTransform is not null)
            UpdateTransform(ClampPoint(ViewportToImage(e.GetPosition(_viewport))));
        if (_panning || _pending is not null || _beforeTransform is not null)
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
        if (IsTextEditing)
        {
            if (control && e.Key == Key.Enter)
            {
                CommitTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
            // Native TextBox caret, selection, IME, Delete, and local undo stay intact.
            return;
        }
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
            SelectItem(null);
            e.Handled = true;
        }
        else if ((e.Key is Key.Enter or Key.F2) && _selected?.Tool == AnnotationTool.Text)
        {
            BeginTextEdit(_selected, null, null);
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
        if (IsTextEditing)
            return;
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void BeginTextEdit(AnnotationItem? annotation, WpfRect? placement, WpfPoint? caretPosition)
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
            WpfPoint start = ClampPoint(placement?.TopLeft ?? caretPosition ?? new WpfPoint(0, 0));
            double availableWidth = Math.Max(1, _image.PixelWidth - start.X);
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
        _selected = annotation;
        _syncTextUi = true;
        _textEditor.IsUndoEnabled = false;
        _textEditor.Text = _textDraft.Text;
        _textEditor.IsUndoEnabled = true;
        _syncTextUi = false;
        _textEditBorder.Visibility = Visibility.Visible;
        _spaceDown = false;
        _hintLabel.Text = I18n.T(TextHint);
        UpdateTextEditor();
        AttachHostWindow();
        _textEditor.Focus();
        _textEditor.CaretIndex = _textEditor.Text.Length;
        if (annotation is not null && caretPosition is WpfPoint position)
        {
            _textEditor.UpdateLayout();
            int index = _textEditor.GetCharacterIndexFromPoint(new WpfPoint(position.X - _textDraft.Start.X, position.Y - _textDraft.Start.Y), true);
            if (index >= 0)
                _textEditor.CaretIndex = index;
        }
        _surface.InvalidateVisual();
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
        draft.Text = _textEditor.Text;
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
                _selected = empty ? null : target;
            }
            else
            {
                _annotations.Add(draft);
                _selected = draft;
            }
            FinishMutation();
        }
        else
            _selected = target;
        _surface.InvalidateVisual();
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
        _selected = target;
        _surface.InvalidateVisual();
        RaiseSelectionChanged();
        RaiseChanged();
    }

    private void EndTextUi()
    {
        bool hadFocus = _textEditor.IsKeyboardFocusWithin;
        _textDraft = null;
        _textTarget = null;
        _beforeText = null;
        _textEditBorder.Visibility = Visibility.Collapsed;
        _hintLabel.Text = I18n.T(CanvasHint);
        if (hadFocus)
            Focus();
        UpdateCursor();
    }

    private void TextEditorChanged()
    {
        if (_syncTextUi || _textDraft is null)
            return;
        _textDraft.Text = _textEditor.Text;
        UpdateTextEditor();
        RaiseChanged();
    }

    private void UpdateTextEditor()
    {
        if (_textDraft is null)
            return;
        WpfRect bounds = _textDraft.Bounds;
        double border = 1 / _zoom;
        _textEditBorder.BorderThickness = new Thickness(border);
        _textEditor.FontSize = _textDraft.FontSize;
        _textEditor.Foreground = new SolidColorBrush(_textDraft.Color);
        bool lightInput = RelativeLuminance(_textDraft.Color) < 0.23;
        _textEditor.Background = BrushFor(lightInput ? "#F2FFFFFF" : "#F2151D2B");
        _textEditor.CaretBrush = BrushFor(lightInput ? "#0C111B" : "#EEF3F9");
        _textEditor.Width = Math.Max(1, bounds.Width);
        // The input viewport grows with all lines. Its extra caret room is UI only.
        _textEditor.Height = Math.Max(bounds.Height, _textDraft.FontSize * 1.4) + _textDraft.FontSize * 0.2;
        WpfCanvas.SetLeft(_textEditBorder, _textDraft.Start.X - border);
        WpfCanvas.SetTop(_textEditBorder, _textDraft.Start.Y - border);
    }

    private void AttachHostWindow()
    {
        Window? window = Window.GetWindow(this);
        if (ReferenceEquals(window, _hostWindow))
            return;
        DetachHostWindow();
        _hostWindow = window;
        if (window is not null)
            window.PreviewMouseDown += OnHostMouseDown;
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is not null)
            _hostWindow.PreviewMouseDown -= OnHostMouseDown;
        _hostWindow = null;
    }

    private void OnHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && IsTextEditing && !IsWithin(_textEditor, e.OriginalSource as DependencyObject))
        {
            _outsideTextClick = IsWithin(_viewport, e.OriginalSource as DependencyObject);
            CommitTextEdit();
        }
    }

    private static bool IsWithin(DependencyObject parent, DependencyObject? child)
    {
        while (child is not null)
        {
            if (ReferenceEquals(parent, child))
                return true;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return false;
    }

    private void CompleteGesture()
    {
        AnnotationItem? pending = _pending;
        EditorState? before = _beforeTransform;
        AnnotationItem? source = _transformSource;
        _pending = null;
        _beforeTransform = null;
        _transformSource = null;
        _transformHandle = SelectionHandle.None;
        _panning = false;
        if (_viewport.IsMouseCaptured)
            _viewport.ReleaseMouseCapture();

        if (pending?.Tool == AnnotationTool.Text)
        {
            WpfRect? placement = pending.TextWidth > 0 ? pending.Bounds : null;
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
        else if (before is not null && source is not null && _selected is not null && !_selected.ContentEquals(source))
        {
            if (_selected.Tool == AnnotationTool.Mosaic)
                PrepareMosaic(_selected);
            if (!_selected.ContentEquals(source))
            {
                _undo.Push(before);
                _redo.Clear();
                FinishMutation();
                RaiseSelectionChanged();
            }
        }

        UpdateCursor();
        _surface.InvalidateVisual();
    }

    private void CancelGesture()
    {
        EditorState? before = _beforeTransform;
        _pending = null;
        _beforeTransform = null;
        _transformSource = null;
        _transformHandle = SelectionHandle.None;
        _panning = false;
        if (before is not null)
            Restore(before);
        if (_viewport.IsMouseCaptured)
            _viewport.ReleaseMouseCapture();
        UpdateCursor();
        _surface.InvalidateVisual();
    }

    private void UpdatePending(WpfPoint point)
    {
        if (_pending is null)
            return;
        if (_pending.Tool == AnnotationTool.Text)
        {
            if ((point - _gestureStart).Length * _zoom >= 4)
            {
                var bounds = new WpfRect(_gestureStart, point);
                _pending.Start = bounds.TopLeft;
                _pending.End = bounds.TopLeft;
                _pending.TextWidth = Math.Min(Math.Max(24, bounds.Width), Math.Max(1, _image!.PixelWidth - bounds.Left));
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
        if ((_pending.Tool is AnnotationTool.Pen or AnnotationTool.Highlight) && (_pending.Points[^1] - point).Length >= 0.6)
            _pending.Points.Add(point);
    }

    private void BeginTransform(SelectionHandle handle, WpfPoint point)
    {
        if (_selected is null)
            return;
        EndPropertyEdit();
        _beforeTransform = Snapshot();
        _transformSource = _selected.Clone();
        _transformHandle = handle;
        _transformAnchor = point;
        _hoverHandle = handle;
        UpdateCursor();
    }

    private void UpdateTransform(WpfPoint point)
    {
        if (_selected is null || _transformSource is null || _image is null)
            return;
        AnnotationItem source = _transformSource;
        _selected.CopyFrom(source);
        WpfVector delta = point - _transformAnchor;
        if (delta.LengthSquared < 0.0000000001)
        {
            _surface.InvalidateVisual();
            return;
        }
        if (_transformHandle == SelectionHandle.Move)
        {
            WpfRect bounds = source.Bounds;
            double x = Math.Clamp(bounds.X + delta.X, 0, Math.Max(0, _image.PixelWidth - bounds.Width));
            double y = Math.Clamp(bounds.Y + delta.Y, 0, Math.Max(0, _image.PixelHeight - bounds.Height));
            _selected.Offset(new WpfVector(x - bounds.X, y - bounds.Y));
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
            WpfRect original = EditableBounds(source);
            WpfRect resized = ResizeBox(original, _transformHandle, delta, _selected.Tool == AnnotationTool.Text ? 12 : 2);
            ApplyResize(_selected, source, original, resized);
        }
        _surface.InvalidateVisual();
    }

    private WpfRect ResizeBox(WpfRect original, SelectionHandle handle, WpfVector delta, double minimum)
    {
        double left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;
        static double ClampRange(double value, double min, double max) => Math.Clamp(value, min, Math.Max(min, max));
        if (handle is SelectionHandle.TopLeft or SelectionHandle.Left or SelectionHandle.BottomLeft)
            left = ClampRange(left + delta.X, 0, right - minimum);
        if (handle is SelectionHandle.TopRight or SelectionHandle.Right or SelectionHandle.BottomRight)
            right = ClampRange(right + delta.X, left + minimum, _image!.PixelWidth);
        if (handle is SelectionHandle.TopLeft or SelectionHandle.Top or SelectionHandle.TopRight)
            top = ClampRange(top + delta.Y, 0, bottom - minimum);
        if (handle is SelectionHandle.BottomLeft or SelectionHandle.Bottom or SelectionHandle.BottomRight)
            bottom = ClampRange(bottom + delta.Y, top + 2, _image!.PixelHeight);
        return new WpfRect(new WpfPoint(left, top), new WpfPoint(right, bottom));
    }

    private static void ApplyResize(AnnotationItem item, AnnotationItem source, WpfRect original, WpfRect resized)
    {
        if (item.Tool == AnnotationTool.Text)
        {
            item.Start = resized.TopLeft;
            item.End = resized.TopLeft;
            item.TextWidth = resized.Width;
            item.TextBoxHeight = resized.Height;
        }
        else if (item.Tool is AnnotationTool.Pen or AnnotationTool.Highlight)
        {
            WpfPoint Map(WpfPoint point) => new(resized.X + (point.X - original.X) * resized.Width / original.Width, resized.Y + (point.Y - original.Y) * resized.Height / original.Height);
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

    private static WpfRect EditableBounds(AnnotationItem item)
    {
        WpfRect bounds = item.Bounds;
        if (bounds.Width < 2)
            bounds = new WpfRect(bounds.X - (2 - bounds.Width) / 2, bounds.Y, 2, bounds.Height);
        if (bounds.Height < 2)
            bounds = new WpfRect(bounds.X, bounds.Y - (2 - bounds.Height) / 2, bounds.Width, 2);
        return bounds;
    }

    private static IEnumerable<(SelectionHandle Handle, WpfPoint Position)> HandlePoints(AnnotationItem item)
    {
        if (item.Tool == AnnotationTool.Arrow)
        {
            yield return (SelectionHandle.StartPoint, item.Start);
            yield return (SelectionHandle.EndPoint, item.End);
            yield break;
        }
        WpfRect bounds = EditableBounds(item);
        double middleX = bounds.X + bounds.Width / 2;
        double middleY = bounds.Y + bounds.Height / 2;
        yield return (SelectionHandle.TopLeft, bounds.TopLeft);
        yield return (SelectionHandle.Top, new WpfPoint(middleX, bounds.Top));
        yield return (SelectionHandle.TopRight, bounds.TopRight);
        yield return (SelectionHandle.Right, new WpfPoint(bounds.Right, middleY));
        yield return (SelectionHandle.BottomRight, bounds.BottomRight);
        yield return (SelectionHandle.Bottom, new WpfPoint(middleX, bounds.Bottom));
        yield return (SelectionHandle.BottomLeft, bounds.BottomLeft);
        yield return (SelectionHandle.Left, new WpfPoint(bounds.Left, middleY));
    }

    private SelectionHandle HitSelectionHandle(WpfPoint point)
    {
        if (_selected is null || IsTextEditing)
            return SelectionHandle.None;
        var nearest = HandlePoints(_selected).OrderBy(handle => (handle.Position - point).LengthSquared).First();
        return (nearest.Position - point).Length <= 8 / _zoom ? nearest.Handle : SelectionHandle.None;
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
    }

    public void EndPropertyEdit()
    {
        VerifyAccess();
        EditorState? before = _beforeProperty;
        bool changed = PropertyHasChanges;
        _beforeProperty = null;
        _propertySource = null;
        _propertyTarget = null;
        if (before is not null && changed)
        {
            _undo.Push(before);
            _redo.Clear();
            FinishMutation();
        }
    }

    private void ChangeSelectedStyle(Action<AnnotationItem> change)
    {
        AnnotationItem? item = SelectedItem;
        if (item is null)
            return;
        AnnotationItem previous = item.Clone();
        change(item);
        if (item.ContentEquals(previous))
            return;
        if (item.Tool == AnnotationTool.Mosaic)
            PrepareMosaic(item);
        if (IsTextEditing)
            UpdateTextEditor();
        else if (_beforeProperty is null)
        {
            // The snapshot must precede the property assignment.
            AnnotationItem next = item.Clone();
            item.CopyFrom(previous);
            RememberMutation();
            item.CopyFrom(next);
            FinishMutation();
        }
        _surface.InvalidateVisual();
        RaiseSelectionChanged();
        RaiseChanged();
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

    private EditorState Snapshot() => new(_revision, _annotations.Select(item => item.Clone()).ToList(), _selected is null ? -1 : _annotations.IndexOf(_selected));

    private void Restore(EditorState state)
    {
        _annotations.Clear();
        _annotations.AddRange(state.Items.Select(item => item.Clone()));
        _revision = state.Revision;
        _selected = state.SelectedIndex >= 0 && state.SelectedIndex < _annotations.Count ? _annotations[state.SelectedIndex] : null;
        _surface.InvalidateVisual();
        RaiseSelectionChanged();
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
            if (!includeSelection || !IsTextEditing || !ReferenceEquals(annotation, _textTarget))
                annotation.Draw(context);
        context.Pop();
        if (includeSelection)
        {
            if (_pending?.Tool == AnnotationTool.Text)
            {
                WpfRect bounds = _pending.Bounds;
                if (_pending.TextWidth == 0)
                    bounds = new WpfRect(_pending.Start, new Size(Math.Min(360, Math.Max(1, _image.PixelWidth - _pending.Start.X)), _pending.FontSize * 1.4));
                context.DrawRectangle(null, new Pen(BrushFor("#63D5C5"), 1 / _zoom), bounds);
            }
            else
                _pending?.Draw(context);
            if (_selected is not null && !IsTextEditing)
                DrawSelection(context, _selected);
        }
    }

    private void DrawSelection(DrawingContext context, AnnotationItem item)
    {
        var outline = new Pen(BrushFor("#63D5C5"), 1 / _zoom) { DashStyle = DashStyles.Dash };
        if (item.Tool != AnnotationTool.Arrow)
            context.DrawRectangle(null, outline, EditableBounds(item));
        var handlePen = new Pen(BrushFor("#63D5C5"), 1.5 / _zoom);
        foreach (var handle in HandlePoints(item))
            context.DrawEllipse(BrushFor("#EEF3F9"), handlePen, handle.Position, 4 / _zoom, 4 / _zoom);
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

    private WpfPoint ViewportToImage(WpfPoint point) => new((point.X - _panX) / _zoom, (point.Y - _panY) / _zoom);

    private void RefreshView()
    {
        _documentLayer.RenderTransform = new MatrixTransform(_zoom, 0, 0, _zoom, _panX, _panY);
        _zoomLabel.Text = $"{_zoom:P0}";
        UpdateTextEditor();
        _surface.InvalidateVisual();
    }

    private void UpdateCursor()
    {
        _viewport.Cursor = _panning || _spaceDown ? Cursors.Hand : (_transformHandle != SelectionHandle.None ? _transformHandle : _hoverHandle) switch
        {
            SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
            SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
            SelectionHandle.Left or SelectionHandle.Right => Cursors.SizeWE,
            SelectionHandle.Top or SelectionHandle.Bottom => Cursors.SizeNS,
            SelectionHandle.StartPoint or SelectionHandle.EndPoint or SelectionHandle.Move => Cursors.SizeAll,
            _ => _tool == AnnotationTool.Select ? Cursors.Arrow : _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross
        };
        _textEditor.Cursor = _panning ? Cursors.Hand : Cursors.IBeam;
    }

    private void SelectItem(AnnotationItem? item)
    {
        if (!ReferenceEquals(_selected, item))
        {
            _selected = item;
            _hoverHandle = SelectionHandle.None;
            RaiseSelectionChanged();
        }
        _surface.InvalidateVisual();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            RefreshLanguage();
        else
            Dispatcher.BeginInvoke(new Action(RefreshLanguage));
    }

    private void RefreshLanguage() => _hintLabel.Text = I18n.T(IsTextEditing ? TextHint : CanvasHint);

    private static double RelativeLuminance(WpfColor color)
    {
        static double Channel(byte value)
        {
            double fraction = value / 255.0;
            return fraction <= 0.04045 ? fraction / 12.92 : Math.Pow((fraction + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

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

    private sealed record EditorState(long Revision, List<AnnotationItem> Items, int SelectedIndex);

    private sealed class ImageSurface(EditorView editor) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) => editor.DrawScene(drawingContext, includeSelection: true);
    }
}

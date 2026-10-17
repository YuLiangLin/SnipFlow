using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using SnipFlow.Capture;
using SnipFlow.Services;

namespace SnipFlow.Scroll;

/// <summary>Observes user scrolling without sending input or taking focus from the target.</summary>
/// <remarks>
/// <see cref="_targetBounds"/> uses desktop pixels (the unit of <see cref="Window.Position"/>); frame
/// rows, positions and trims use the pixels of the captured frames, which equal the desktop pixels except
/// on scaled Wayland desktops, where captures keep the compositor resolution (<see cref="FrameHeight"/>).
/// </remarks>
public sealed partial class ScrollCaptureWindow : DialogWindow
{
    private readonly PixelRect _targetBounds;
    private readonly ScrollCaptureTarget _target;
    private readonly ScrollCaptureWorkflow _workflow;
    // Frames stay in document order. Positions are offsets of the raw viewport in document pixels.
    private readonly List<Bitmap> _frames = new();
    private readonly List<int> _positions = new();
    private readonly Stack<bool> _addedAtTop = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer;
    private readonly Image _preview;
    private readonly Border _previewBorder;
    private readonly Image _joinPreview;
    private readonly TextBlock _status;
    private readonly TextBlock _dimensions;
    private readonly TextBlock _instruction;
    private readonly TextBlock _regionOnlyNote;
    private readonly Button _pauseButton;
    private readonly Button _undoButton;
    private readonly Button _finishButton;
    private readonly Button _manualButton;
    private readonly Button _applyTrimButton;
    private readonly StackPanel _manualPanel;
    private readonly ComboBox _direction;
    private readonly Slider _overlapSlider;
    private readonly TextBlock _overlapLabel;
    private readonly Button _confirmJoinButton;
    private readonly TextBox _topTrimBox;
    private readonly TextBox _bottomTrimBox;
    private readonly Expander _trimExpander;
    private readonly Expander _rangeExpander;
    private readonly StackPanel _finishNotice;
    private Bitmap? _anchorRaw;
    private int _anchorPosition;
    private Bitmap? _lastProcessedRaw;
    private Bitmap? _unmatchedRaw;
    private PixelSize _frameSize;
    private int _topTrim;
    private int _bottomTrim;
    private bool _userPaused = true;
    private bool _busy;
    private bool _finishing;
    private bool _closed;
    private bool _toolbarExcluded;
    private bool _limitReached;
    private nint _handle;

    public Bitmap? Result { get; private set; }

    public ScrollCaptureWindow(PixelRect targetPhysicalBounds, Bitmap? initialFrame = null,
        ScrollCaptureWorkflow workflow = ScrollCaptureWorkflow.Quick, WindowCaptureTarget? selectedTarget = null)
    {
        if (targetPhysicalBounds.Width < 32 || targetPhysicalBounds.Height < 48)
            throw new ArgumentException(I18n.T("請選取至少 32 × 48 像素的捲動內容範圍。"), nameof(targetPhysicalBounds));
        if ((long)targetPhysicalBounds.Width * targetPhysicalBounds.Height > ImageStitcher.MaxOutputPixels
            || (initialFrame is not null && (long)initialFrame.PixelSize.Width * initialFrame.PixelSize.Height > ImageStitcher.MaxOutputPixels))
            throw new ArgumentException(I18n.T("選取範圍太大，請縮小範圍後重試。"), nameof(targetPhysicalBounds));
        if (initialFrame is not null && !FrameFitsRegion(initialFrame.PixelSize, targetPhysicalBounds))
            throw new ArgumentException("The initial frame must match the selected region.", nameof(initialFrame));
        if (workflow is not ScrollCaptureWorkflow.Quick and not ScrollCaptureWorkflow.Precise and not ScrollCaptureWorkflow.Code)
            throw new ArgumentOutOfRangeException(nameof(workflow));

        _targetBounds = targetPhysicalBounds;
        _target = new ScrollCaptureTarget(_targetBounds, selectedTarget);
        _workflow = workflow;
        Title = I18n.T(workflow == ScrollCaptureWorkflow.Code ? "SnipFlow · 程式碼長截圖" : "SnipFlow · 對話長截圖");
        Width = 410;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        Background = Brush("Panel");
        ShowActivated = false;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = UiThemes.AppFont;
        FontSize = 12;
        Foreground = Brush("Ink");

        var shell = new Border
        {
            BorderBrush = Brush("Line"), BorderThickness = new Thickness(1),
            Background = Brush("Panel"), Padding = new Thickness(14)
        };
        _shell = shell;
        var layout = new StackPanel();
        _layout = layout;
        shell.Child = layout;
        Content = new ScrollViewer
        {
            Content = shell, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = new TextBlock
        {
            Text = I18n.T(workflow == ScrollCaptureWorkflow.Code ? "程式碼長截圖" : "對話長截圖"),
            FontWeight = FontWeight.SemiBold, FontSize = 15, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(title);
        WindowChrome.EnableTitleBarDrag(this, title, allowMaximize: false);
        var cancel = MakeButton("取消");
        cancel.Padding = new Thickness(9, 4, 9, 4);
        cancel.Margin = new Thickness(0);
        cancel.Click += (_, _) => Close();
        _cancelButton = cancel;
        _headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _headerActions.Children.Add(cancel);
        Grid.SetColumn(_headerActions, 1);
        header.Children.Add(_headerActions);
        layout.Children.Add(header);
        _instruction = new TextBlock
        {
            Text = I18n.T(workflow == ScrollCaptureWorkflow.Code
                ? "在原視窗慢慢捲動，保留重疊；按「完成」產生長圖。"
                : "在原視窗慢慢捲動，完成後按「完成」。"),
            Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        layout.Children.Add(_instruction);
        // Shown once when the target cannot be tracked (Wayland): only pixels guard the collection.
        _regionOnlyNote = new TextBlock
        {
            Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8), IsVisible = false
        };
        layout.Children.Add(_regionOnlyNote);

        var previewRow = new Grid { Margin = new Thickness(0, 0, 0, 8), ColumnDefinitions = new ColumnDefinitions("96,*") };
        _preview = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(_preview, BitmapInterpolationMode.HighQuality);
        _previewBorder = new Border
        {
            Height = 58, Background = Brush("Bg"), BorderBrush = Brush("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            ClipToBounds = true, Child = _preview
        };
        ToolTip.SetTip(_previewBorder, I18n.T("已收集長圖預覽"));
        previewRow.Children.Add(_previewBorder);
        var information = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(information, 1);
        _dimensions = new TextBlock { Foreground = Brush("Muted"), FontSize = 11 };
        _status = new TextBlock
        {
            Text = I18n.T("等待原視窗；現有內容會保留。"), TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Ink"), LineHeight = 17, Margin = new Thickness(0, 5, 0, 0)
        };
        information.Children.Add(_dimensions);
        information.Children.Add(_status);
        previewRow.Children.Add(information);
        layout.Children.Add(previewRow);

        var rangeControls = CreateRangeControls();
        if (workflow == ScrollCaptureWorkflow.Code) layout.Children.Add(rangeControls);

        var actions = new WrapPanel();
        _actions = actions;
        _pauseButton = MakeButton("暫停");
        _pauseButton.Click += (_, _) => TogglePause();
        _finishButton = MakeButton("完成", accent: true);
        _finishButton.Click += async (_, _) => await FinishAsync();
        _undoButton = MakeButton("上一步");
        _undoButton.Click += (_, _) => UndoFrame();
        _manualButton = MakeButton("手動接合");
        _manualButton.Click += (_, _) => BeginManualJoin();
        actions.Children.Add(_pauseButton);
        actions.Children.Add(_finishButton);
        actions.Children.Add(_manualButton);
        layout.Children.Add(actions);

        _finishNotice = CreateFinishNotice();
        layout.Children.Add(_finishNotice);

        _manualPanel = new StackPanel { IsVisible = false, Margin = new Thickness(0, 11, 0, 0) };
        _manualPanel.Children.Add(new TextBlock
        {
            Text = I18n.T("只接到已擷取內容的最上端或最下端。請確認接縫。"),
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("Warning")
        });
        _joinPreview = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapInterpolationMode(_joinPreview, BitmapInterpolationMode.None);
        _manualPanel.Children.Add(new ScrollViewer
        {
            Content = _joinPreview, Height = 160, Margin = new Thickness(0, 7, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brush("Bg")
        });
        _direction = new ComboBox
        {
            Background = Brush("Raised"), Foreground = Brush("Ink"), HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(7, 4, 7, 4),
            ItemsSource = new[] { I18n.T("向上補較早內容"), I18n.T("向下加入新內容") }, SelectedIndex = 1
        };
        _direction.SelectionChanged += (_, _) => UpdateJoinPreview();
        _manualPanel.Children.Add(_direction);
        _overlapLabel = new TextBlock { Margin = new Thickness(0, 7, 0, 0), Foreground = Brush("Muted") };
        _manualPanel.Children.Add(_overlapLabel);
        _overlapSlider = new Slider
        {
            Minimum = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 6)
        };
        _overlapSlider.ValueChanged += (_, _) => UpdateJoinPreview();
        _manualPanel.Children.Add(_overlapSlider);
        var joinActions = new StackPanel { Orientation = Orientation.Horizontal };
        _confirmJoinButton = MakeButton("確認接縫", accent: true);
        _confirmJoinButton.Click += (_, _) => ConfirmManualJoin();
        var discard = MakeButton("捨棄此畫面");
        discard.Click += (_, _) =>
        {
            _manualPanel.IsVisible = false;
            _unmatchedRaw = null;
            RefreshPreview();
            UpdateButtons();
            SetStatus("已捨棄未接合畫面；按「繼續」再捲動。");
        };
        joinActions.Children.Add(_confirmJoinButton);
        joinActions.Children.Add(discard);
        _manualPanel.Children.Add(joinActions);
        layout.Children.Add(_manualPanel);

        _trimExpander = new Expander
        {
            Header = I18n.T("略過固定頂部列／底部輸入框"), Foreground = Brush("Muted"),
            Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var trimContent = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        trimContent.Children.Add(new TextBlock
        {
            Text = I18n.T("手動設定要略過的像素；至少保留 48 px。"),
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("Muted"), FontSize = 11
        });
        var trimActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        trimActions.Children.Add(new TextBlock { Text = I18n.T("頂部"), VerticalAlignment = VerticalAlignment.Center });
        _topTrimBox = MakeTrimBox();
        trimActions.Children.Add(_topTrimBox);
        trimActions.Children.Add(new TextBlock { Text = I18n.T("底部"), VerticalAlignment = VerticalAlignment.Center });
        _bottomTrimBox = MakeTrimBox();
        trimActions.Children.Add(_bottomTrimBox);
        _applyTrimButton = MakeButton("套用裁切");
        _applyTrimButton.Click += (_, _) => ApplyTrim();
        trimActions.Children.Add(_applyTrimButton);
        trimContent.Children.Add(trimActions);
        _trimExpander.Content = trimContent;
        var adjustments = new StackPanel();
        if (workflow != ScrollCaptureWorkflow.Code) adjustments.Children.Add(rangeControls);
        _undoButton.Margin = new Thickness(0, 8, 0, 0);
        _undoButton.HorizontalAlignment = HorizontalAlignment.Left;
        adjustments.Children.Add(_undoButton);
        adjustments.Children.Add(_trimExpander);
        _rangeExpander = new Expander
        {
            Header = I18n.T("微調範圍"), Content = adjustments,
            IsExpanded = workflow == ScrollCaptureWorkflow.Precise,
            Foreground = Brush("Muted"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        layout.Children.Add(_rangeExpander);
        CreateCompactBar(workflow);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(275) };
        _timer.Tick += async (_, _) => await ObserveAsync();
        PlaceBeforeShow();
        Opened += async (_, _) =>
        {
            _handle = TryGetPlatformHandle()?.Handle ?? 0;
            _toolbarExcluded = ScrollCaptureTarget.ExcludeToolbar(_handle);
            WindowAppearance.Apply(this);
            if (initialFrame is not null)
                AddFirstFrame(initialFrame);
            _userPaused = _workflow == ScrollCaptureWorkflow.Precise;
            PlaceToolbar();
            LimitWindowHeight();
            ShowRegionOnlyNote();
            SetStatus(_workflow switch
            {
                ScrollCaptureWorkflow.Code => "已開始收集程式碼畫面；自行捲動後按「完成」。",
                ScrollCaptureWorkflow.Precise => "從首頁選起點，再捲動對話。終點可從末頁選取。",
                _ => "自動收集中；慢慢捲動後按「完成」。"
            });
            UpdateButtons();
            // Wayland: a screen stream keeps repeated captures fast and without screenshot flashes.
            // The desktop may ask once which screen to share; cancelling that cancels the capture.
            if (!await EnsureLiveCaptureAsync(initial: true) || _closed) return;
            if (!_userPaused) StartTimer();
            UpdateButtons();
        };
        PositionChanged += (_, _) => LimitWindowHeight();
        SizeChanged += (_, _) =>
        {
            LimitWindowHeight();
            if (_handle != 0 && !_closed) KeepToolbarClear();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _lifetime.Cancel();
            _activePicker?.Close();
            _liveCapture?.Dispose();
            _liveCapture = null;
            _ = ReleaseFramesAsync(keep: initialFrame);
        };
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
        }, RoutingStrategies.Tunnel);
        UpdateButtons();
    }

    /// <summary>
    /// Captures have exactly the region's pixels, except in Wayland sessions, where the portal and the
    /// screen stream keep the compositor resolution (larger than the region on scaled desktops).
    /// </summary>
    private static bool FrameFitsRegion(PixelSize frame, PixelRect region) =>
        frame.Width > 0 && frame.Height > 0 && (PlatformInfo.IsWayland || frame == region.Size);

    private async Task ObserveAsync()
    {
        if (_closed || _finishing || _busy || _pickingBoundary || _userPaused || NeedsStart
            || _rangeEnd is not null || _manualPanel.IsVisible) return;
        if (!IsLiveCaptureRunning())
        {
            PauseForStoppedStream();
            return;
        }
        if (DateTime.UtcNow < _settleUntil) return;
        if (!CanObserveTarget(out var reason))
        {
            ResetObservation(discardPending: false);
            SetStatus(reason, warning: true);
            return;
        }
        _busy = true;
        UpdateButtons();
        try
        {
            // The toolbar stays visible (excluded from capture, or outside the region). No opacity
            // changes or Activate calls; the capture itself runs off the UI thread.
            var raw = await CaptureTargetAsync();
            if (!CanCommitObservation()) return;
            if (!AcceptFrameSize(raw)) return;
            if (_frames.Count == 0)
            {
                AddFirstFrame(raw);
                return;
            }
            if (_lastProcessedRaw is not null)
            {
                var processed = _lastProcessedRaw;
                var same = await Task.Run(() => ImageStitcher.AreSameImages(Crop(processed), Crop(raw)), _lifetime.Token);
                if (!CanCommitObservation()) return;
                if (same)
                {
                    if (!ReferenceEquals(_unmatchedRaw, _lastProcessedRaw)) _unmatchedRaw = null;
                    return;
                }
            }
            var bodies = _frames.Select(Crop).ToArray();
            var positions = _positions.ToArray();
            var anchor = Crop(_anchorRaw ?? _frames[^1]);
            var anchorPosition = _anchorPosition;
            var body = Crop(raw);
            var analysis = await Task.Run(() => AnalyzeFrame(anchor, anchorPosition, body, bodies, positions, _lifetime.Token), _lifetime.Token);
            if (!CanCommitObservation()) return;
            // A faster scroll may skip the overlap with the last stored frame. Commit the
            // last reliable intermediate viewport, then retry against that new frontier.
            if (analysis.Kind == FrameKind.Unreliable && _pendingAlignedRaw is not null)
            {
                var intermediate = _pendingAlignedRaw;
                var intermediatePosition = _pendingAlignedPosition;
                if (TryAcceptFrame(intermediate, intermediatePosition, intermediatePosition < _positions[0]))
                {
                    bodies = _frames.Select(Crop).ToArray();
                    positions = _positions.ToArray();
                    anchor = Crop(_anchorRaw!);
                    anchorPosition = _anchorPosition;
                    analysis = await Task.Run(() => AnalyzeFrame(anchor, anchorPosition, body, bodies, positions, _lifetime.Token), _lifetime.Token);
                    if (!CanCommitObservation()) return;
                }
                else return;
            }
            _lastProcessedRaw = raw;
            if (analysis.Kind == FrameKind.Known)
            {
                _anchorRaw = raw;
                _anchorPosition = analysis.Position;
                _unmatchedRaw = null;
                _pendingAlignedRaw = null;
                SetStatus("目前內容已收集，可繼續捲動或完成。");
            }
            else if (analysis.Kind == FrameKind.New)
            {
                var frontierPosition = analysis.AtTop ? _positions[0] : _positions[^1];
                var distance = Math.Abs((long)analysis.Position - frontierPosition);
                if (distance >= Math.Max(24, BodyHeight * 0.45))
                    TryAcceptFrame(raw, analysis.Position, analysis.AtTop);
                else
                {
                    _pendingAlignedRaw = raw;
                    _pendingAlignedPosition = analysis.Position;
                    _unmatchedRaw = null;
                    SetStatus("目前畫面已對齊，可繼續捲動或完成。");
                }
            }
            else
            {
                _unmatchedRaw = raw;
                SetStatus(analysis.FixedEdge
                    ? "固定列影響對齊；請在「微調範圍」略過頂部／底部。此畫面未加入。"
                    : "暫時無法對齊。回捲一點、保留重疊，或按「手動接合」。", warning: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            SettingsStore.Log(exception);
            if (!_closed)
            {
                _userPaused = true;
                SetStatus(I18n.F("擷取失敗，現有內容已保留：{0}", exception.Message), warning: true);
            }
        }
        finally
        {
            _busy = false;
            if (!_closed) UpdateButtons();
        }
    }

    private bool CanCommitObservation()
    {
        if (_closed || _finishing || _pickingBoundary || _userPaused || _manualPanel.IsVisible) return false;
        if (CanObserveTarget(out var reason)) return true;
        ResetObservation(discardPending: false);
        SetStatus(reason, warning: true);
        return false;
    }

    /// <summary>
    /// <see cref="ScrollCaptureTarget.CanObserve"/>, plus SnipFlow's own toolbar: without capture
    /// exclusion (always on Linux) it must stay outside the region, also where window stacking is
    /// unknown (Wayland), so it never appears in a frame.
    /// </summary>
    private bool CanObserveTarget(out string reason, bool allowToolbarForeground = false)
    {
        if (!_toolbarExcluded && ToolbarCoversRegion())
        {
            DelayObservation();
            reason = "請把操作條移到選取範圍外，避免出現在截圖中。";
            return false;
        }
        var observable = _target.CanObserve(_handle, _toolbarExcluded, out reason, allowToolbarForeground);
        if (_target.IsRegionOnly) ShowRegionOnlyNote();
        return observable;
    }

    /// <summary>Fresh pixels of the region, read off the UI thread (Wayland reads the screen stream).</summary>
    private Task<Bitmap> CaptureTargetAsync() =>
        Task.Run(() => ScreenshotService.CaptureRectangleAsync(_targetBounds), _lifetime.Token);

    /// <summary>
    /// Every stored frame must have the same size. The size is fixed by the first frame; it can only
    /// differ when the first frame came from another source (the Wayland screenshot portal before the
    /// screen stream started), and then the fresh viewport replaces it while nothing has been joined.
    /// </summary>
    private bool AcceptFrameSize(Bitmap raw)
    {
        if (_frames.Count == 0 || raw.PixelSize == _frameSize) return true;
        if (_frames.Count == 1 && _addedAtTop.Count == 0 && _rangeStart is null && _rangeEnd is null
            && _topTrim == 0 && _bottomTrim == 0 && FrameFitsRegion(raw.PixelSize, _targetBounds))
        {
            _frames.Clear();
            _positions.Clear();
            _pendingAlignedRaw = null;
            _unmatchedRaw = null;
            AddFirstFrame(raw);
            return false;
        }
        ResetObservation(discardPending: false);
        SetStatus("擷取畫面的大小已改變，已保留現有內容；請完成後重新選取範圍。", warning: true);
        return false;
    }

    private static FrameAnalysis AnalyzeFrame(FrameRows anchor, int anchorPosition, FrameRows current,
        IReadOnlyList<FrameRows> frames, IReadOnlyList<int> positions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (anchorPosition >= positions[0] && anchorPosition <= positions[^1]
            && ImageStitcher.AreSameImages(anchor, current)) return new FrameAnalysis(FrameKind.Known, anchorPosition);
        var match = ImageStitcher.FindScroll(anchor, current);
        int? position = match.IsReliable ? checked(anchorPosition + match.AdvancePixels) : null;
        if (position is null)
        {
            // Reacquire a known viewport after pausing or changing direction. Conflicting
            // reliable matches are rejected instead of choosing one repeated chat bubble.
            var recovered = new List<int>();
            for (var index = 0; index < frames.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (ImageStitcher.AreSameImages(frames[index], current))
                    return new FrameAnalysis(FrameKind.Known, positions[index]);
                var known = ImageStitcher.FindScroll(frames[index], current);
                if (known.IsReliable) recovered.Add(checked(positions[index] + known.AdvancePixels));
            }
            if (recovered.Count > 0 && recovered.Max() - recovered.Min() <= 2) position = recovered[0];
        }
        if (position is null)
            return new FrameAnalysis(FrameKind.Unreliable, FixedEdge: ImageStitcher.HasFixedEdgeBand(anchor, current));
        if (position >= positions[0] && position <= positions[^1])
            return new FrameAnalysis(FrameKind.Known, position.Value);

        var atTop = position < positions[0];
        var frontierIndex = atTop ? 0 : frames.Count - 1;
        var frontier = ImageStitcher.FindScroll(frames[frontierIndex], current);
        if (!frontier.IsReliable || (atTop ? frontier.AdvancePixels >= 0 : frontier.AdvancePixels <= 0))
            return new FrameAnalysis(FrameKind.Unreliable, FixedEdge: ImageStitcher.HasFixedEdgeBand(anchor, current));
        var frontierPosition = checked(positions[frontierIndex] + frontier.AdvancePixels);
        if (Math.Abs((long)frontierPosition - position.Value) > 2)
            return new FrameAnalysis(FrameKind.Unreliable);
        if (ImageStitcher.HasFixedEdgeBand(frames[frontierIndex], current))
            return new FrameAnalysis(FrameKind.Unreliable, FixedEdge: true);
        return new FrameAnalysis(FrameKind.New, frontierPosition, atTop);
    }

    private void AddFirstFrame(Bitmap frame)
    {
        if (_frames.Count == 0) _frameSize = frame.PixelSize;
        _frames.Add(frame);
        _positions.Add(0);
        _anchorRaw = frame;
        _anchorPosition = 0;
        _lastProcessedRaw = frame;
        RefreshPreview();
        UpdateDimensions();
        SetStatus("已收集首張；在原視窗慢慢捲動。");
    }

    private bool TryAcceptFrame(Bitmap raw, int position, bool atTop)
    {
        if (_frames.Count >= ImageStitcher.MaxFrames)
        {
            PauseAtLimit(I18n.F("已達 {0} 張上限；按「完成」保留已收集內容。", ImageStitcher.MaxFrames));
            return false;
        }
        var storedPixels = (long)(_frames.Count + 1) * FrameWidth * FrameHeight;
        if (storedPixels > ImageStitcher.MaxStoredPixels)
        {
            PauseAtLimit("已達記憶體上限；按「完成」保留已收集內容。");
            return false;
        }
        var frontier = atTop ? _positions[0] : _positions[^1];
        var advance = Math.Abs((long)position - frontier);
        if (advance < 1 || advance > BodyHeight || (atTop ? position >= frontier : position <= frontier))
        {
            SetStatus("接合位置超出前沿，請保留更多重疊內容。", warning: true);
            return false;
        }
        var first = atTop ? position : _positions[0];
        var last = atTop ? _positions[^1] : position;
        if ((last - (long)first + BodyHeight) * FrameWidth > ImageStitcher.MaxOutputPixels)
        {
            PauseAtLimit("已達長圖大小上限；按「完成」保留已收集內容。");
            return false;
        }
        if (atTop)
        {
            _frames.Insert(0, raw);
            _positions.Insert(0, position);
        }
        else
        {
            _frames.Add(raw);
            _positions.Add(position);
        }
        _addedAtTop.Push(atTop);
        _anchorRaw = raw;
        _anchorPosition = position;
        _unmatchedRaw = null;
        _pendingAlignedRaw = null;
        if (!_finishing && !_pickingBoundary && !_userPaused) _limitReached = false;
        HideFinishNotice();
        RefreshPreview();
        UpdateDimensions();
        SetStatus(atTop ? "已補上較早內容，繼續向上捲動。" : "已加入新內容，繼續向下捲動。");
        return true;
    }

    /// <summary>Frame pixels; the region size until the first frame fixes it.</summary>
    private int FrameWidth => _frameSize.Width > 0 ? _frameSize.Width : _targetBounds.Width;
    private int FrameHeight => _frameSize.Height > 0 ? _frameSize.Height : _targetBounds.Height;

    private int BodyHeight => FrameHeight - _topTrim - _bottomTrim;

    private FrameRows Crop(Bitmap image) => Crop(image, _topTrim, _bottomTrim);

    private static FrameRows Crop(Bitmap image, int top, int bottom) =>
        new(image, top, image.PixelSize.Height - top - bottom);

    private int[] GetOverlaps(int height)
    {
        var overlaps = new int[Math.Max(0, _positions.Count - 1)];
        for (var index = 1; index < _positions.Count; index++)
            overlaps[index - 1] = checked(height - (_positions[index] - _positions[index - 1]));
        return overlaps;
    }

    private void TogglePause()
    {
        if (_closed || _finishing || _pickingBoundary || NeedsStart || _manualPanel.IsVisible) return;
        _userPaused = !_userPaused;
        ResetObservation(discardPending: false);
        if (!_userPaused)
        {
            _rangeEnd = null;
            HideFinishNotice();
            StartTimer();
        }
        else _timer.Stop();
        SetStatus(_userPaused ? "已暫停，擷取內容會保留。" : "切回目標視窗繼續捲動。");
        UpdateButtons();
    }

    private void PauseAtLimit(string message)
    {
        _userPaused = true;
        _limitReached = true;
        _timer.Stop();
        // A reliable pending tail may still fit after undo. Keep it for an explicit finish.
        ResetObservation(discardPending: false);
        SetStatus(message, warning: true);
        UpdateButtons();
    }

    private void UndoFrame()
    {
        if (_busy || _finishing || _pickingBoundary || _frames.Count <= 1 || _addedAtTop.Count == 0) return;
        var index = _addedAtTop.Pop() ? 0 : _frames.Count - 1;
        _frames.RemoveAt(index);
        _positions.RemoveAt(index);
        _anchorRaw = _frames[^1];
        _anchorPosition = _positions[^1];
        _userPaused = true;
        _unmatchedRaw = null;
        _manualPanel.IsVisible = false;
        _limitReached = false;
        HideFinishNotice();
        InvalidateRangeAfterUndo();
        ResetObservation();
        RefreshPreview();
        UpdateDimensions();
        UpdateButtons();
        SetStatus("已移除上一張並暫停；按「繼續」再捲動。");
    }

    private void ApplyTrim()
    {
        if (_busy || _finishing || _pickingBoundary || _manualPanel.IsVisible) return;
        if (!int.TryParse(_topTrimBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var top)
            || !int.TryParse(_bottomTrimBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var bottom)
            || top < 0 || bottom < 0 || (long)top + bottom > FrameHeight - 48)
        {
            SetStatus("裁切值須為非負整數，並至少保留 48 px。", warning: true);
            return;
        }
        var height = FrameHeight - top - bottom;
        if (!CanTrimRange(top, bottom)) return;
        if (_frames.Count > 0 && ((long)_positions[^1] - _positions[0] + height)
            * FrameWidth > ImageStitcher.MaxOutputPixels)
        {
            PauseAtLimit("已達長圖大小上限；按「完成」保留已收集內容。");
            return;
        }
        for (var index = 1; index < _positions.Count; index++)
        {
            if (_positions[index] - _positions[index - 1] > height)
            {
                SetStatus("此裁切會讓接縫失去重疊，請減少裁切或重新截取。", warning: true);
                return;
            }
        }
        _topTrim = top;
        _bottomTrim = bottom;
        HideFinishNotice();
        // A previously aligned tail was validated against the old crop. Keep its pixels
        // as unresolved content until the observer or an explicit join checks the new crop.
        if (_pendingAlignedRaw is not null) _unmatchedRaw = _pendingAlignedRaw;
        _anchorRaw = _frames.Count > 0 ? _frames[^1] : null;
        _anchorPosition = _frames.Count > 0 ? _positions[^1] : 0;
        ResetObservation();
        RefreshPreview();
        UpdateDimensions();
        SetStatus("裁切已套用到所有畫面；切回目標繼續捲動。");
        UpdateButtons();
    }

    private void BeginManualJoin()
    {
        if (_busy || _finishing || _pickingBoundary || _unmatchedRaw is null || _frames.Count == 0) return;
        _userPaused = true;
        _timer.Stop();
        ResetObservation(discardPending: false);
        HideFinishNotice();
        ExpandToolbar();
        _manualPanel.IsVisible = true;
        _overlapSlider.Maximum = BodyHeight - 1;
        _overlapSlider.Value = Math.Min(BodyHeight - 1, BodyHeight / 2);
        UpdateJoinPreview();
        UpdateButtons();
        SetStatus("請檢查接縫，尚未加入此畫面。", warning: true);
    }

    private void UpdateJoinPreview()
    {
        // SelectionChanged can run while the constructor is still creating the controls.
        if (_unmatchedRaw is null || _frames.Count == 0 || _overlapSlider is null || _overlapLabel is null) return;
        var overlap = (int)Math.Clamp(Math.Round(_overlapSlider.Value), 0, BodyHeight - 1);
        var atTop = _direction.SelectedIndex == 0;
        _overlapLabel.Text = I18n.F("重疊 {0:N0} px · 新增 {1:N0} px", overlap, BodyHeight - overlap);
        var body = Crop(_unmatchedRaw);
        var frontier = Crop(atTop ? _frames[0] : _frames[^1]);
        _joinPreview.Source = atTop ? ImageStitcher.CreateJoinPreview(body, frontier, overlap)
            : ImageStitcher.CreateJoinPreview(frontier, body, overlap);
    }

    private void ConfirmManualJoin()
    {
        if (_busy || _finishing || _pickingBoundary || _unmatchedRaw is null || _frames.Count == 0) return;
        var atTop = _direction.SelectedIndex == 0;
        var overlap = (int)Math.Clamp(Math.Round(_overlapSlider.Value), 0, BodyHeight - 1);
        var advance = BodyHeight - overlap;
        var position = atTop ? checked(_positions[0] - advance) : checked(_positions[^1] + advance);
        var raw = _unmatchedRaw;
        if (!TryAcceptFrame(raw, position, atTop)) return;
        _manualPanel.IsVisible = false;
        ResetObservation();
        SetStatus("接縫已加入；按「繼續」再捲動。");
        UpdateButtons();
    }

    private void ResetObservation(bool discardPending = true)
    {
        _lastProcessedRaw = null;
        if (discardPending) _pendingAlignedRaw = null;
    }

    private void LimitWindowHeight()
    {
        if (_handle == 0 || _closed) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        var maximum = Math.Max(120, screen.WorkingArea.Height / scale - 24);
        if (Math.Abs(MaxHeight - maximum) > 0.5) MaxHeight = maximum;
    }

    private void RefreshPreview()
    {
        RefreshCollectedPreview();
    }

    private void UpdateDimensions()
    {
        if (_frames.Count == 0)
        {
            _dimensions.Text = I18n.F("範圍 {0:N0} × {1:N0} px", FrameWidth, BodyHeight);
            return;
        }
        var height = (long)(_rangeEnd ?? checked(_positions[^1] + FrameHeight - _bottomTrim))
            - (_rangeStart ?? checked(_positions[0] + _topTrim));
        _dimensions.Text = I18n.F("{0} 張 · {1:N0} × {2:N0} px", _frames.Count, FrameWidth, height);
    }

    private void UpdateButtons()
    {
        _pauseButton.Content = new TextBlock { Text = I18n.T(_userPaused ? "繼續" : "暫停") };
        ToolTip.SetTip(_pauseButton, _rangeEnd is not null ? I18n.T("繼續收集會延伸終點。") : null);
        _pauseButton.IsEnabled = !_finishing && !_pickingBoundary && !NeedsStart && !_manualPanel.IsVisible
            && !_startingLiveCapture;
        bool canEdit = !_busy && !_finishing && !_pickingBoundary;
        // Finishing waits for an in-flight matcher; a continuous observer must not make Done unclickable.
        _finishButton.IsEnabled = !_finishing && !_pickingBoundary && _frames.Count > 0
            && HasFinishRange && !_manualPanel.IsVisible;
        _undoButton.IsEnabled = canEdit && _frames.Count > 1 && !_manualPanel.IsVisible;
        _manualButton.IsEnabled = canEdit && _unmatchedRaw is not null && !_manualPanel.IsVisible;
        _manualButton.IsVisible = _unmatchedRaw is not null;
        _manualPanel.IsEnabled = canEdit;
        _finishNotice.IsEnabled = canEdit;
        _confirmJoinButton.IsEnabled = canEdit;
        _applyTrimButton.IsEnabled = canEdit && !_manualPanel.IsVisible;
        _topTrimBox.IsEnabled = _bottomTrimBox.IsEnabled = _applyTrimButton.IsEnabled;
        UpdateRangeControls();
        UpdateCompactControls();
    }

    private void SetStatus(string text, bool warning = false)
    {
        _status.Text = I18n.T(text);
        _status.Foreground = Brush(warning ? "Warning" : "Ink");
        _compactStatus.Text = _status.Text;
        _compactStatus.Foreground = _status.Foreground;
        ToolTip.SetTip(_compactStatus, _status.Text);
    }

    private static TextBox MakeTrimBox() => new()
    {
        Text = "0", Width = 56, MinHeight = 0, Margin = new Thickness(7, 0, 10, 0), Padding = new Thickness(6),
        Background = Brush("Bg"), Foreground = Brush("Ink"),
        BorderBrush = Brush("Line"), CaretBrush = Brush("Ink"), VerticalAlignment = VerticalAlignment.Center
    };

    private static Button MakeButton(string label, bool accent = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = I18n.T(label) }, Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 7, 0), FontSize = 12, MinHeight = 0,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand), FontWeight = FontWeight.SemiBold
        };
        if (accent) button.WithTheme("Primary");
        else
        {
            button.Background = Brush("Raised");
            button.Foreground = Brush("Ink");
        }
        return button;
    }

    private static IBrush Brush(string key) => ThemeService.Brush(key);

    private enum FrameKind { Known, New, Unreliable }
    private sealed record FrameAnalysis(FrameKind Kind, int Position = 0, bool AtTop = false, bool FixedEdge = false);
}

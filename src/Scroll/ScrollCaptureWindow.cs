using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SnipFlow.Capture;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Scroll;

/// <summary>Observes user scrolling without sending input or taking focus from the target.</summary>
public sealed class ScrollCaptureWindow : Window
{
    private readonly DrawingRectangle _targetBounds;
    private readonly ScrollCaptureTarget _target;
    // Frames stay in document order. Positions describe the top of each cropped body.
    private readonly List<BitmapSource> _frames = new();
    private readonly List<int> _positions = new();
    private readonly Stack<bool> _addedAtTop = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer;
    private readonly System.Windows.Controls.Image _preview;
    private readonly System.Windows.Controls.Image _joinPreview;
    private readonly TextBlock _status;
    private readonly TextBlock _dimensions;
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
    private BitmapSource? _anchorRaw;
    private int _anchorPosition;
    private BitmapSource? _candidateRaw;
    private BitmapSource? _lastProcessedRaw;
    private BitmapSource? _unmatchedRaw;
    private long _candidateSince;
    private int _topTrim;
    private int _bottomTrim;
    private bool _userPaused;
    private bool _busy;
    private bool _finishing;
    private bool _closed;
    private bool _toolbarExcluded;
    private IntPtr _handle;

    public BitmapSource? Result { get; private set; }

    public ScrollCaptureWindow(DrawingRectangle targetPhysicalBounds, BitmapSource? initialFrame = null)
    {
        if (targetPhysicalBounds.Width < 32 || targetPhysicalBounds.Height < 48)
            throw new ArgumentException(I18n.T("請選取至少 32 × 48 像素的捲動內容範圍。"), nameof(targetPhysicalBounds));
        if ((long)targetPhysicalBounds.Width * targetPhysicalBounds.Height > ImageStitcher.MaxOutputPixels)
            throw new ArgumentException(I18n.T("選取範圍太大，請縮小範圍後重試。"), nameof(targetPhysicalBounds));
        if (initialFrame is not null && (initialFrame.PixelWidth != targetPhysicalBounds.Width
            || initialFrame.PixelHeight != targetPhysicalBounds.Height))
            throw new ArgumentException("The initial frame must match the selected region.", nameof(initialFrame));

        _targetBounds = targetPhysicalBounds;
        _target = new ScrollCaptureTarget(_targetBounds);
        Title = I18n.T("SnipFlow · 捲動截圖");
        Width = 430;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("#202020");
        ShowActivated = false;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI");
        FontSize = 12;
        Foreground = Brush("#ECECEC");

        var shell = new Border
        {
            BorderBrush = Brush("#383838"), BorderThickness = new Thickness(1),
            Background = Brush("#202020"), Padding = new Thickness(14)
        };
        var layout = new StackPanel();
        shell.Child = layout;
        Content = new ScrollViewer
        {
            Content = shell, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false
        };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = I18n.T("捲動截圖"), FontWeight = FontWeights.SemiBold, FontSize = 15 };
        header.Children.Add(title);
        title.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        var cancel = MakeButton("取消");
        cancel.Padding = new Thickness(9, 4, 9, 4);
        cancel.Margin = new Thickness(0);
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 1);
        header.Children.Add(cancel);
        layout.Children.Add(header);
        layout.Children.Add(new TextBlock
        {
            Text = I18n.T("在目標視窗上下捲動；停下時自動擷取。"),
            Foreground = Brush("#A3A3A3"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var previewRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        previewRow.ColumnDefinitions.Add(new ColumnDefinition());
        _preview = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        previewRow.Children.Add(new Border
        {
            Height = 58, Background = Brush("#181818"), BorderBrush = Brush("#383838"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            ClipToBounds = true, Child = _preview
        });
        var information = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(information, 1);
        _dimensions = new TextBlock { Foreground = Brush("#A3A3A3"), FontSize = 11 };
        _status = new TextBlock
        {
            Text = I18n.T("切回目標視窗即可開始。"), TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#ECECEC"), LineHeight = 17, Margin = new Thickness(0, 5, 0, 0)
        };
        information.Children.Add(_dimensions);
        information.Children.Add(_status);
        previewRow.Children.Add(information);
        layout.Children.Add(previewRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
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
        actions.Children.Add(_undoButton);
        actions.Children.Add(_manualButton);
        layout.Children.Add(actions);

        _manualPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 11, 0, 0) };
        _manualPanel.Children.Add(new TextBlock
        {
            Text = I18n.T("只接到已擷取內容的最上端或最下端。請確認接縫。"),
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#EAC88D")
        });
        _joinPreview = new System.Windows.Controls.Image { Stretch = Stretch.None, SnapsToDevicePixels = true };
        _manualPanel.Children.Add(new ScrollViewer
        {
            Content = _joinPreview, Height = 160, Margin = new Thickness(0, 7, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brush("#181818")
        });
        _direction = new ComboBox
        {
            Background = Brush("#2B2B2B"), Foreground = Brush("#ECECEC"),
            Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(7, 4, 7, 4),
            ItemsSource = new[] { I18n.T("向上補較早內容"), I18n.T("向下加入新內容") }, SelectedIndex = 1
        };
        _direction.SelectionChanged += (_, _) => UpdateJoinPreview();
        _manualPanel.Children.Add(_direction);
        _overlapLabel = new TextBlock { Margin = new Thickness(0, 7, 0, 0), Foreground = Brush("#A3A3A3") };
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
            _manualPanel.Visibility = Visibility.Collapsed;
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
            Header = I18n.T("略過固定頂部列／底部輸入框"), Foreground = Brush("#A3A3A3"),
            Margin = new Thickness(0, 10, 0, 0)
        };
        var trimContent = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        trimContent.Children.Add(new TextBlock
        {
            Text = I18n.T("手動設定要略過的像素；至少保留 48 px。"),
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A3A3A3"), FontSize = 11
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
        layout.Children.Add(_trimExpander);

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(275) };
        _timer.Tick += async (_, _) => await ObserveAsync();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            _toolbarExcluded = ScrollCaptureTarget.ExcludeToolbar(_handle);
            WindowAppearance.Apply(this);
        };
        Loaded += (_, _) =>
        {
            ScrollCaptureTarget.PlaceToolbar(_handle, _targetBounds);
            LimitWindowHeight();
            if (initialFrame is not null)
            {
                var first = initialFrame.IsFrozen ? initialFrame : initialFrame.CloneCurrentValue();
                if (!first.IsFrozen) first.Freeze();
                AddFirstFrame(first);
            }
            _timer.Start();
        };
        LocationChanged += (_, _) => LimitWindowHeight();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _lifetime.Cancel();
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
        };
        UpdateButtons();
    }

    private async Task ObserveAsync()
    {
        if (_closed || _finishing || _busy || _userPaused || _manualPanel.Visibility == Visibility.Visible) return;
        if (!_target.CanObserve(_handle, _toolbarExcluded, out var reason))
        {
            ResetObservation();
            SetStatus(reason, warning: true);
            return;
        }
        _busy = true;
        UpdateButtons();
        try
        {
            // The excluded toolbar stays visible. No opacity changes or Activate calls.
            var raw = ScreenshotService.CaptureRectangle(_targetBounds);
            if (!CanCommitObservation()) return;
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
                    _candidateRaw = null;
                    _candidateSince = 0;
                    return;
                }
            }
            if (_candidateRaw is null)
            {
                _candidateRaw = raw;
                _unmatchedRaw = raw;
                _candidateSince = Stopwatch.GetTimestamp();
                SetStatus("等待捲動停止…");
                return;
            }
            var candidate = _candidateRaw;
            var stable = await Task.Run(() => ImageStitcher.AreSameImages(Crop(candidate), Crop(raw)), _lifetime.Token);
            if (!CanCommitObservation()) return;
            if (!stable)
            {
                _candidateRaw = raw;
                _unmatchedRaw = raw;
                _candidateSince = Stopwatch.GetTimestamp();
                SetStatus("等待捲動停止…");
                return;
            }
            if (Stopwatch.GetElapsedTime(_candidateSince).TotalMilliseconds < 550) return;

            var bodies = _frames.Select(Crop).ToArray();
            var positions = _positions.ToArray();
            var anchor = Crop(_anchorRaw ?? _frames[^1]);
            var anchorPosition = _anchorPosition;
            var body = Crop(raw);
            var analysis = await Task.Run(() => AnalyzeFrame(anchor, anchorPosition, body, bodies, positions, _lifetime.Token), _lifetime.Token);
            if (!CanCommitObservation()) return;
            _lastProcessedRaw = raw;
            _candidateRaw = null;
            if (analysis.Kind == FrameKind.Known)
            {
                _anchorRaw = raw;
                _anchorPosition = analysis.Position;
                _unmatchedRaw = null;
                SetStatus("這段已擷取；捲過最上端或最下端即可延伸。");
            }
            else if (analysis.Kind == FrameKind.New)
            {
                TryAcceptFrame(raw, analysis.Position, analysis.AtTop);
            }
            else
            {
                _unmatchedRaw = raw;
                SetStatus(analysis.FixedEdge
                    ? "固定列可能影響對齊，請展開裁切設定；此畫面尚未加入。"
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
        if (_closed || _finishing || _userPaused || _manualPanel.Visibility == Visibility.Visible) return false;
        if (_target.CanObserve(_handle, _toolbarExcluded, out var reason)) return true;
        ResetObservation();
        SetStatus(reason, warning: true);
        return false;
    }

    private static FrameAnalysis AnalyzeFrame(BitmapSource anchor, int anchorPosition, BitmapSource current,
        IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> positions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ImageStitcher.AreSameImages(anchor, current)) return new FrameAnalysis(FrameKind.Known, anchorPosition);
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

    private void AddFirstFrame(BitmapSource frame)
    {
        _frames.Add(frame);
        _positions.Add(0);
        _anchorRaw = frame;
        _anchorPosition = 0;
        _lastProcessedRaw = frame;
        RefreshPreview();
        UpdateDimensions();
        SetStatus("已擷取首張；切回目標視窗上下捲動。");
    }

    private bool TryAcceptFrame(BitmapSource raw, int position, bool atTop)
    {
        if (_frames.Count >= ImageStitcher.MaxFrames)
        {
            PauseAtLimit("已達 20 張上限，請按「完成」並分段截取。");
            return false;
        }
        var storedPixels = (long)(_frames.Count + 1) * _targetBounds.Width * _targetBounds.Height;
        if (storedPixels > ImageStitcher.MaxStoredPixels)
        {
            PauseAtLimit("已達畫面記憶體上限，請按「完成」並分段截取。");
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
        if ((last - (long)first + BodyHeight) * _targetBounds.Width > ImageStitcher.MaxOutputPixels)
        {
            PauseAtLimit("已達長截圖像素上限，請按「完成」並分段截取。");
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
        RefreshPreview();
        UpdateDimensions();
        SetStatus(atTop ? "已補上較早內容，繼續向上捲動。" : "已加入新內容，繼續向下捲動。");
        return true;
    }

    private int BodyHeight => _targetBounds.Height - _topTrim - _bottomTrim;

    private BitmapSource Crop(BitmapSource image) => Crop(image, _topTrim, _bottomTrim);

    private static BitmapSource Crop(BitmapSource image, int top, int bottom)
    {
        if (top == 0 && bottom == 0) return image;
        var cropped = new CroppedBitmap(image, new Int32Rect(0, top, image.PixelWidth, image.PixelHeight - top - bottom));
        cropped.Freeze();
        return cropped;
    }

    private int[] GetOverlaps(int height)
    {
        var overlaps = new int[Math.Max(0, _positions.Count - 1)];
        for (var index = 1; index < _positions.Count; index++)
            overlaps[index - 1] = checked(height - (_positions[index] - _positions[index - 1]));
        return overlaps;
    }

    private void TogglePause()
    {
        if (_closed || _finishing || _manualPanel.Visibility == Visibility.Visible) return;
        _userPaused = !_userPaused;
        ResetObservation();
        if (!_userPaused) _timer.Start();
        SetStatus(_userPaused ? "已暫停，擷取內容會保留。" : "切回目標視窗繼續捲動。");
        UpdateButtons();
    }

    private void PauseAtLimit(string message)
    {
        _userPaused = true;
        ResetObservation();
        SetStatus(message, warning: true);
        UpdateButtons();
    }

    private void UndoFrame()
    {
        if (_busy || _finishing || _frames.Count <= 1 || _addedAtTop.Count == 0) return;
        var index = _addedAtTop.Pop() ? 0 : _frames.Count - 1;
        _frames.RemoveAt(index);
        _positions.RemoveAt(index);
        _anchorRaw = _frames[^1];
        _anchorPosition = _positions[^1];
        _userPaused = true;
        _unmatchedRaw = null;
        _manualPanel.Visibility = Visibility.Collapsed;
        ResetObservation();
        RefreshPreview();
        UpdateDimensions();
        UpdateButtons();
        SetStatus("已移除上一張並暫停；按「繼續」再捲動。");
    }

    private void ApplyTrim()
    {
        if (_busy || _finishing || _manualPanel.Visibility == Visibility.Visible) return;
        if (!int.TryParse(_topTrimBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var top)
            || !int.TryParse(_bottomTrimBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var bottom)
            || top < 0 || bottom < 0 || (long)top + bottom > _targetBounds.Height - 48)
        {
            SetStatus("裁切值須為非負整數，並至少保留 48 px。", warning: true);
            return;
        }
        var height = _targetBounds.Height - top - bottom;
        if (_frames.Count > 0 && ((long)_positions[^1] - _positions[0] + height)
            * _targetBounds.Width > ImageStitcher.MaxOutputPixels)
        {
            PauseAtLimit("已達長截圖像素上限，請按「完成」並分段截取。");
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
        _unmatchedRaw = null;
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
        if (_busy || _finishing || _unmatchedRaw is null || _frames.Count == 0) return;
        _userPaused = true;
        ResetObservation();
        _manualPanel.Visibility = Visibility.Visible;
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
        if (_busy || _finishing || _unmatchedRaw is null || _frames.Count == 0) return;
        var atTop = _direction.SelectedIndex == 0;
        var overlap = (int)Math.Clamp(Math.Round(_overlapSlider.Value), 0, BodyHeight - 1);
        var advance = BodyHeight - overlap;
        var position = atTop ? checked(_positions[0] - advance) : checked(_positions[^1] + advance);
        var raw = _unmatchedRaw;
        if (!TryAcceptFrame(raw, position, atTop)) return;
        _manualPanel.Visibility = Visibility.Collapsed;
        ResetObservation();
        SetStatus("接縫已加入；按「繼續」再捲動。");
        UpdateButtons();
    }

    private async Task FinishAsync()
    {
        if (_busy || _finishing || _closed || _frames.Count == 0) return;
        _finishing = true;
        _timer.Stop();
        _busy = true;
        UpdateButtons();
        SetStatus("正在產生長截圖…");
        try
        {
            var frames = _frames.Select(Crop).ToArray();
            var overlaps = GetOverlaps(BodyHeight);
            var result = await Task.Run(() => ImageStitcher.Stitch(frames, overlaps));
            if (_closed) return;
            Result = result;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            SettingsStore.Log(exception);
            if (!_closed)
            {
                _finishing = false;
                _userPaused = true;
                SetStatus(I18n.F("產生長截圖失敗，請重新截取：{0}", exception.Message), warning: true);
            }
        }
        finally
        {
            _busy = false;
            if (!_closed) UpdateButtons();
        }
    }

    private void ResetObservation()
    {
        _candidateRaw = null;
        _lastProcessedRaw = null;
        _candidateSince = 0;
    }

    private void LimitWindowHeight()
    {
        if (_handle == IntPtr.Zero || _closed) return;
        var screen = System.Windows.Forms.Screen.FromHandle(_handle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var maximum = Math.Max(120, screen.Height / scale - 24);
        if (Math.Abs(MaxHeight - maximum) > 0.5) MaxHeight = maximum;
    }

    private void RefreshPreview()
    {
        if (_frames.Count == 0) { _preview.Source = null; return; }
        _preview.Source = Crop(_anchorRaw ?? _frames[^1]);
    }

    private void UpdateDimensions()
    {
        if (_frames.Count == 0)
        {
            _dimensions.Text = I18n.F("範圍 {0:N0} × {1:N0} px", _targetBounds.Width, BodyHeight);
            return;
        }
        var height = (long)_positions[^1] - _positions[0] + BodyHeight;
        _dimensions.Text = I18n.F("{0} 張 · {1:N0} × {2:N0} px", _frames.Count, _targetBounds.Width, height);
    }

    private void UpdateButtons()
    {
        _pauseButton.Content = new TextBlock { Text = I18n.T(_userPaused ? "繼續" : "暫停") };
        _pauseButton.IsEnabled = !_finishing && _manualPanel.Visibility != Visibility.Visible;
        _finishButton.IsEnabled = !_busy && !_finishing && _frames.Count > 0;
        _undoButton.IsEnabled = !_busy && !_finishing && _frames.Count > 1 && _manualPanel.Visibility != Visibility.Visible;
        _manualButton.IsEnabled = !_busy && !_finishing && _unmatchedRaw is not null && _manualPanel.Visibility != Visibility.Visible;
        _confirmJoinButton.IsEnabled = !_busy && !_finishing;
        _applyTrimButton.IsEnabled = !_busy && !_finishing && _manualPanel.Visibility != Visibility.Visible;
        _topTrimBox.IsEnabled = _bottomTrimBox.IsEnabled = _applyTrimButton.IsEnabled;
    }

    private void SetStatus(string text, bool warning = false)
    {
        _status.Text = I18n.T(text);
        _status.Foreground = Brush(warning ? "#EAC88D" : "#ECECEC");
    }

    private static TextBox MakeTrimBox() => new()
    {
        Text = "0", Width = 48, Margin = new Thickness(7, 0, 10, 0), Padding = new Thickness(6),
        Background = Brush("#181818"), Foreground = Brush("#ECECEC"),
        BorderBrush = Brush("#484848"), CaretBrush = Brush("#ECECEC")
    };

    private static Button MakeButton(string label, bool accent = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = I18n.T(label) }, Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 7, 0), FontSize = 12,
            Background = Brush(accent ? "#E8E8E8" : "#2B2B2B"),
            Foreground = Brush(accent ? "#191919" : "#ECECEC"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Button.Background))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Button.Padding))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.55));
        template.Triggers.Add(disabled);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.85));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private enum FrameKind { Known, New, Unreliable }
    private sealed record FrameAnalysis(FrameKind Kind, int Position = 0, bool AtTop = false, bool FixedEdge = false);
}

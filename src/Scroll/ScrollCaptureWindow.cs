using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using SnipFlow.Capture;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Scroll;

/// <summary>A manual scroll workflow: users scroll their page, then capture the next frame.</summary>
public sealed class ScrollCaptureWindow : Window
{
    private readonly DrawingRectangle _targetBounds;
    private readonly List<BitmapSource> _frames = new();
    private readonly List<int> _overlaps = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Controls.Image _preview;
    private readonly TextBlock _status;
    private readonly TextBlock _dimensions;
    private readonly Button _addButton;
    private readonly Button _undoButton;
    private readonly Button _finishButton;
    private readonly StackPanel _manualPanel;
    private readonly Slider _overlapSlider;
    private readonly TextBlock _overlapLabel;
    private readonly Button _confirmJoinButton;
    private BitmapSource? _pendingFrame;
    private bool _busy;
    private bool _closed;

    public BitmapSource? Result { get; private set; }

    public ScrollCaptureWindow(DrawingRectangle targetPhysicalBounds)
    {
        if (targetPhysicalBounds.Width < 32 || targetPhysicalBounds.Height < 48)
            throw new ArgumentException(I18n.T("請選取至少 32 × 48 像素的捲動內容範圍。"), nameof(targetPhysicalBounds));
        if ((long)targetPhysicalBounds.Width * targetPhysicalBounds.Height > ImageStitcher.MaxOutputPixels)
            throw new ArgumentException(I18n.T("選取範圍太大，請縮小範圍後重試。"), nameof(targetPhysicalBounds));

        _targetBounds = targetPhysicalBounds;
        Title = I18n.T("SnipFlow · 手動長截圖");
        Width = 510;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI");
        FontSize = 13;
        Foreground = Brush("#ECECEC");

        var shell = new Border
        {
            Background = Brush("#202020"),
            BorderBrush = Brush("#383838"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 5, Opacity = 0.35 }
        };
        var layout = new StackPanel();
        shell.Child = layout;
        Content = shell;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = I18n.T("手動長截圖"), FontWeight = FontWeights.SemiBold, FontSize = 22 };
        header.Children.Add(title);
        header.MouseLeftButtonDown += (_, args) => { if (args.ButtonState == MouseButtonState.Pressed) DragMove(); };
        layout.Children.Add(header);
        layout.Children.Add(new TextBlock
        {
            Text = I18n.T("回到目標頁面向下捲動約半頁，再按「加入畫面」。保留重疊內容，並避開固定標頭、頁尾及動畫。"),
            Foreground = Brush("#A3A3A3"), TextWrapping = TextWrapping.Wrap,
            LineHeight = 20, Margin = new Thickness(0, 0, 0, 14)
        });

        _preview = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        layout.Children.Add(new Border
        {
            Height = 140, Background = Brush("#181818"), BorderBrush = Brush("#383838"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Child = _preview
        });
        _dimensions = new TextBlock
        {
            Text = I18n.F("範圍 {0:N0} × {1:N0} px", _targetBounds.Width, _targetBounds.Height),
            Foreground = Brush("#A3A3A3"), FontSize = 11, Margin = new Thickness(0, 8, 0, 8)
        };
        layout.Children.Add(_dimensions);
        _status = new TextBlock
        {
            Text = I18n.T("正在取得第一個畫面…"), Foreground = Brush("#ECECEC"),
            TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 0, 0, 12)
        };
        layout.Children.Add(_status);

        _manualPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 14) };
        _manualPanel.Children.Add(new TextBlock
        {
            Text = I18n.T("檢查上方接縫預覽，調整下一張要略過的頂部高度："),
            Foreground = Brush("#EAC88D"), TextWrapping = TextWrapping.Wrap
        });
        _overlapLabel = new TextBlock { Margin = new Thickness(0, 7, 0, 0), Foreground = Brush("#A3A3A3") };
        _manualPanel.Children.Add(_overlapLabel);
        _overlapSlider = new Slider { Minimum = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 8) };
        _overlapSlider.ValueChanged += (_, _) => UpdateJoinPreview();
        _manualPanel.Children.Add(_overlapSlider);
        var joinActions = new StackPanel { Orientation = Orientation.Horizontal };
        _confirmJoinButton = MakeButton("確認接縫", accent: true);
        _confirmJoinButton.Click += (_, _) => ConfirmPendingFrame();
        var discard = MakeButton("捨棄此畫面");
        discard.Click += (_, _) => DiscardPendingFrame();
        joinActions.Children.Add(_confirmJoinButton);
        joinActions.Children.Add(discard);
        _manualPanel.Children.Add(joinActions);
        layout.Children.Add(_manualPanel);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _addButton = MakeButton("加入畫面", accent: true);
        _addButton.Click += async (_, _) => await AddFrameAsync();
        _undoButton = MakeButton("上一步");
        _undoButton.Click += (_, _) => UndoFrame();
        _finishButton = MakeButton("完成");
        _finishButton.Click += async (_, _) => await FinishAsync();
        var cancel = MakeButton("取消");
        cancel.Click += (_, _) => Close();
        actions.Children.Add(_addButton);
        actions.Children.Add(_undoButton);
        actions.Children.Add(_finishButton);
        actions.Children.Add(cancel);
        layout.Children.Add(actions);

        SourceInitialized += (_, _) => ExcludeToolbarFromCapture();
        Loaded += async (_, _) => await AddFrameAsync();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        UpdateButtons();
    }

    private async Task AddFrameAsync()
    {
        if (_busy || _pendingFrame is not null || _closed)
            return;
        if (_frames.Count >= ImageStitcher.MaxFrames)
        {
            SetStatus("已達 20 張上限，請按「完成」並分段截取。", warning: true);
            return;
        }

        SetBusy(true);
        try
        {
            SetStatus("正在取得畫面…");
            var frame = await CaptureFrameAsync();
            if (_closed)
                return;
            var storedPixels = _frames.Sum(image => (long)image.PixelWidth * image.PixelHeight);
            if (storedPixels + (long)frame.PixelWidth * frame.PixelHeight > ImageStitcher.MaxStoredPixels)
            {
                SetStatus("已達畫面記憶體上限，請按「完成」並分段截取。", warning: true);
                return;
            }
            if (_frames.Count == 0)
            {
                _frames.Add(frame);
                _preview.Source = frame;
                SetStatus("第一張已加入。現在可向下捲動目標頁面。 ");
                UpdateDimensions();
                return;
            }

            SetStatus("正在比對重疊內容…");
            var previous = _frames[^1];
            var match = await Task.Run(() => ImageStitcher.FindOverlap(previous, frame), _lifetime.Token);
            if (_closed)
                return;
            if (match.IsDuplicate)
            {
                SetStatus("畫面沒有變化，未加入重複頁面。請繼續捲動，或按「完成」。", warning: true);
                return;
            }
            if (match.IsReliable)
            {
                if (TryAcceptFrame(frame, match.OverlapPixels))
                {
                    SetStatus(I18n.F("已加入第 {0} 張 · 自動對齊 {1:N0} px（信心 {2:P0}）。", _frames.Count, match.OverlapPixels, match.Confidence));
                    return;
                }
            }

            _pendingFrame = frame;
            _overlapSlider.Maximum = Math.Min(previous.PixelHeight, frame.PixelHeight) - 1;
            _overlapSlider.Value = Math.Clamp(match.OverlapPixels, 0, (int)_overlapSlider.Maximum);
            _manualPanel.Visibility = Visibility.Visible;
            UpdateJoinPreview();
            SetStatus(match.IsReliable
                ? "已達輸出大小上限。可增加重疊高度，或捨棄此畫面並完成目前長截圖。"
                : "無法可靠對齊。請確認接縫後加入，或捨棄此畫面並減少捲動距離。", warning: true);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            if (!_closed)
                SetStatus(I18n.F("擷取失敗：{0}", exception.Message), warning: true);
        }
        finally
        {
            if (!_closed)
                SetBusy(false);
        }
    }

    private async Task<BitmapSource> CaptureFrameAsync()
    {
        // Hide through opacity so the owner's ShowDialog dispatcher keeps running.
        Opacity = 0;
        IsHitTestVisible = false;
        try
        {
            await Task.Delay(180, _lifetime.Token);
            var frame = ScreenshotService.CaptureRectangle(_targetBounds);
            if (!frame.IsFrozen)
                frame.Freeze();
            return frame;
        }
        finally
        {
            if (!_closed)
            {
                Opacity = 1;
                IsHitTestVisible = true;
                Activate();
            }
        }
    }

    private bool TryAcceptFrame(BitmapSource frame, int overlap)
    {
        var candidateFrames = _frames.Append(frame).ToArray();
        var candidateOverlaps = _overlaps.Append(overlap).ToArray();
        if (ImageStitcher.CalculateOutputPixels(candidateFrames, candidateOverlaps) > ImageStitcher.MaxOutputPixels)
        {
            SetStatus("加入後將超過 40 百萬像素，請增加重疊高度，或完成目前的長截圖。", warning: true);
            return false;
        }
        _preview.Source = ImageStitcher.CreateJoinPreview(_frames[^1], frame, overlap);
        _frames.Add(frame);
        _overlaps.Add(overlap);
        UpdateDimensions();
        return true;
    }

    private void ConfirmPendingFrame()
    {
        if (_pendingFrame is null || _busy)
            return;
        if (!TryAcceptFrame(_pendingFrame, (int)_overlapSlider.Value))
            return;
        _pendingFrame = null;
        _manualPanel.Visibility = Visibility.Collapsed;
        SetStatus(I18n.F("已加入第 {0} 張 · 使用手動確認的接縫。", _frames.Count));
        UpdateButtons();
    }

    private void DiscardPendingFrame()
    {
        _pendingFrame = null;
        _manualPanel.Visibility = Visibility.Collapsed;
        _preview.Source = _frames.Count > 0 ? _frames[^1] : null;
        SetStatus("已捨棄未對齊的畫面。請少捲動一些，保留較多重疊內容。");
        UpdateButtons();
    }

    private void UndoFrame()
    {
        if (_busy || _pendingFrame is not null || _frames.Count < 2)
            return;
        _frames.RemoveAt(_frames.Count - 1);
        _overlaps.RemoveAt(_overlaps.Count - 1);
        _preview.Source = _frames[^1];
        SetStatus("已移除最後一張。請回到目標頁面重新調整捲動位置。");
        UpdateDimensions();
        UpdateButtons();
    }

    private void UpdateJoinPreview()
    {
        if (_pendingFrame is null || _frames.Count == 0)
            return;
        var overlap = Math.Clamp((int)_overlapSlider.Value, 0, _pendingFrame.PixelHeight - 1);
        _overlapLabel.Text = I18n.F("重疊 {0:N0} px · 新增 {1:N0} px", overlap, _pendingFrame.PixelHeight - overlap);
        _preview.Source = ImageStitcher.CreateJoinPreview(_frames[^1], _pendingFrame, overlap);
    }

    private async Task FinishAsync()
    {
        if (_busy || _pendingFrame is not null || _frames.Count == 0)
            return;
        SetBusy(true);
        try
        {
            SetStatus("正在合成長截圖…");
            var frames = _frames.ToArray();
            var overlaps = _overlaps.ToArray();
            var result = await Task.Run(() => ImageStitcher.Stitch(frames, overlaps), _lifetime.Token);
            if (_closed)
                return;
            Result = result;
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            if (!_closed)
                SetStatus(I18n.F("合成失敗：{0}", exception.Message), warning: true);
        }
        finally
        {
            if (!_closed)
                SetBusy(false);
        }
    }

    private void UpdateDimensions()
    {
        if (_frames.Count == 0)
            return;
        var pixels = ImageStitcher.CalculateOutputPixels(_frames, _overlaps);
        _dimensions.Text = I18n.F("{0} 張 · {1:N0} × {2:N0} px · 接縫預覽", _frames.Count, _frames[0].PixelWidth, pixels / _frames[0].PixelWidth);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _addButton.IsEnabled = !_busy && _pendingFrame is null && _frames.Count < ImageStitcher.MaxFrames;
        _undoButton.IsEnabled = !_busy && _pendingFrame is null && _frames.Count > 1;
        _finishButton.IsEnabled = !_busy && _pendingFrame is null && _frames.Count > 0;
        _confirmJoinButton.IsEnabled = !_busy;
    }

    private void SetStatus(string text, bool warning = false)
    {
        _status.Text = I18n.T(text);
        _status.Foreground = Brush(warning ? "#EAC88D" : "#ECECEC");
    }

    private void ExcludeToolbarFromCapture()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = SetWindowDisplayAffinity(handle, 0x11); // WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+.
        var screen = System.Windows.Forms.Screen.FromRectangle(_targetBounds).WorkingArea;
        var dpi = GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var physicalWidth = (int)Math.Ceiling(Width * scale);
        _ = SetWindowPos(handle, IntPtr.Zero, Math.Max(screen.Left, screen.Right - physicalWidth - 16), screen.Top + 16,
            0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    private static Button MakeButton(string label, bool accent = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = I18n.T(label), Foreground = Brush(accent ? "#191919" : "#ECECEC") }, Padding = new Thickness(13, 9, 13, 9),
            Margin = new Thickness(0, 0, 7, 0), FontSize = 12,
            Background = Brush(accent ? "#E8E8E8" : "#2B2B2B"),
            Foreground = Brush(accent ? "#191919" : "#ECECEC"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            FontWeight = FontWeights.SemiBold
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Button.Background)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Button.Padding)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.7));
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

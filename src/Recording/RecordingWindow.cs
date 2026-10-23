using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Styling;

namespace SnipFlow.Recording;

/// <summary>A small recording toolbar. Opening it does not start video or audio capture.</summary>
/// <remarks>
/// Windows excludes the toolbar and the region border from capture (<c>RecordingWindow.Windows.cs</c>). Linux has
/// no capture exclusion, so the toolbar and the border are placed outside the recorded rectangle
/// (<c>RecordingWindow.Linux.cs</c>).
/// </remarks>
public sealed partial class RecordingWindow : DialogWindow
{
    private static readonly ControlTheme ButtonTheme = CreateButtonTheme();
    private readonly Func<string, string> _translate;
    private readonly ScreenRecordingService _service;
    private readonly RecordingRegionHighlight _highlight;
    private readonly PixelRect _bounds;
    private readonly string? _suggestedPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _stateLabel;
    private readonly TextBlock _clock;
    private readonly TextBlock _status;
    private readonly TextBlock _pathLabel;
    private readonly CheckBox _systemAudio;
    private readonly CheckBox _microphone;
    private readonly CheckBox _cursor;
    private readonly Button _recordButton;
    private readonly Button _cancelButton;
    private bool _busy;
    private bool _loaded;
    private bool _recordingStarted;
    private bool _allowClose;
    private bool _closingRequested;
    private bool _excludedFromCapture;
    private bool _savePending;
    private bool _rangeInvalidated;
    private RecordingDraft? _draft;
    private Task<string>? _exportTask;
    private DateTime _startedAt;

    public string? ResultPath { get; private set; }
    public PixelRect CaptureBounds => _bounds;

    public RecordingWindow(PixelRect targetPhysicalBounds, Func<string, string>? translate = null)
        : this(targetPhysicalBounds, null, translate)
    {
    }

    public RecordingWindow(PixelRect targetPhysicalBounds, string? outputPath, Func<string, string>? translate = null)
    {
        _translate = translate ?? (text => text);
        _bounds = ScreenRecordingService.NormalizeBounds(targetPhysicalBounds, _translate);
        _suggestedPath = outputPath;
        _service = new ScreenRecordingService(_translate);
        _highlight = new RecordingRegionHighlight(_bounds);
        Title = "SnipFlow · " + T("螢幕錄影");
        Width = 444;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        Background = ColorBrush("Panel");
        Foreground = ColorBrush("Ink");
        FontFamily = UiThemes.AppFont;
        FontSize = 13;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var shell = new Border
        {
            Background = ColorBrush("Panel"), BorderBrush = ColorBrush("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(20)
        };
        var layout = new StackPanel();
        shell.Child = layout;
        Content = shell;

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 12), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = new TextBlock
        {
            Text = T("螢幕錄影"), FontSize = 20, FontWeight = FontWeight.SemiBold, Background = Brushes.Transparent
        };
        WindowChrome.EnableTitleBarDrag(this, title, allowMaximize: false);
        title.AddHandler(PointerPressedEvent, (_, _) => OnUserMove(), RoutingStrategies.Bubble, handledEventsToo: true);
        heading.Children.Add(title);
        _clock = new TextBlock
        {
            Text = "00:00:00", FontFamily = UiThemes.MonoFont, FontSize = 24,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_clock, 1);
        heading.Children.Add(_clock);
        layout.Children.Add(heading);

        _stateLabel = new TextBlock { Text = T("準備錄影"), Foreground = ColorBrush("Ink"), Margin = new Thickness(0, 0, 0, 5) };
        layout.Children.Add(_stateLabel);
        layout.Children.Add(new TextBlock
        {
            Text = $"{_bounds.Width:N0} × {_bounds.Height:N0} px · 30 fps · MP4",
            Foreground = ColorBrush("Muted"), FontSize = 11, Margin = new Thickness(0, 0, 0, 15)
        });

        var toggles = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _systemAudio = MakeToggle(T("系統聲音"), true);
        _microphone = MakeToggle(T("麥克風"), false);
        _cursor = MakeToggle(T("滑鼠游標"), true);
        toggles.Children.Add(_systemAudio);
        toggles.Children.Add(_microphone);
        toggles.Children.Add(_cursor);
        layout.Children.Add(toggles);

        _status = new TextBlock
        {
            Text = _bounds.Size != targetPhysicalBounds.Size
                ? T("為符合影片尺寸，右或下邊緣最多內縮 1 像素。")
                : T("按下開始後才會錄製，停止後儲存為 MP4。"),
            Foreground = ColorBrush("Muted"), TextWrapping = TextWrapping.Wrap,
            LineHeight = 19, Margin = new Thickness(0, 0, 0, 13)
        };
        layout.Children.Add(_status);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _recordButton = MakeButton(T("開始錄影"), primary: true);
        _recordButton.IsEnabled = false;
        _recordButton.Click += async (_, _) =>
        {
            if (_savePending) await SaveCompletedRecordingAsync();
            else if (_recordingStarted) await StopAndSaveAsync();
            else await StartRecordingAsync();
        };
        _cancelButton = MakeButton(T("取消"), primary: false);
        _cancelButton.Margin = new Thickness(10, 0, 0, 0);
        _cancelButton.Click += async (_, _) => await DiscardAndCloseAsync();
        Grid.SetColumn(_cancelButton, 1);
        actions.Children.Add(_recordButton);
        actions.Children.Add(_cancelButton);
        layout.Children.Add(actions);

        _pathLabel = new TextBlock
        {
            Foreground = ColorBrush("Muted"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 12, 0, 0), Text = T("停止錄影後再選擇儲存位置")
        };
        layout.Children.Add(_pathLabel);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += async (_, _) =>
        {
            _clock.Text = _service.Elapsed.ToString(@"hh\:mm\:ss");
            if (_recordingStarted && !_busy && !_highlight.IsExcludedFromCapture)
                await HandleRangeInvalidatedAsync();
        };
        _service.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => ShowState(state));
        _service.Interrupted += (_, message) => Dispatcher.UIThread.Post(async () => await HandleSourceEndedAsync(message));
        // The toolbar window exists before it is shown: place it (and on Windows exclude it from capture)
        // before the first frame, then again once its real size and scale are known.
        ConfigureToolbarWindow();
        Opened += (_, _) =>
        {
            ConfigureToolbarWindow();
            WindowAppearance.Apply(this);
            // WPF Loaded: show the border once the toolbar is on screen.
            Dispatcher.UIThread.Post(OnToolbarLoaded, DispatcherPriority.Background);
        };
        _highlight.Invalidated += async (_, _) => await HandleRangeInvalidatedAsync();
        Closing += OnClosing;
        Closed += (_, _) => { _timer.Stop(); _highlight.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); };
        AddHandler(KeyDownEvent, async (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                await DiscardAndCloseAsync();
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Places the toolbar; on Windows also excludes it from capture. Sets <see cref="_excludedFromCapture"/>.</summary>
    partial void ConfigureToolbarWindow();

    /// <summary>The user started dragging the toolbar by its title.</summary>
    partial void OnUserMove();

    /// <summary>
    /// Linux: a toolbar that overlaps the recorded rectangle appears in the video; say so. <paramref name="started"/>
    /// adds the warning in front of the status shown when recording starts.
    /// </summary>
    partial void CheckToolbarPlacement(bool started);

    private void OnToolbarLoaded()
    {
        if (_closingRequested || _allowClose)
            return;
        _loaded = true;
        try { _highlight.Show(this); }
        catch (Exception exception)
        {
            _rangeInvalidated = true;
            _highlight.Hide();
            SetStatus(T("無法顯示錄影範圍，請關閉後重新框選。") + " " + exception.Message, error: true);
        }
        if (!_highlight.IsExcludedFromCapture)
        {
            _rangeInvalidated = true;
            SetStatus(T("無法顯示或排除錄影範圍提示，請關閉後重新框選。"), error: true);
        }
        else
            CheckToolbarPlacement(started: false);
        SetBusy(false);
    }

    private async Task StartRecordingAsync()
    {
        if (_busy || _closingRequested)
            return;
        if (!_excludedFromCapture || !_highlight.IsExcludedFromCapture || _rangeInvalidated)
        {
            SetStatus(T("無法顯示或排除錄影範圍提示，請關閉後重新框選。"), error: true);
            return;
        }

        SetBusy(true);
        try
        {
            await CleanupDraftAsync();
            _draft = new RecordingDraft(Path.Combine(SettingsStore.DataRoot, "Recordings"), _translate);
            _startedAt = DateTime.Now;
            SetStatus(T("正在啟動錄影…"));
            var options = new RecordingOptions
            {
                SystemAudio = _systemAudio.IsChecked == true,
                Microphone = _microphone.IsChecked == true,
                CaptureCursor = _cursor.IsChecked == true
            };
            await _service.StartAsync(_bounds, _draft.OutputPath, options, _lifetime.Token);
            if (_closingRequested)
                return;
            _recordingStarted = true;
            if (_rangeInvalidated || !_highlight.IsExcludedFromCapture)
            {
                SetBusy(false);
                await FinishRecordingAsync(showSaveDialog: false);
                if (!_closingRequested && _savePending)
                    SetStatus(T("錄影範圍已失效，已停止收集；可儲存已錄影片。"), error: true);
                return;
            }
            _highlight.SetRecording(true);
            _recordButton.Content = T("停止錄影");
            _recordButton.Background = ColorBrush("Error");
            _cancelButton.Content = T("放棄錄影");
            _timer.Start();
            // A non-fatal note, such as recording without sound when no sound server is reachable, comes first.
            if (_service.Notice is { } notice)
                SetStatus(notice + " " + T("紅色外框內為錄影範圍；停止後選擇儲存位置。"), error: true);
            else
                SetStatus(T("紅色外框內為錄影範圍；停止後選擇儲存位置。"));
            CheckToolbarPlacement(started: true);
            _ = ObserveCompletionAsync();
        }
        catch (OperationCanceledException) when (_closingRequested) { }
        catch (Exception exception)
        {
            if (!_closingRequested)
                SetStatus(exception.Message, error: true);
        }
        finally { if (!_closingRequested) SetBusy(false); }
    }

    private Task StopAndSaveAsync() => FinishRecordingAsync(showSaveDialog: true);

    private async Task FinishRecordingAsync(bool showSaveDialog)
    {
        if (_busy || !_recordingStarted || _closingRequested)
            return;
        SetBusy(true);
        SetStatus(T("正在完成影片，請稍候…"));
        try
        {
            var result = await _service.StopAsync();
            if (_closingRequested)
                return;
            if (_draft is null) throw new InvalidOperationException(T("影片暫存資料遺失，請重新錄影。"));
            _draft.MarkReady(result);
            _recordingStarted = false;
            _savePending = true;
            _timer.Stop();
            _clock.Text = _service.Elapsed.ToString(@"hh\:mm\:ss");
            _highlight.Hide();
            _recordButton.Content = T("儲存影片");
            _recordButton.Background = ColorBrush("Accent");
            _cancelButton.Content = T("放棄影片");
            _pathLabel.Text = T("影片尚未儲存");
            ToolTip.SetTip(_pathLabel, null);
            SetStatus(T("影片已保留，按「儲存影片」可選擇位置。"));
            if (showSaveDialog) await SaveCompletedRecordingCoreAsync();
        }
        catch (Exception exception)
        {
            if (!_closingRequested)
            {
                SetStatus(exception.Message, error: true);
                ResetStartControls();
            }
        }
        finally { if (!_closingRequested) SetBusy(false); }
    }

    private async Task SaveCompletedRecordingAsync()
    {
        if (_busy || !_savePending || _closingRequested) return;
        SetBusy(true);
        try { await SaveCompletedRecordingCoreAsync(); }
        finally { if (!_closingRequested) SetBusy(false); }
    }

    private async Task SaveCompletedRecordingCoreAsync()
    {
        if (_draft is null || !_savePending || _closingRequested) return;
        try
        {
            var path = _suggestedPath;
            var overwrite = false;
            if (path is null)
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (string.IsNullOrWhiteSpace(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                path = FileDialogs.SaveFile(this, T("儲存錄影"), T("MP4 影片") + "|*.mp4",
                    $"SnipFlow-{_startedAt:yyyyMMdd-HHmmss}.mp4", ".mp4", folder);
                if (path is null)
                {
                    SetStatus(T("影片已保留，按「儲存影片」可選擇位置。"));
                    return;
                }
                overwrite = File.Exists(path); // The dialog confirms replacement before export.
            }
            if (_closingRequested) return;
            SetStatus(T("正在儲存影片…"));
            _exportTask = _draft.SaveAsync(path, overwrite, _lifetime.Token);
            var savedPath = await _exportTask;
            ResultPath = savedPath;
            _savePending = false;
            _pathLabel.Text = savedPath;
            ToolTip.SetTip(_pathLabel, savedPath);
            try { await CleanupDraftAsync(); }
            catch (Exception exception) { SettingsStore.Log(exception); }
            if (_closingRequested) return;
            _allowClose = true;
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
        }
        catch (OperationCanceledException) when (_closingRequested) { }
        catch (Exception exception)
        {
            if (!_closingRequested)
                SetStatus(T("影片已保留，可重試儲存。") + " " + exception.Message, error: true);
        }
    }

    private async Task HandleRangeInvalidatedAsync()
    {
        if (_closingRequested || _allowClose || _savePending) return;
        _rangeInvalidated = true;
        if (_busy) return;
        if (_recordingStarted)
        {
            await FinishRecordingAsync(showSaveDialog: false);
            if (!_closingRequested && _savePending)
                SetStatus(T("錄影範圍已失效，已停止收集；可儲存已錄影片。"), error: true);
        }
        else
        {
            SetStatus(T("無法顯示或排除錄影範圍提示，請關閉後重新框選。"), error: true);
            SetBusy(false);
        }
    }

    /// <summary>The captured source ended on its own (Wayland "Stop sharing"): keep what was recorded.</summary>
    private async Task HandleSourceEndedAsync(string message)
    {
        if (_closingRequested || _allowClose || _savePending || !_recordingStarted || _busy) return;
        await FinishRecordingAsync(showSaveDialog: false);
        if (!_closingRequested && _savePending)
            SetStatus(message, error: true);
    }

    private async Task CleanupDraftAsync()
    {
        var draft = _draft;
        if (draft is null) return;
        if (_service.IsActive) throw new InvalidOperationException(T("已有錄影正在進行。"));
        await draft.DisposeAsync();
        if (ReferenceEquals(_draft, draft)) _draft = null;
    }

    private async Task ObserveCompletionAsync()
    {
        try
        {
            await _service.Completion;
            if (!_busy && !_closingRequested && !_allowClose && !_savePending && _recordingStarted)
            {
                SetStatus(T("錄影已結束，請重新開始。"), error: true);
                ResetStartControls();
                SetBusy(false);
            }
        }
        catch (Exception exception)
        {
            if (!_closingRequested && !_allowClose)
            {
                SetStatus(exception.Message, error: true);
                ResetStartControls();
                SetBusy(false);
            }
        }
    }

    private async Task DiscardAndCloseAsync()
    {
        if (_closingRequested || _allowClose)
            return;
        _closingRequested = true;
        _lifetime.Cancel();
        SetBusy(true);
        _cancelButton.IsEnabled = false;
        SetStatus(T("正在停止錄影並清除未完成檔案…"));
        try
        {
            await _service.CancelAsync();
            if (_exportTask is { } export)
            {
                try { ResultPath = await export; }
                catch (Exception) { }
            }
            await CleanupDraftAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _closingRequested = false;
            SetStatus(T("未能完成錄影清理，請重試關閉。") + " " + exception.Message, error: true);
            _cancelButton.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose)
            return;
        args.Cancel = true;
        // A ready/completed service can finish cleanup synchronously. Defer it until
        // this Closing event returns so Close does not reenter the window while it is closing.
        Dispatcher.UIThread.Post(async () => await DiscardAndCloseAsync(), DispatcherPriority.Normal);
    }

    private void ShowState(RecordingState state)
    {
        _stateLabel.Text = state switch
        {
            RecordingState.Starting => T("正在啟動"),
            RecordingState.Recording => "● " + T("錄影中"),
            RecordingState.Finishing => T("正在完成影片"),
            RecordingState.Completed => T("錄影完成"),
            RecordingState.Cancelled => T("已取消"),
            RecordingState.Failed => T("錄影失敗"),
            _ => T("準備錄影")
        };
        _stateLabel.Foreground = ColorBrush(state is RecordingState.Recording or RecordingState.Failed ? "Error" : "Ink");
    }

    private void ResetStartControls()
    {
        _recordingStarted = false;
        _timer.Stop();
        _highlight.SetRecording(false);
        _recordButton.Content = T("開始錄影");
        _recordButton.Background = ColorBrush("Accent");
        _cancelButton.Content = T("取消");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _recordButton.IsEnabled = !busy && !_closingRequested
            && (_recordingStarted || _savePending || (_loaded && !_rangeInvalidated && _excludedFromCapture));
        _systemAudio.IsEnabled = !busy && !_recordingStarted && !_savePending;
        _microphone.IsEnabled = !busy && !_recordingStarted && !_savePending;
        _cursor.IsEnabled = !busy && !_recordingStarted && !_savePending;
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = ColorBrush(error ? "Error" : "Muted");
    }

    private static CheckBox MakeToggle(string label, bool selected) => new()
    {
        Content = label, IsChecked = selected, VerticalContentAlignment = VerticalAlignment.Center,
        Foreground = ColorBrush("Ink"), Margin = new Thickness(0, 0, 17, 5), FontSize = 12
    };

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text, Padding = new Thickness(15, 10, 15, 10),
        Background = ColorBrush(primary ? "Accent" : "Raised"),
        Foreground = ColorBrush(primary ? "AccentInk" : "Ink"),
        BorderThickness = new Thickness(0), FontWeight = FontWeight.SemiBold,
        FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Theme = ButtonTheme
    };

    /// <summary>The WPF template of the original: a rounded background, 88 % opacity on hover, 40 % when disabled.</summary>
    private static ControlTheme CreateButtonTheme()
    {
        var theme = new ControlTheme(typeof(Button));
        theme.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Button>((_, _) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                CornerRadius = new CornerRadius(7),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            presenter.Bind(ContentPresenter.BackgroundProperty, new TemplateBinding(TemplatedControl.BackgroundProperty));
            presenter.Bind(ContentPresenter.PaddingProperty, new TemplateBinding(TemplatedControl.PaddingProperty));
            presenter.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty));
            presenter.Bind(ContentPresenter.ContentTemplateProperty, new TemplateBinding(ContentControl.ContentTemplateProperty));
            return presenter;
        })));
        theme.Children.Add(new Style(selector => selector.Nesting().Class(":pointerover"))
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.88) }
        });
        theme.Children.Add(new Style(selector => selector.Nesting().Class(":disabled"))
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.4) }
        });
        return theme;
    }

    private string T(string text) => _translate(text);

    private static IBrush ColorBrush(string key) => ThemeService.Brush(key);
}

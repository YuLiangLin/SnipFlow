using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Recording;

/// <summary>A small recording toolbar. Opening it does not start video or audio capture.</summary>
public sealed class RecordingWindow : Window
{
    private readonly Func<string, string> _translate;
    private readonly ScreenRecordingService _service;
    private readonly DrawingRectangle _bounds;
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
    private bool _recordingStarted;
    private bool _allowClose;
    private bool _closingRequested;
    private bool _excludedFromCapture;

    public string? ResultPath { get; private set; }
    public DrawingRectangle CaptureBounds => _bounds;

    public RecordingWindow(DrawingRectangle targetPhysicalBounds, string? outputPath = null, Func<string, string>? translate = null)
    {
        _translate = translate ?? (text => text);
        _bounds = ScreenRecordingService.NormalizeBounds(targetPhysicalBounds, _translate);
        _suggestedPath = outputPath;
        _service = new ScreenRecordingService(_translate);
        Title = "SnipFlow · " + T("螢幕錄影");
        Width = 444;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = ColorBrush("#202020");
        Foreground = ColorBrush("#ECECEC");
        FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI");
        FontSize = 13;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var shell = new Border
        {
            Background = ColorBrush("#202020"), BorderBrush = ColorBrush("#383838"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(20)
        };
        var layout = new StackPanel();
        shell.Child = layout;
        Content = shell;

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = T("螢幕錄影"), FontSize = 20, FontWeight = FontWeights.SemiBold };
        title.MouseLeftButtonDown += (_, args) => { if (args.ButtonState == MouseButtonState.Pressed) DragMove(); };
        heading.Children.Add(title);
        _clock = new TextBlock
        {
            Text = "00:00:00", FontFamily = new FontFamily("Consolas"), FontSize = 24,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_clock, 1);
        heading.Children.Add(_clock);
        layout.Children.Add(heading);

        _stateLabel = new TextBlock { Text = T("準備錄影"), Foreground = ColorBrush("#ECECEC"), Margin = new Thickness(0, 0, 0, 5) };
        layout.Children.Add(_stateLabel);
        layout.Children.Add(new TextBlock
        {
            Text = $"{_bounds.Width:N0} × {_bounds.Height:N0} px · 30 fps · MP4",
            Foreground = ColorBrush("#A3A3A3"), FontSize = 11, Margin = new Thickness(0, 0, 0, 15)
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
            Foreground = ColorBrush("#A3A3A3"), TextWrapping = TextWrapping.Wrap,
            LineHeight = 19, Margin = new Thickness(0, 0, 0, 13)
        };
        layout.Children.Add(_status);

        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _recordButton = MakeButton(T("開始錄影"), primary: true);
        _recordButton.Click += async (_, _) =>
        {
            if (_recordingStarted) await StopAndSaveAsync();
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
            Foreground = ColorBrush("#A3A3A3"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 12, 0, 0), Text = T("儲存位置在開始錄影前選擇")
        };
        layout.Children.Add(_pathLabel);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => _clock.Text = _service.Elapsed.ToString(@"hh\:mm\:ss");
        _service.StateChanged += (_, state) =>
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(() => ShowState(state));
        };
        SourceInitialized += (_, _) => { WindowAppearance.Apply(this); ConfigureToolbarWindow(); };
        Closing += OnClosing;
        Closed += (_, _) => { _timer.Stop(); _lifetime.Cancel(); };
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                await DiscardAndCloseAsync();
            }
        };
    }

    private async Task StartRecordingAsync()
    {
        if (_busy || _closingRequested)
            return;
        if (!_excludedFromCapture)
        {
            SetStatus(T("無法排除錄影操作條，請重新開啟 SnipFlow 後再試。"), error: true);
            return;
        }

        SetBusy(true);
        try
        {
            var defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(defaultFolder))
                defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dialog = new SaveFileDialog
            {
                Title = T("儲存錄影"), Filter = T("MP4 影片") + "|*.mp4", DefaultExt = ".mp4", AddExtension = true,
                FileName = _suggestedPath is null ? $"SnipFlow-{DateTime.Now:yyyyMMdd-HHmmss}.mp4" : Path.GetFileName(_suggestedPath),
                InitialDirectory = _suggestedPath is null ? defaultFolder : Path.GetDirectoryName(Path.GetFullPath(_suggestedPath)),
                OverwritePrompt = true, CheckPathExists = true
            };
            var path = _suggestedPath;
            var overwrite = false;
            if (path is null)
            {
                if (dialog.ShowDialog(this) != true)
                    return;
                path = dialog.FileName;
                overwrite = File.Exists(path); // SaveFileDialog has already requested overwrite confirmation.
            }
            _pathLabel.Text = path;
            _pathLabel.ToolTip = path;
            SetStatus(T("正在啟動錄影…"));
            var options = new RecordingOptions
            {
                SystemAudio = _systemAudio.IsChecked == true,
                Microphone = _microphone.IsChecked == true,
                CaptureCursor = _cursor.IsChecked == true,
                OverwriteExisting = overwrite
            };
            await _service.StartAsync(_bounds, path, options, _lifetime.Token);
            if (_closingRequested)
                return;
            _recordingStarted = true;
            _recordButton.Content = T("停止並儲存");
            _recordButton.Background = ColorBrush("#F17B82");
            _cancelButton.Content = T("放棄錄影");
            _timer.Start();
            SetStatus(T("正在錄製所選範圍。停止後即可播放影片。"));
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

    private async Task StopAndSaveAsync()
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
            ResultPath = result;
            _allowClose = true;
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
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

    private async Task ObserveCompletionAsync()
    {
        try { await _service.Completion; }
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
            ResultPath = null;
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

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose)
            return;
        args.Cancel = true;
        await DiscardAndCloseAsync();
    }

    private void ShowState(RecordingState state)
    {
        _stateLabel.Text = state switch
        {
            RecordingState.Starting => T("正在啟動"),
            RecordingState.Recording => "● " + T("錄影中"),
            RecordingState.Finishing => T("正在完成影片"),
            RecordingState.Completed => T("已儲存"),
            RecordingState.Cancelled => T("已取消"),
            RecordingState.Failed => T("錄影失敗"),
            _ => T("準備錄影")
        };
        _stateLabel.Foreground = ColorBrush(state is RecordingState.Recording or RecordingState.Failed ? "#F17B82" : "#ECECEC");
    }

    private void ResetStartControls()
    {
        _recordingStarted = false;
        _timer.Stop();
        _recordButton.Content = T("開始錄影");
        _recordButton.Background = ColorBrush("#E8E8E8");
        _cancelButton.Content = T("取消");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _recordButton.IsEnabled = !busy && !_closingRequested;
        _systemAudio.IsEnabled = !busy && !_recordingStarted;
        _microphone.IsEnabled = !busy && !_recordingStarted;
        _cursor.IsEnabled = !busy && !_recordingStarted;
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = ColorBrush(error ? "#F5B7BA" : "#A3A3A3");
    }

    private void ConfigureToolbarWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _excludedFromCapture = SetWindowDisplayAffinity(handle, 0x11);
        var area = System.Windows.Forms.Screen.FromRectangle(_bounds).WorkingArea;
        var dpi = GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var physicalWidth = (int)Math.Ceiling(Width * scale);
        var x = Math.Max(area.Left, area.Right - physicalWidth - 16);
        var y = area.Top + 16;
        _ = SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    private static CheckBox MakeToggle(string label, bool selected) => new()
    {
        Content = label, IsChecked = selected, VerticalContentAlignment = VerticalAlignment.Center,
        Foreground = ColorBrush("#ECECEC"), Margin = new Thickness(0, 0, 17, 5), FontSize = 12
    };

    private static Button MakeButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(15, 10, 15, 10),
            Background = ColorBrush(primary ? "#E8E8E8" : "#2B2B2B"),
            Foreground = ColorBrush(primary ? "#191919" : "#ECECEC"),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            FontSize = 12, Cursor = Cursors.Hand
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
        disabled.Setters.Add(new Setter(OpacityProperty, 0.4));
        template.Triggers.Add(disabled);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.88));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }

    private string T(string text) => _translate(text);

    private static SolidColorBrush ColorBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

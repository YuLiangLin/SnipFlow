using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using SnipFlow.Capture;
using SnipFlow.Editor;
using SnipFlow.Scroll;
using SnipFlow.Services;
namespace SnipFlow;

public partial class MainWindow : Window
{
    public UpdateService Updates { get; } = new();
    HwndSource? hotkeySource;
    int hotkeyId = 1;
    bool hotkeyActive;
    bool busy;
    bool selectingHistory;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public MainWindow()
    {
        InitializeComponent(); ToolClick(new Button { Tag = "Pen" }, new()); Editor.SetColor(Color.FromRgb(99, 213, 197));
        Editor.Changed += (_, _) => RefreshDocumentState();
        Updates.Changed += (_, _) => Dispatcher.BeginInvoke(() => { StatusText.Text = Updates.Status; UpdateBanner.Visibility = Updates.ReadyToRestart ? Visibility.Visible : Visibility.Collapsed; });
        SourceInitialized += (_, _) =>
        {
            hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); hotkeySource?.AddHook(HotkeyHook);
            SetHotkey(SettingsStore.Current.Hotkey);
        };
        Loaded += (_, _) => { RefreshHistory(); RefreshDocumentState(); };
    }
    IntPtr HotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x312 && wParam.ToInt32() == hotkeyId) { handled = true; _ = StartCaptureAsync(false); }
        return IntPtr.Zero;
    }
    public bool SetHotkey(string shortcut)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        uint modifiers = shortcut switch { "Ctrl + Shift + S" => 0x4006, "Alt + Shift + S" => 0x4005, _ => 0x4003 };
        int next = hotkeyId + 1;
        if (!RegisterHotKey(hwnd, next, modifiers, 0x53)) { StatusText.Text = $"{shortcut} 已被其他程式使用；可透過按鈕或系統匣截圖。"; return false; }
        if (hotkeyActive) UnregisterHotKey(hwnd, hotkeyId);
        hotkeyId = next; hotkeyActive = true; HotkeyHint.Text = shortcut; return true;
    }
    public void ReleaseHotkey()
    {
        if (hotkeyActive) UnregisterHotKey(new WindowInteropHelper(this).Handle, hotkeyId);
        hotkeySource?.RemoveHook(HotkeyHook); hotkeyActive = false;
    }
    public async Task StartCaptureAsync(bool scrolling)
    {
        if (busy || !PrepareToDiscard()) return;
        busy = true; var wasVisible = IsVisible;
        try
        {
            Hide(); await Task.Delay(220);
            var result = await ScreenshotService.CaptureRegionAsync();
            if (result == null) { if (wasVisible) { Show(); Activate(); } return; }
            BitmapSource image = result.Image;
            if (scrolling)
            {
                var window = new ScrollCaptureWindow(result.Bounds);
                if (window.ShowDialog() != true || window.Result == null) { if (wasVisible) { Show(); Activate(); } return; }
                image = window.Result;
            }
            Editor.LoadImage(image); Show(); Activate();
            await AddHistoryAsync(image);
            if (SettingsStore.Current.CopyAfterCapture) await CopyImageAsync(image);
            StatusText.Text = SettingsStore.Current.CopyAfterCapture ? "截圖完成，已複製到剪貼簿。" : "截圖完成，可以開始標註。";
        }
        catch (Exception ex) { Show(); Activate(); ReportError(ex, "截圖未完成"); }
        finally { busy = false; RefreshDocumentState(); }
    }
    public void OpenImage(string path)
    {
        if (busy || !PrepareToDiscard()) return;
        try { var image = HistoryStore.Read(path); Editor.LoadImage(image); ImageInfo.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px"; StatusText.Text = $"已開啟 {Path.GetFileName(path)}"; RefreshDocumentState(); }
        catch (Exception ex) { ReportError(ex, "圖片無法開啟"); }
    }
    void RefreshDocumentState()
    {
        if (Editor == null) return;
        EmptyState.Visibility = Editor.HasImage ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.IsEnabled = SaveButton.IsEnabled = OcrButton.IsEnabled = Editor.HasImage && !busy;
        UndoButton.IsEnabled = Editor.CanUndo; RedoButton.IsEnabled = Editor.CanRedo;
        Title = Editor.HasEdits ? "SnipFlow · 快剪 *" : "SnipFlow · 快剪";
    }
    void RefreshHistory()
    {
        selectingHistory = true;
        try { var items = HistoryStore.Load(); HistoryList.ItemsSource = items; HistoryCount.Text = items.Count.ToString(); HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
        catch (Exception ex) { ReportError(ex, "歷史讀取失敗"); }
        finally { selectingHistory = false; }
    }
    async Task AddHistoryAsync(BitmapSource image)
    {
        try { await Task.Run(() => HistoryStore.Add(image)); RefreshHistory(); ImageInfo.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px"; }
        catch (Exception ex) { ReportError(ex, "截圖已完成，但歷史儲存失敗"); }
    }
    async Task CopyImageAsync(BitmapSource image)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Clipboard.SetImage(image); return; }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(120); }
        }
    }
    async void CaptureClick(object sender, RoutedEventArgs e) => await StartCaptureAsync(false);
    async void ScrollClick(object sender, RoutedEventArgs e) => await StartCaptureAsync(true);
    async void CopyClick(object sender, RoutedEventArgs e)
    {
        if (!Editor.HasImage || busy) return;
        try { await CopyImageAsync(Editor.ExportImage()); StatusText.Text = "已複製標註後的圖片 · 可直接貼到聊天或文件。"; }
        catch (Exception ex) { ReportError(ex, "複製失敗"); }
    }
    bool SaveCurrent()
    {
        if (!Editor.HasImage) return false;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 圖片 (*.png)|*.png", FileName = $"SnipFlow_{DateTime.Now:yyyyMMdd_HHmmss}.png", AddExtension = true, DefaultExt = ".png", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return false;
        try { var image = Editor.ExportImage(); HistoryStore.SavePng(image, dialog.FileName); Editor.MarkSaved(); HistoryStore.Add(image); RefreshHistory(); StatusText.Text = $"已儲存 {Path.GetFileName(dialog.FileName)}"; return true; }
        catch (Exception ex) { ReportError(ex, "儲存失敗"); return false; }
    }
    void SaveClick(object sender, RoutedEventArgs e) => SaveCurrent();
    public bool PrepareToDiscard()
    {
        if (busy) return false;
        if (!Editor.HasEdits) return true;
        var result = MessageBox.Show(this, "目前的標註尚未儲存，要先存成 PNG 嗎？", "保留標註", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && SaveCurrent();
    }
    void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "圖片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有檔案|*.*" };
        if (dialog.ShowDialog(this) == true) OpenImage(dialog.FileName);
    }
    void ImageDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenImage(files[0]);
    }
    void ToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } button && Enum.TryParse<AnnotationTool>(tag, out var tool))
        { Editor.SetTool(tool); foreach (var candidate in ToolButtons.Children.OfType<Button>().Where(b => b.Tag is string)) { candidate.Background = candidate.Tag?.ToString() == tag ? new SolidColorBrush(Color.FromRgb(35,69,69)) : Brushes.Transparent; candidate.Foreground = candidate.Tag?.ToString() == tag ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink"); } ToolLabel.Text = tool switch { AnnotationTool.Select => "選取／移動", AnnotationTool.Arrow => "箭頭", AnnotationTool.Rectangle => "矩形", AnnotationTool.Ellipse => "橢圓", AnnotationTool.Pen => "畫筆", AnnotationTool.Highlight => "螢光筆", AnnotationTool.Text => "文字", _ => "馬賽克" }; StatusText.Text = button.ToolTip?.ToString() ?? "就緒"; }
    }
    void ColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value }) Editor.SetColor((Color)ColorConverter.ConvertFromString(value));
    }
    void StrokeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Editor != null) Editor.StrokeWidth = e.NewValue; }
    void UndoClick(object sender, RoutedEventArgs e) => Editor.Undo();
    void RedoClick(object sender, RoutedEventArgs e) => Editor.Redo();
    void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (!selectingHistory && HistoryList.SelectedItem is HistoryItem item) OpenImage(item.Path);
    }
    async void OcrClick(object sender, RoutedEventArgs e)
    {
        if (!Editor.HasImage || busy) return;
        busy = true; RefreshDocumentState(); StatusText.Text = "正在本機辨識文字…";
        try
        {
            var text = await OcrService.RecognizeAsync(Editor.ExportImage());
            var window = new TextResultWindow(text) { Owner = this }; window.ShowDialog(); StatusText.Text = string.IsNullOrWhiteSpace(text) ? "沒有辨識出文字。" : "文字辨識完成。";
        }
        catch (Exception ex) { ReportError(ex, "文字辨識未完成"); }
        finally { busy = false; RefreshDocumentState(); }
    }
    void SettingsClick(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(this) { Owner = this }.ShowDialog(); HotkeyHint.Text = SettingsStore.Current.Hotkey; RefreshHistory();
    }
    void ApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        if (!PrepareToDiscard()) return;
        try { Updates.ApplyAndRestart(); } catch (Exception ex) { ReportError(ex, "更新尚未套用"); }
    }
    void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.N) { _ = StartCaptureAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0); }
            else if (e.Key == Key.S) SaveCurrent();
            else if (e.Key == Key.O) OpenClick(this, new());
            else if (e.Key == Key.C) CopyClick(this, new());
            else if (e.Key == Key.Z) Editor.Undo();
            else if (e.Key == Key.Y) Editor.Redo();
            else if (e.Key == Key.V)
            {
                if (PrepareToDiscard()) try { if (Clipboard.ContainsImage()) { var image = Clipboard.GetImage(); if (image != null) { image.Freeze(); Editor.LoadImage(image); ImageInfo.Text = $"{image.PixelWidth} × {image.PixelHeight} px"; } } } catch (Exception ex) { ReportError(ex, "貼上失敗"); }
            }
            else return;
            e.Handled = true; return;
        }
        var key = e.Key switch { Key.V => "Select", Key.A => "Arrow", Key.R => "Rectangle", Key.E => "Ellipse", Key.P => "Pen", Key.H => "Highlight", Key.T => "Text", Key.M => "Mosaic", _ => null };
        if (key != null) { ToolClick(new Button { Tag = key }, new()); e.Handled = true; }
    }
    void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element is Button || FindButton(element)) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    static bool FindButton(DependencyObject child)
    {
        for (DependencyObject? node = child; node != null; node = VisualTreeHelper.GetParent(node)) if (node is Button) return true;
        return false;
    }
    void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void HideClick(object sender, RoutedEventArgs e) => Hide();
    void ExitClick(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitRequested();
    void WindowClosing(object? sender, CancelEventArgs e) { if (!Application.Current.Dispatcher.HasShutdownStarted) { e.Cancel = true; Hide(); } }
    void ReportError(Exception ex, string title) { SettingsStore.Log(ex); StatusText.Text = title; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
}

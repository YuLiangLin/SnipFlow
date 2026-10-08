using System.ComponentModel;
using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using SnipFlow.Capture;
using SnipFlow.Editor;
using SnipFlow.Recording;
using SnipFlow.Scroll;
using SnipFlow.Services;
using CaptureMode = SnipFlow.Capture.CaptureMode;
namespace SnipFlow;

public partial class MainWindow : Window
{
    public UpdateService Updates { get; } = new();
    GlobalHotkeyService? hotkeys;
    bool capturePending;
    public string? ActiveHotkey => hotkeys?.ActiveGesture?.ToString();
    public string HotkeyError { get; private set; } = "";
    bool busy;
    bool updating;
    bool selectingHistory;
    bool refreshingProperties;
    bool compact = true;
    bool modeInitialized;
    bool hadImage;
    Rect? editorBounds;
    readonly System.Windows.Threading.DispatcherTimer autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    string autoSaveDirectory = SettingsStore.Current.CaptureSaveDirectory;
    bool autoSaveEnabled = SettingsStore.Current.AutoSaveCaptures;
    string? autoSavePath;
    long autoSavedRevision = -1;
    int documentGeneration;
    long autoSaveSequence;
    bool autoSaveInProgress;
    bool isClosed;
    Task? autoSaveWrite;
    string statusKey = "就緒";
    object?[] statusValues = Array.Empty<object?>();
    bool showingUpdateStatus;
    AnnotationTool activeTool = AnnotationTool.Pen;
    Color activeColor = Color.FromRgb(99, 213, 197);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowEnabled(IntPtr hwnd);
    bool CanStartCapture => !historyDragInProgress && IsEnabled && IsWindowEnabled(new WindowInteropHelper(this).Handle);
    public MainWindow()
    {
        InitializeComponent(); ToolClick(new Button { Tag = "Pen" }, new()); Editor.SetColor(Color.FromRgb(99, 213, 197));
        InitializeHistoryDrag();
        TaskbarIconService.Attach(this);
        Editor.Changed += (_, _) => { RefreshDocumentState(); QueueAutoSave(); };
        Editor.SelectionChanged += (_, _) => RefreshProperties();
        Updates.Changed += UpdateChanged;
        I18n.Changed += LanguageChanged;
        SettingsStore.Changed += SettingsChanged;
        autoSaveTimer.Tick += AutoSaveTick;
        Closed += (_, _) => { isClosed = true; CloseColorPalette(); autoSaveTimer.Stop(); Updates.Changed -= UpdateChanged; SettingsStore.Changed -= SettingsChanged; I18n.Changed -= LanguageChanged; ReleaseHotkey(); };
        ApplyWindowMode(true);
        SourceInitialized += (_, _) => { WindowAppearance.Apply(this); WindowBoundsService.Attach(this); UpdateMinimumWindowSize(); SetHotkey(SettingsStore.Current.Hotkey); };
        SizeChanged += (_, _) => UpdateMinimumWindowSize();
        StateChanged += (_, _) => { UpdateMinimumWindowSize(); RefreshUpdateControls(); };
        LocationChanged += (_, _) => UpdateMinimumWindowSize();
        Loaded += (_, _) => { RefreshHistory(); RefreshDocumentState(); if (Updates.ReadyToRestart) RefreshUpdateStatus(); };
    }
    void GlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (Keyboard.FocusedElement is HotkeyInputBox input && Window.GetWindow(input)?.IsActive == true && hotkeys?.ActiveGesture is { } gesture)
        { input.SetGesture(gesture); return; }
        if (CanStartCapture) _ = StartCaptureAsync(false);
    }
    public bool SetHotkey(string shortcut)
    {
        if (!HotkeyGesture.TryParse(shortcut, out var gesture))
        { HotkeyError = I18n.T("請使用至少兩個修飾鍵加字母／數字，或 F1–F11。"); RefreshHotkeyHint(); return false; }
        try
        {
            if (hotkeys == null) { hotkeys = new GlobalHotkeyService(); hotkeys.Pressed += GlobalHotkeyPressed; }
            if (!hotkeys.TryRegister(gesture, out var error))
            {
                HotkeyError = error == 1409 ? I18n.F("{0} 已被其他程式使用；可透過按鈕或系統匣截圖。", gesture.ToString())
                    : I18n.F("{0} 無法啟用（Windows 錯誤 {1}）。", gesture.ToString(), error);
                SetStatus(error == 1409 ? "{0} 已被其他程式使用；可透過按鈕或系統匣截圖。" : "{0} 無法啟用（Windows 錯誤 {1}）。", gesture.ToString(), error);
                RefreshHotkeyHint(); return false;
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex); HotkeyError = I18n.T("全域快捷鍵無法啟用。");
            SetStatus("全域快捷鍵無法啟用。"); RefreshHotkeyHint(); return false;
        }
        HotkeyError = ""; RefreshHotkeyHint(); SetStatus("全域截圖快捷鍵：{0}", gesture.ToString()); return true;
    }
    void RefreshHotkeyHint() => HotkeyHint.Text = CompactHotkeyHint.Text = ActiveHotkey ?? I18n.T("全域快捷鍵未啟用");
    public void ReleaseHotkey()
    {
        hotkeys?.Dispose(); hotkeys = null; RefreshHotkeyHint();
    }
    public Task StartCaptureAsync(bool scrolling) => StartCaptureCoreAsync(CaptureMode.Region, scrolling);
    public Task StartCaptureAsync(CaptureMode mode) => StartCaptureCoreAsync(mode, scrolling: false);
    async Task StartCaptureCoreAsync(CaptureMode mode, bool scrolling)
    {
        if (busy || capturePending || !CanStartCapture) return;
        CloseColorPalette();
        try
        {
            bool appendToCollage = Editor.IsCollage;
            if (appendToCollage)
            {
                if (Editor.ImageCount >= 24) { SetStatus("每份合圖最多可放入 24 張圖片。"); return; }
                Editor.CaptureSession();
            }
            else if (!PrepareToDiscard()) return;
            int expectedGeneration = documentGeneration;
            capturePending = true;
            busy = true; var wasVisible = IsVisible;
            RefreshDocumentState();
            Hide(); await Task.Delay(220);
            var result = scrolling
                ? await ScreenshotService.CaptureRegionAsync()
                : await ScreenshotService.CaptureAsync(mode);
            if (result == null) { if (wasVisible) { Show(); Activate(); } return; }
            BitmapSource image = result.Image;
            if (scrolling)
            {
                var window = new ScrollCaptureWindow(result.Bounds, result.Image);
                if (window.ShowDialog() != true || window.Result == null) { if (wasVisible) { Show(); Activate(); } return; }
                image = window.Result;
            }
            if (expectedGeneration != documentGeneration || appendToCollage != Editor.IsCollage)
                throw new InvalidOperationException(I18n.T("目前文件已變更，請重新截圖。"));
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (appendToCollage)
            {
                Editor.AddImages(new[] { image });
                ToolClick(new Button { Tag = "Select" }, new());
            }
            else LoadDocument(image);
            ApplyWindowMode(false); Show(); Activate();
            await AddHistoryAsync(image);
            if (SettingsStore.Current.CopyAfterCapture) await CopyImageAsync(image);
            SetStatus(Editor.IsCollage ? "截圖已加入合圖。" : SettingsStore.Current.CopyAfterCapture ? "截圖完成，已複製到剪貼簿。" : "截圖完成，可以開始標註。");
        }
        catch (Exception ex) { Show(); Activate(); ReportError(ex, "截圖未完成"); }
        finally { busy = false; capturePending = false; RefreshDocumentState(); QueueAutoSave(); }
    }
    public async Task StartRecordingAsync()
    {
        if (busy || capturePending || !CanStartCapture) return;
        Editor.CommitTextEdit();
        busy = true; var wasVisible = IsVisible;
        RefreshDocumentState();
        try
        {
            Hide(); await Task.Delay(220);
            var region = await ScreenshotService.CaptureRegionAsync();
            if (region == null) return;
            var recording = new RecordingWindow(region.Bounds, translate: I18n.T);
            recording.ShowDialog();
            if (recording.ResultPath != null) SetStatus("錄影已儲存：{0}", recording.ResultPath);
        }
        catch (Exception ex) { Show(); Activate(); ReportError(ex, "錄影未完成"); }
        finally
        {
            busy = false;
            if (wasVisible) { Show(); Activate(); }
            RefreshDocumentState();
        }
    }
    public void OpenImage(string path)
    {
        if (Path.GetExtension(path).Equals(".snipflow", StringComparison.OrdinalIgnoreCase)) { OpenProject(path); return; }
        if (busy || !PrepareToDiscard()) return;
        try { var image = HistoryStore.Read(path); LoadDocument(image); ApplyWindowMode(false); ImageInfo.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px"; SetStatus("已開啟 {0}", Path.GetFileName(path)); RefreshDocumentState(); }
        catch (Exception ex) { ReportError(ex, "圖片無法開啟"); }
    }
    void LoadDocument(BitmapSource image)
    {
        ResetDocumentStorage();
        Editor.LoadImage(image);
    }
    internal void RestoreAfterUpdate(string directory)
    {
        ResetDocumentStorage();
        try
        {
            EditorSessionStore.Restore(Editor, directory);
            ApplyWindowMode(false); ImageInfo.Text = "";
            SetStatus("已恢復更新前的編輯。"); RefreshDocumentState(); QueueAutoSave();
        }
        catch (Exception ex) { ReportError(ex, "無法恢復編輯，更新前的工作仍保存在本機。"); }
    }
    void QueueAutoSave()
    {
        autoSaveTimer.Stop();
        if (isClosed || !SettingsStore.Current.AutoSaveCaptures || !Editor.HasImage) return;
        if (Editor.Revision == autoSavedRevision)
        {
            if (!Editor.IsInteracting && Editor.HasEdits) Editor.MarkSaved();
            return;
        }
        autoSaveTimer.Start();
    }
    void SettingsChanged(object? sender, EventArgs e)
    {
        if (isClosed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SettingsChanged(sender, e)); return; }
        var finishPendingSave = autoSaveEnabled && !SettingsStore.Current.AutoSaveCaptures;
        autoSaveEnabled = SettingsStore.Current.AutoSaveCaptures;
        if (finishPendingSave && Editor.HasImage && (autoSavePath != null || autoSaveWrite != null)
            && (autoSaveInProgress || Editor.Revision != autoSavedRevision))
            SaveAutomaticallyBeforeDiscard(finishPending: true);
        var directory = SettingsStore.Current.CaptureSaveDirectory;
        if (!string.Equals(autoSaveDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            autoSaveDirectory = directory; documentGeneration++; autoSavePath = null; autoSavedRevision = -1;
        }
        QueueAutoSave();
    }
    async void AutoSaveTick(object? sender, EventArgs e)
    {
        autoSaveTimer.Stop();
        if (isClosed || !SettingsStore.Current.AutoSaveCaptures || !Editor.HasImage || Editor.Revision == autoSavedRevision) return;
        if (busy || capturePending || autoSaveInProgress || Editor.IsInteracting) { QueueAutoSave(); return; }
        autoSaveInProgress = true;
        var sequence = ++autoSaveSequence;
        var generation = documentGeneration;
        var revision = Editor.Revision;
        try
        {
            var image = Editor.ExportImage();
            var project = Editor.IsCollage ? Editor.CaptureSession() : null;
            revision = Editor.Revision;
            var path = autoSavePath ??= CaptureFileStore.CreatePath(autoSaveDirectory);
            // The last saved revision becomes uncertain as soon as a replacement starts.
            // Undoing back to it must still wait for, and replace, the pending write.
            autoSavedRevision = -1; Editor.MarkUnsaved();
            autoSaveWrite = Task.Run(() =>
            {
                CaptureFileStore.Save(image, path);
                if (project is not null) EditorSessionStore.SaveProject(project, Path.ChangeExtension(path, ".snipflow"));
            });
            await autoSaveWrite;
            if (!isClosed && SettingsStore.Current.AutoSaveCaptures && generation == documentGeneration && sequence == autoSaveSequence && path == autoSavePath)
            {
                autoSavedRevision = revision;
                if (Editor.Revision == revision && !Editor.IsInteracting)
                {
                    Editor.MarkSaved(); SetStatus(project is null ? "已自動儲存：{0}" : "已自動儲存 PNG 與可編輯專案：{0}", project is null ? path : Path.ChangeExtension(path, ".snipflow"));
                }
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            if (!isClosed && generation == documentGeneration && sequence == autoSaveSequence && autoSavedRevision != revision)
                SetStatus("自動儲存失敗，請檢查截圖資料夾或手動儲存。");
        }
        finally
        {
            autoSaveInProgress = false; autoSaveWrite = null;
            if (!isClosed && (generation != documentGeneration || Editor.Revision != revision)) QueueAutoSave();
        }
    }
    bool SaveAutomaticallyBeforeDiscard(bool finishPending = false)
    {
        if (!SettingsStore.Current.AutoSaveCaptures && !finishPending) return false;
        autoSaveTimer.Stop();
        try
        {
            // Finish the background disk write before writing the latest revision to the same file.
            // This task contains only PNG encoding and file IO, so it does not need the UI thread.
            FinishAutoSaveWrite();
            autoSaveSequence++;
            var image = Editor.ExportImage();
            var path = autoSavePath ??= CaptureFileStore.CreatePath(autoSaveDirectory);
            CaptureFileStore.Save(image, path);
            if (Editor.IsCollage) EditorSessionStore.SaveProject(Editor, Path.ChangeExtension(path, ".snipflow"));
            autoSavedRevision = Editor.Revision; Editor.MarkSaved();
            SetStatus(Editor.IsCollage ? "已自動儲存 PNG 與可編輯專案：{0}" : "已自動儲存：{0}", Editor.IsCollage ? Path.ChangeExtension(path, ".snipflow") : path);
            return true;
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex); Editor.MarkUnsaved(); SetStatus("自動儲存失敗，請檢查截圖資料夾或手動儲存。");
            return false;
        }
    }
    void FinishAutoSaveWrite()
    {
        try { autoSaveWrite?.GetAwaiter().GetResult(); }
        catch (Exception ex) { SettingsStore.Log(ex); }
    }
    void RefreshDocumentState()
    {
        if (Editor == null) return;
        if (busy || capturePending) CloseColorPalette();
        EmptyState.Visibility = Editor.HasImage ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.IsEnabled = SaveButton.IsEnabled = OcrButton.IsEnabled = Editor.HasImage && !busy;
        Editor.IsEnabled = !busy;
        CollageBar.Visibility = Editor.IsCollage ? Visibility.Visible : Visibility.Collapsed;
        AddImagesButton.IsEnabled = ProjectButton.IsEnabled = !busy;
        ImageInfo.Text = Editor.HasImage ? $"{Editor.PixelWidth:N0} × {Editor.PixelHeight:N0} px" : "";
        UndoButton.IsEnabled = Editor.CanUndo && !busy && !capturePending;
        RedoButton.IsEnabled = Editor.CanRedo && !busy && !capturePending;
        Workspace.IsEnabled = CompactPanel.IsEnabled = HeaderCapture.IsEnabled = !busy && !capturePending;
        HistoryClearButton.IsEnabled = !busy && !capturePending && !historyDragInProgress && HistoryList.Items.Count > 0;
        Title = Editor.HasEdits ? "SnipFlow *" : "SnipFlow";
        if (Editor.HasImage && !hadImage) ApplyWindowMode(false);
        hadImage = Editor.HasImage;
        RefreshProperties();
        RefreshUpdateControls();
    }
    void ApplyWindowMode(bool useCompact)
    {
        if (Workspace == null) return;
        if (modeInitialized && compact == useCompact) return;
        modeInitialized = true;
        if (useCompact && !compact)
            editorBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        compact = useCompact;
        Workspace.Visibility = HeaderCapture.Visibility = CompactButton.Visibility = MaximizeButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (compact)
        {
            WindowState = WindowState.Normal;
            MinWidth = 420; Width = 420;
            ResizeMode = ResizeMode.CanMinimize;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            UpdateMinimumWindowSize();
            var area = WindowBoundsService.GetWorkArea(this);
            var bounds = editorBounds ?? new Rect(area.Left + Math.Max(0, (area.Width - 1240) / 2), area.Top + Math.Max(0, (area.Height - 810) / 2), Math.Min(1240, area.Width), Math.Min(810, area.Height));
            if (WindowState == WindowState.Normal)
            {
                Width = Math.Clamp(bounds.Width, MinWidth, area.Width); Height = Math.Clamp(bounds.Height, MinHeight, area.Height);
                Left = Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - Width));
                Top = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
            }
        }
        RefreshUpdateControls();
    }
    void UpdateMinimumWindowSize()
    {
        if (compact || !modeInitialized) return;
        var area = WindowBoundsService.GetWorkArea(this);
        MinWidth = Math.Min(950, area.Width); MinHeight = Math.Min(620, area.Height);
    }
    void CompactClick(object sender, RoutedEventArgs e) { Editor.CommitTextEdit(); ApplyWindowMode(true); }
    void ExpandClick(object sender, RoutedEventArgs e) => ApplyWindowMode(false);
    void LanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        RefreshDocumentState();
        RefreshHotkeyHint();
        StatusText.Text = showingUpdateStatus ? Updates.Status : I18n.F(statusKey, statusValues);
        StatusText.ToolTip = StatusText.Text;
    });
    void SetStatus(string key, params object?[] values)
    {
        showingUpdateStatus = false; statusKey = key; statusValues = values; StatusText.Text = I18n.F(key, values); StatusText.ToolTip = StatusText.Text;
    }
    void UpdateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (isClosed) return;
        RefreshUpdateStatus();
    });
    void RefreshUpdateStatus()
    {
        showingUpdateStatus = true;
        StatusText.Text = Updates.Status; StatusText.ToolTip = StatusText.Text;
        RefreshUpdateControls();
    }
    void RefreshUpdateControls()
    {
        bool ready = Updates.ReadyToRestart;
        bool showCompactAction = ready && compact;
        UpdateBanner.Visibility = ready && !compact ? Visibility.Visible : Visibility.Collapsed;
        CompactUpdateButton.Visibility = showCompactAction ? Visibility.Visible : Visibility.Collapsed;
        EditorUpdateButton.IsEnabled = CompactUpdateButton.IsEnabled = ready && !Updates.IsBusy && !updating && !busy && !capturePending && !historyDragInProgress;
        StatusRow.Height = new GridLength(showCompactAction ? 42 : 26);
        if (compact && modeInitialized)
        {
            // Keep the capture controls at their normal size when the update action appears.
            double height = showCompactAction ? 280 : 264;
            MinHeight = height;
            if (Height != height) Height = height;
            if (showCompactAction && WindowState == WindowState.Normal && double.IsFinite(Top))
            {
                var area = WindowBoundsService.GetWorkArea(this);
                double top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - height));
                if (Top != top) Top = top;
            }
        }
    }
    void RefreshProperties()
    {
        if (Editor == null || ColorButtons == null || refreshingProperties) return;
        refreshingProperties = true;
        try
        {
            var shownTool = Editor.SelectedTool ?? activeTool;
            ToolLabel.Text = ToolName(shownTool);
            TextProperties.Visibility = shownTool == AnnotationTool.Text ? Visibility.Visible : Visibility.Collapsed;
            WidthProperties.Visibility = shownTool is AnnotationTool.Text or AnnotationTool.Image ? Visibility.Collapsed : Visibility.Visible;
            ColorButtons.Visibility = shownTool is AnnotationTool.Image or AnnotationTool.Mosaic ? Visibility.Collapsed : Visibility.Visible;
            WidthSlider.Value = Editor.SelectedStrokeWidth ?? Editor.StrokeWidth;
            if (!TextSizeBox.IsKeyboardFocusWithin) TextSizeBox.Text = (Editor.SelectedTextSize ?? Editor.TextSize).ToString("0.#", CultureInfo.CurrentCulture);
            var color = Editor.SelectedColor ?? activeColor;
            foreach (var button in ColorButtons.Children.OfType<Button>())
            {
                if (button.Tag is not string hex) continue;
                bool selected = (Color)ColorConverter.ConvertFromString(hex) == color;
                button.BorderBrush = (Brush)FindResource(selected ? "Accent" : "Muted");
                button.BorderThickness = new Thickness(selected ? 2 : 1);
            }
            MoreColorsButton.ToolTip = I18n.T("更多顏色") + " · " + ColorPaletteView.ToHex(color);
        }
        finally { refreshingProperties = false; }
    }
    static string ToolKey(AnnotationTool tool) => tool switch { AnnotationTool.Select => "選取／移動", AnnotationTool.Arrow => "箭頭", AnnotationTool.Rectangle => "矩形", AnnotationTool.Ellipse => "橢圓", AnnotationTool.Pen => "畫筆", AnnotationTool.Highlight => "螢光筆", AnnotationTool.Text => "文字", AnnotationTool.Image => "圖片", _ => "馬賽克" };
    static string ToolName(AnnotationTool tool) => I18n.T(ToolKey(tool));
    void RefreshHistory()
    {
        selectingHistory = true;
        try { var items = HistoryStore.Load(); HistoryList.ItemsSource = items; HistoryCount.Text = items.Count.ToString(); HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; HistoryClearButton.IsEnabled = !busy && !capturePending && !historyDragInProgress && items.Count > 0; }
        catch (Exception ex) { ReportError(ex, "歷史讀取失敗"); }
        finally { selectingHistory = false; }
    }
    async Task AddHistoryAsync(BitmapSource image)
    {
        try { await Task.Run(() => HistoryStore.Add(image)); RefreshHistory(); RefreshDocumentState(); }
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
    async void RecordClick(object sender, RoutedEventArgs e) => await StartRecordingAsync();
    async void CopyClick(object sender, RoutedEventArgs e)
    {
        if (!Editor.HasImage || busy) return;
        try { await CopyImageAsync(Editor.ExportImage()); SetStatus("已複製含標註圖片"); }
        catch (Exception ex) { ReportError(ex, "複製失敗"); }
    }
    bool SaveCurrent()
    {
        if (!Editor.HasImage) return false;
        Editor.CommitTextEdit();
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = I18n.T("PNG 圖片 (*.png)|*.png"), FileName = $"SnipFlow_{DateTime.Now:yyyyMMdd_HHmmss}.png", AddExtension = true, DefaultExt = ".png", OverwritePrompt = true };
        if (Directory.Exists(SettingsStore.Current.CaptureSaveDirectory)) dialog.InitialDirectory = SettingsStore.Current.CaptureSaveDirectory;
        if (dialog.ShowDialog(this) != true) return false;
        try
        {
            FinishAutoSaveWrite();
            var isAutoSaveFile = string.Equals(Path.GetFullPath(dialog.FileName), autoSavePath, StringComparison.OrdinalIgnoreCase);
            if (isAutoSaveFile) { autoSaveSequence++; autoSavedRevision = -1; }
            var image = Editor.ExportImage(); CaptureFileStore.Save(image, dialog.FileName);
            if (!Editor.IsCollage)
            {
                if (isAutoSaveFile) autoSavedRevision = Editor.Revision;
                Editor.MarkSaved();
            }
            HistoryStore.Add(image); RefreshHistory(); SetStatus("已儲存 {0}", Path.GetFileName(dialog.FileName));
            return true;
        }
        catch (Exception ex) { ReportError(ex, "儲存失敗"); return false; }
    }
    void SaveClick(object sender, RoutedEventArgs e) => SaveCurrent();
    public bool PrepareToDiscard()
    {
        if (busy || historyDragInProgress) return false;
        Editor.CommitTextEdit();
        var hasUnsavedEdits = Editor.HasEdits;
        var needsAutoSave = SettingsStore.Current.AutoSaveCaptures && Editor.HasImage && Editor.Revision != autoSavedRevision;
        if (!hasUnsavedEdits && !needsAutoSave) return true;
        if (SaveAutomaticallyBeforeDiscard()) return true;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
        var result = MessageBox.Show(this, I18n.T(Editor.IsCollage ? "合圖尚未儲存，要先儲存可編輯專案嗎？" : hasUnsavedEdits ? "目前的標註尚未儲存，要先存成 PNG 嗎？" : "自動儲存未完成，要改為手動儲存嗎？"), I18n.T("保留標註"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && SaveDocument();
    }
    void OpenClick(object sender, RoutedEventArgs e)
    {
        if (busy || capturePending) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = I18n.T("圖片與 SnipFlow 專案|*.snipflow;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有檔案|*.*") };
        if (dialog.ShowDialog(this) == true) OpenImage(dialog.FileName);
    }
    void ImageDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (files.Length == 1 && Path.GetExtension(files[0]).Equals(".snipflow", StringComparison.OrdinalIgnoreCase)) OpenProject(files[0]);
        else if (Editor.IsCollage || files.Length > 1) AddImageFiles(files);
        else OpenImage(files[0]);
        e.Handled = true;
    }
    void ToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } button && Enum.TryParse<AnnotationTool>(tag, out var tool))
        { activeTool = tool; Editor.SetTool(tool); foreach (var candidate in ToolButtons.Children.OfType<Button>().Where(b => b.Tag is string)) { candidate.Background = candidate.Tag?.ToString() == tag ? (Brush)FindResource("Selected") : Brushes.Transparent; candidate.Foreground = (Brush)FindResource("Ink"); } RefreshProperties(); SetStatus(ToolKey(tool)); }
    }
    void ColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value }) ApplyAnnotationColor((Color)ColorConverter.ConvertFromString(value));
    }
    void StrokeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Editor != null && !refreshingProperties) Editor.StrokeWidth = e.NewValue; }
    void PropertyDragStarted(object sender, DragStartedEventArgs e) => Editor.BeginPropertyEdit();
    void PropertyDragCompleted(object sender, DragCompletedEventArgs e) => Editor.EndPropertyEdit();
    void TextSizeChanged(object sender, KeyboardFocusChangedEventArgs e) => ApplyTextSize();
    void TextSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyTextSize(); Editor.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { TextSizeBox.Text = (Editor.SelectedTextSize ?? Editor.TextSize).ToString("0.#", CultureInfo.CurrentCulture); Editor.Focus(); e.Handled = true; }
    }
    void ApplyTextSize()
    {
        if (refreshingProperties || Editor == null) return;
        if (double.TryParse(TextSizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) && double.IsFinite(size)) Editor.TextSize = Math.Clamp(size, 12, 240);
        TextSizeBox.Text = (Editor.SelectedTextSize ?? Editor.TextSize).ToString("0.#", CultureInfo.CurrentCulture);
    }
    void UndoClick(object sender, RoutedEventArgs e) { if (!busy && !capturePending) Editor.Undo(); }
    void RedoClick(object sender, RoutedEventArgs e) { if (!busy && !capturePending) Editor.Redo(); }
    void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (!selectingHistory && HistoryList.SelectedItem is HistoryItem item)
        {
            if (!Editor.IsCollage) OpenImage(item.Path);
        }
    }
    async void OcrClick(object sender, RoutedEventArgs e)
    {
        if (!Editor.HasImage || busy) return;
        busy = true; RefreshDocumentState(); SetStatus("正在本機辨識文字…");
        try
        {
            var text = await OcrService.RecognizeAsync(Editor.ExportImage());
            var window = new TextResultWindow(text) { Owner = this }; window.ShowDialog(); SetStatus(string.IsNullOrWhiteSpace(text) ? "沒有辨識出文字。" : "文字辨識完成。");
        }
        catch (Exception ex) { ReportError(ex, "文字辨識未完成"); }
        finally { busy = false; RefreshDocumentState(); }
    }
    void SettingsClick(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(this) { Owner = this }.ShowDialog(); RefreshHotkeyHint(); RefreshHistory();
    }
    public async Task UpdateNowAsync()
    {
        if (updating || busy || capturePending || historyDragInProgress || Updates.IsBusy) return;
        updating = busy = true; RefreshDocumentState();
        try
        {
            if (!Updates.ReadyToRestart) await Updates.CheckAsync(true);
            if (!Updates.ReadyToRestart) return;
            FinishAutoSaveWrite();
            string[] restartArgs = Array.Empty<string>();
            if (Editor.HasImage)
            {
                if (SettingsStore.Current.AutoSaveCaptures && Editor.Revision != autoSavedRevision)
                    SaveAutomaticallyBeforeDiscard();
                var checkpoint = EditorSessionStore.Save(Editor);
                restartArgs = new[] { "--restore-update-session", checkpoint };
            }
            // Committing edits while saving the checkpoint can queue another autosave.
            autoSaveTimer.Stop();
            Updates.ApplyAndRestart(restartArgs);
        }
        finally { updating = busy = false; RefreshDocumentState(); QueueAutoSave(); }
    }
    async void ApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        try { await UpdateNowAsync(); } catch (Exception ex) { ReportError(ex, "更新尚未套用"); }
    }
    void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (busy || capturePending || historyDragInProgress) return;
        var modifiers = Keyboard.Modifiers;
        if (Editor.IsTextEditing)
        {
            if (modifiers == ModifierKeys.Control && e.Key == Key.S) { Editor.CommitTextEdit(); SaveDocument(); e.Handled = true; }
            return;
        }
        if (Keyboard.FocusedElement is TextBox)
        {
            if (modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                if (TextSizeBox.IsKeyboardFocusWithin) ApplyTextSize();
                SaveDocument(); e.Handled = true;
            }
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
        { Editor.Redo(); e.Handled = true; return; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        { SaveProjectCurrent(saveAs: true); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.S) SaveDocument();
            else if (e.Key == Key.O) OpenClick(this, new());
            else if (e.Key == Key.C) CopyClick(this, new());
            else if (e.Key == Key.Z) Editor.Undo();
            else if (e.Key == Key.Y) Editor.Redo();
            else if (e.Key == Key.A) Editor.SelectAll();
            else if (e.Key == Key.D) RunObjectCommand(Editor.DuplicateSelection);
            else if (e.Key == Key.V)
            {
                try
                {
                    if (Clipboard.ContainsImage())
                    {
                        var image = Clipboard.GetImage();
                        if (image != null)
                        {
                            image.Freeze();
                            if (Editor.IsCollage) Editor.AddImages(new[] { image });
                            else if (PrepareToDiscard()) LoadDocument(image);
                            ApplyWindowMode(false);
                            RefreshDocumentState();
                        }
                    }
                    else if (Clipboard.ContainsText()) _ = StartCodeImageAsync();
                }
                catch (Exception ex) { ReportError(ex, "貼上失敗"); }
            }
            else return;
            e.Handled = true; return;
        }
        if (modifiers != ModifierKeys.None) return;
        var key = e.Key switch { Key.V => "Select", Key.A => "Arrow", Key.R => "Rectangle", Key.E => "Ellipse", Key.P => "Pen", Key.H => "Highlight", Key.T => "Text", Key.M => "Mosaic", _ => null };
        if (key != null) { ToolClick(new Button { Tag = key }, new()); e.Handled = true; }
    }
    void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element is Button || FindButton(element)) return;
        if (e.ClickCount == 2 && !compact) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
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
    void ReportError(Exception ex, string title) { SettingsStore.Log(ex); SetStatus(title); MessageBox.Show(this, ex.Message, I18n.T(title), MessageBoxButton.OK, MessageBoxImage.Warning); }
}

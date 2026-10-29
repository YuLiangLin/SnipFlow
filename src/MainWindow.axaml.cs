using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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
    DesktopShortcut? desktopShortcut;
    bool capturePending;
    /// <summary>The shortcut that currently starts a capture, normalized like the setting ("Ctrl + Alt + S").</summary>
    public string? ActiveHotkey => hotkeys?.ActiveGesture?.ToString()
        ?? (desktopShortcut is { Registered: true, IsCurrent: true, Gesture: { } desktopGesture } ? desktopGesture.ToString() : null);
    /// <summary>The active shortcut as shown to the user; a portal may describe its trigger in its own words.</summary>
    public string? ActiveHotkeyDisplay => hotkeys?.ActiveGesture is not null ? hotkeys.ActiveDescription ?? hotkeys.ActiveGesture.ToString() : ActiveHotkey;
    public string HotkeyError { get; private set; } = "";
    /// <summary>Raised after the global shortcut changed asynchronously (desktop portal, desktop keyboard settings).</summary>
    public event EventHandler? HotkeyStateChanged;
    bool busy;
    bool updating;
    bool selectingHistory;
    bool refreshingProperties;
    bool compact = true;
    bool modeInitialized;
    bool hadImage;
    Rect? editorBounds;
    Rect? normalBounds;
    readonly DispatcherTimer autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
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
    ArrowStylePanel? arrowStylePanel;
    Flyout? arrowStyleFlyout;
#if WINDOWS
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowEnabled(IntPtr hwnd);
    bool IsNativeWindowEnabled => TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero || IsWindowEnabled(handle);
#else
    // X11 windows have no disabled state; an open modal dialog is detected through OwnedWindows below.
    bool IsNativeWindowEnabled => true;
#endif
    bool CanStartCapture => !historyDragInProgress && IsEnabled && IsNativeWindowEnabled && !OwnedWindows.Any(window => window.IsDialog && window.IsVisible);
    public MainWindow()
    {
        InitializeComponent(); Editor.ArrowStyle = SettingsStore.Current.ArrowStyle;
        ToolClick(new Button { Tag = SettingsStore.Current.LastAnnotationTool.ToString() }, new()); Editor.SetColor(Color.FromRgb(99, 213, 197));
        foreach (var tool in Enum.GetValues<AnnotationTool>().Where(ToolAppearanceOptions.Supports))
            Editor.RestoreToolAppearance(tool, SettingsStore.Current.GetToolAppearance(tool));
        Editor.RestoreTextSize(SettingsStore.Current.TextSize);
        Editor.RestoreTextFontFamily(SettingsStore.Current.TextFontFamily);
        RefreshProperties();
        if (!PlatformInfo.IsWindows) RootBorder.CornerRadius = default;
        else RootBorder.CornerRadius = new CornerRadius(10);
        if (Application.Current?.TryGetResource("SnipFlowIcon", null, out var icon) == true && icon is WindowIcon windowIcon) Icon = windowIcon;
        WindowChrome.EnableTitleBarDrag(this, TitleBar);
        WindowChrome.EnableResizeBorder(this);
        InitializeHistoryDrag();
        InitializeHistoryLifecycle();
        InitializeInput();
        TaskbarIconService.Attach(this);
        Editor.Changed += (_, _) => { RefreshDocumentState(); QueueAutoSave(); };
        Editor.SelectionChanged += (_, _) => RefreshProperties();
        Updates.Changed += UpdateChanged;
        I18n.Changed += LanguageChanged;
        SettingsStore.Changed += SettingsChanged;
        autoSaveTimer.Tick += AutoSaveTick;
        Closing += WindowClosing;
        Closed += (_, _) => { isClosed = true; CloseColorPalette(); arrowStyleFlyout?.Hide(); autoSaveTimer.Stop(); Updates.Changed -= UpdateChanged; SettingsStore.Changed -= SettingsChanged; I18n.Changed -= LanguageChanged; ReleaseHotkey(); };
        ApplyWindowMode(true);
        // The native window exists from construction (WPF: SourceInitialized), even when SnipFlow starts in the tray.
        WindowAppearance.Apply(this); WindowBoundsService.Attach(this); UpdateMinimumWindowSize(); SetHotkey(SettingsStore.Current.Hotkey);
        RefreshDesktopShortcut();
        SizeChanged += (_, _) => { UpdateMinimumWindowSize(); RememberNormalBounds(); FitArrowStylePanel(); };
        PositionChanged += (_, _) => { UpdateMinimumWindowSize(); RememberNormalBounds(); };
        // The first Show centers the panel; later shows keep the position set by the user or the editor layout.
        Opened += (_, _) => WindowStartupLocation = WindowStartupLocation.Manual;
        Dispatcher.UIThread.Post(() => { RefreshHistory(); RefreshDocumentState(); if (Updates.ReadyToRestart) RefreshUpdateStatus(); });
    }

    void InitializeInput()
    {
        // WPF PreviewKeyDown: shortcuts run before the focused control sees the key.
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        TextSizeBox.AddHandler(KeyDownEvent, TextSizeKeyDown, RoutingStrategies.Tunnel);
        WidthSlider.AddHandler(Thumb.DragStartedEvent, PropertyDragStarted);
        WidthSlider.AddHandler(Thumb.DragCompletedEvent, PropertyDragCompleted);
        AddHandler(DragDrop.DragOverEvent, FileDragOver);
        AddHandler(DragDrop.DropEvent, ImageDropped);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) { UpdateMinimumWindowSize(); RefreshUpdateControls(); }
    }

    void GlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (FocusedHotkeyInput() is { } input && hotkeys?.ActiveGesture is { } gesture)
        { input.SetGesture(gesture); return; }
        if (CanStartCapture) _ = StartCaptureAsync(false);
    }

    /// <summary>
    /// A capture request from the desktop's own keyboard shortcut (<c>SnipFlow --capture</c> forwarded over
    /// the pipe). It behaves like the in-process global shortcut.
    /// </summary>
    public void DesktopShortcutPressed()
    {
        if (FocusedHotkeyInput() is { } input && desktopShortcut is { Registered: true, Gesture: { } gesture })
        { input.SetGesture(gesture); return; }
        if (CanStartCapture) _ = StartCaptureAsync(false);
    }

    static HotkeyInputBox? FocusedHotkeyInput()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .Where(window => window.IsActive).Select(window => window.FocusManager?.GetFocusedElement())
            .OfType<HotkeyInputBox>().FirstOrDefault();

    public bool SetHotkey(string shortcut)
    {
        if (!HotkeyGesture.TryParse(shortcut, out var gesture))
        { HotkeyError = I18n.T("請使用至少兩個修飾鍵加字母／數字，或 F1–F11。"); RefreshHotkeyHint(); return false; }
        try
        {
            if (hotkeys == null) { hotkeys = new GlobalHotkeyService(); hotkeys.Pressed += GlobalHotkeyPressed; hotkeys.StateChanged += GlobalHotkeyStateChanged; }
            if (!hotkeys.TryRegister(gesture, out var error))
            {
                if (error == GlobalHotkeyService.ErrorPending)
                {
                    // The desktop portal confirms the shortcut asynchronously, possibly after asking the user.
                    HotkeyError = ""; RefreshHotkeyHint(); SetStatus("正在等待桌面環境確認全域快捷鍵…"); return true;
                }
                if (error == GlobalHotkeyService.ErrorUnsupported)
                {
                    // The preference is still stored; the desktop's keyboard settings start the capture.
                    HotkeyError = ""; RefreshHotkeyHint(); RefreshDesktopShortcut(); return true;
                }
                var errorKey = error == GlobalHotkeyService.ErrorInUse ? "{0} 已被其他程式使用；可透過按鈕或系統匣截圖。"
                    : PlatformInfo.IsWindows ? "{0} 無法啟用（Windows 錯誤 {1}）。" : "{0} 無法啟用（錯誤 {1}）。";
                HotkeyError = I18n.F(errorKey, gesture.ToString(), error);
                SetStatus(errorKey, gesture.ToString(), error);
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

    void GlobalHotkeyStateChanged(object? sender, EventArgs e)
    {
        if (isClosed || hotkeys is not { } service) return;
        if (service.ActiveGesture is not null)
        {
            HotkeyError = "";
            SetStatus("全域截圖快捷鍵：{0}", ActiveHotkeyDisplay);
        }
        else if (service.IsUnsupported) { HotkeyError = ""; RefreshDesktopShortcut(reportStatus: true); }
        else if (service.PendingError.Length > 0) { HotkeyError = service.PendingError; SetStatus("全域快捷鍵無法啟用。"); }
        RefreshHotkeyHint();
        HotkeyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads SnipFlow's shortcut in the desktop keyboard settings (GNOME), used when no in-process shortcut exists.</summary>
    /// <param name="reportStatus">Replaces the status bar text, for example the portal's pending message, with the resulting state.</param>
    public async void RefreshDesktopShortcut(bool reportStatus = false)
    {
        try
        {
            if (GlobalHotkeyService.CanRegisterDesktopShortcut)
                desktopShortcut = await GlobalHotkeyService.ReadDesktopShortcutAsync();
            if (isClosed) return;
            if (reportStatus)
            {
                if (ActiveHotkeyDisplay is { } display) SetStatus("全域截圖快捷鍵：{0}", display);
                else SetStatus("全域快捷鍵未啟用");
            }
            RefreshHotkeyHint();
            HotkeyStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
    }

    /// <summary>
    /// True when Settings should offer to add the shortcut to the desktop's keyboard settings, or to update
    /// it because it runs another SnipFlow or another key than the setting.
    /// </summary>
    public bool CanRegisterDesktopShortcut => GlobalHotkeyService.CanRegisterDesktopShortcut && hotkeys?.ActiveGesture is null && hotkeys?.IsPending != true
        && !(desktopShortcut is { Registered: true, IsCurrent: true, Gesture: { } bound }
            && HotkeyGesture.TryParse(SettingsStore.Current.Hotkey, out var wanted) && bound == wanted);

    /// <summary>Adds or updates the GNOME custom shortcut that runs <c>SnipFlow --capture</c>. Only called from the Settings button.</summary>
    public async Task RegisterDesktopShortcutAsync()
    {
        if (!HotkeyGesture.TryParse(SettingsStore.Current.Hotkey, out var gesture)) throw new InvalidOperationException(I18n.T("請使用至少兩個修飾鍵加字母／數字，或 F1–F11。"));
        await GlobalHotkeyService.RegisterDesktopShortcutAsync(gesture);
        desktopShortcut = await GlobalHotkeyService.ReadDesktopShortcutAsync();
        if (isClosed) return;
        RefreshHotkeyHint();
        SetStatus("全域截圖快捷鍵：{0}", ActiveHotkeyDisplay ?? gesture.ToString());
        HotkeyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How the shortcut works on this desktop, for Settings; empty when nothing needs explaining.</summary>
    public string HotkeyNote
    {
        get
        {
            if (hotkeys is { IsPending: true }) return I18n.T("正在等待桌面環境確認全域快捷鍵…");
            if (hotkeys?.ActiveGesture is not null)
                return hotkeys.ActiveDescription is { } trigger ? I18n.F("桌面環境設定的觸發鍵：{0}", trigger) : "";
            if (desktopShortcut is { Registered: true } shortcut)
                return shortcut.IsCurrent ? I18n.T("由 GNOME 鍵盤快捷鍵啟動截圖。")
                    : I18n.T("GNOME 鍵盤快捷鍵指向其他位置的 SnipFlow，請按下方按鈕更新。");
            if (hotkeys?.IsUnsupported == true)
                return GlobalHotkeyService.CanRegisterDesktopShortcut
                    ? I18n.T("此桌面不允許程式直接註冊全域快捷鍵。可按下方按鈕，加入 GNOME 鍵盤快捷鍵。")
                    : I18n.F("此桌面不允許程式直接註冊全域快捷鍵。請在桌面環境的鍵盤設定中，新增執行「{0}」的自訂快捷鍵。", DesktopCaptureCommand);
            return "";
        }
    }

    /// <summary>The command a desktop keyboard shortcut runs to start a capture.</summary>
    public static string DesktopCaptureCommand
    {
        get
        {
            try { return $"\"{StartupService.ResolveLauncher()}\" --capture"; }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException) { return "snipflow --capture"; }
        }
    }

    void RefreshHotkeyHint() => HotkeyHint.Text = CompactHotkeyHint.Text = ActiveHotkeyDisplay ?? I18n.T("全域快捷鍵未啟用");
    public void ReleaseHotkey()
    {
        hotkeys?.Dispose(); hotkeys = null; RefreshHotkeyHint();
    }
    public Task StartCaptureAsync(bool scrolling) => StartCaptureCoreAsync(scrolling ? CaptureMode.Window : CaptureMode.Region, scrolling);
    public Task StartCaptureAsync(CaptureMode mode) => StartCaptureCoreAsync(mode, scrolling: false);
    public Task StartScrollCaptureAsync(CaptureMode selectionMode = CaptureMode.Window) => StartCaptureCoreAsync(selectionMode, scrolling: true);
    public Task StartCodeCaptureAsync(CaptureMode selectionMode = CaptureMode.Window) => StartCaptureCoreAsync(selectionMode, scrolling: true, workflow: ScrollCaptureWorkflow.Code);
    async Task StartCaptureCoreAsync(CaptureMode mode, bool scrolling, ScrollCaptureWorkflow workflow = ScrollCaptureWorkflow.Quick)
    {
        if (busy || capturePending || !CanStartCapture) return;
        if (scrolling && mode is not CaptureMode.Window and not CaptureMode.Region)
            throw new ArgumentOutOfRangeException(nameof(mode));
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
            var result = scrolling && mode == CaptureMode.Window
                ? await ScreenshotService.CaptureWindowRegionAsync()
                : scrolling ? await ScreenshotService.CaptureRegionAsync() : await ScreenshotService.CaptureAsync(mode);
            if (result == null) { if (wasVisible) { Show(); Activate(); } return; }
            // Wayland cannot identify windows, so its window selection is a plain region without a target.
            if (scrolling && mode == CaptureMode.Window && ScreenshotService.SupportsWindowTargets && result.Target == null)
                throw new InvalidOperationException(I18n.T("選取的視窗資訊遺失，請重新選取。"));
            Bitmap image = result.Image;
            if (scrolling)
            {
                var window = new ScrollCaptureWindow(result.Bounds, result.Image, workflow, selectedTarget: result.Target);
                if (window.ShowModal() != true || window.Result == null) { if (wasVisible) { Show(); Activate(); } return; }
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
            recording.ShowModal();
            if (recording.ResultPath != null)
            {
                await AddRecordingHistoryAsync(recording.ResultPath);
                ApplyWindowMode(false);
                SetStatus("錄影已儲存：{0}", recording.ResultPath);
            }
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
        if (IsVideoPath(path)) { _ = OpenVideoEditorAsync(path); return; }
        if (Path.GetExtension(path).Equals(".snipflow", StringComparison.OrdinalIgnoreCase)) { OpenProject(path); return; }
        if (busy || capturePending || historyDragInProgress || !CanStartCapture || !PrepareToDiscard()) return;
        try { var image = HistoryStore.Read(path); LoadDocument(image); ApplyWindowMode(false); ImageInfo.Text = $"{image.PixelSize.Width:N0} × {image.PixelSize.Height:N0} px"; SetStatus("已開啟 {0}", Path.GetFileName(path)); RefreshDocumentState(); }
        catch (Exception ex) { ReportError(ex, "圖片無法開啟"); }
    }
    /// <summary>Opens files passed on the command line or from another launch: one file opens, several images form a collage.</summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        ShowMainWindow();
        OpenDroppedFiles(paths);
    }
    void LoadDocument(Bitmap image)
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
        if (isClosed) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => SettingsChanged(sender, e)); return; }
        var finishPendingSave = autoSaveEnabled && !SettingsStore.Current.AutoSaveCaptures;
        autoSaveEnabled = SettingsStore.Current.AutoSaveCaptures;
        if (finishPendingSave && Editor.HasImage && (autoSavePath != null || autoSaveWrite != null)
            && (autoSaveInProgress || Editor.Revision != autoSavedRevision))
            SaveAutomaticallyBeforeDiscard(finishPending: true);
        var directory = SettingsStore.Current.CaptureSaveDirectory;
        if (!string.Equals(autoSaveDirectory, directory, PlatformInfo.PathComparison))
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
        EmptyState.IsVisible = !Editor.HasImage;
        CopyButton.IsEnabled = SaveButton.IsEnabled = OcrButton.IsEnabled = Editor.HasImage && !busy;
        ImageTransformButton.IsEnabled = Editor.HasImage && !busy && !capturePending;
        Editor.IsEnabled = !busy;
        CollageBar.IsVisible = Editor.IsCollage;
        AddImagesButton.IsEnabled = PasteImagesButton.IsEnabled = ProjectButton.IsEnabled = !busy;
        ImageInfo.Text = Editor.HasImage ? $"{Editor.PixelWidth:N0} × {Editor.PixelHeight:N0} px" : "";
        UndoButton.IsEnabled = Editor.CanUndo && !busy && !capturePending;
        RedoButton.IsEnabled = Editor.CanRedo && !busy && !capturePending;
        Workspace.IsEnabled = CompactPanel.IsEnabled = HeaderCapture.IsEnabled = !busy && !capturePending;
        HistoryClearButton.IsEnabled = !busy && !capturePending && !historyDragInProgress && HistoryList.ItemCount > 0;
        Title = Editor.HasEdits ? "SnipFlow *" : "SnipFlow";
        if (Editor.HasImage && !hadImage) ApplyWindowMode(false);
        hadImage = Editor.HasImage;
        RefreshProperties();
        RefreshUpdateControls();
    }

    /// <summary>The window's bounds in DIPs; Position is in physical pixels.</summary>
    Rect CurrentBounds()
    {
        var scale = WindowBoundsService.GetScaling(this);
        var size = ClientSize;
        return new Rect(Position.X / scale, Position.Y / scale, size.Width, size.Height);
    }

    void RememberNormalBounds()
    {
        // Avalonia has no RestoreBounds; keep the last normal-state editor bounds ourselves.
        if (modeInitialized && !compact && WindowState == WindowState.Normal && IsVisible) normalBounds = CurrentBounds();
    }

    void ApplyWindowMode(bool useCompact)
    {
        if (Workspace == null) return;
        if (modeInitialized && compact == useCompact) return;
        modeInitialized = true;
        if (useCompact && !compact)
            editorBounds = WindowState == WindowState.Normal ? CurrentBounds() : normalBounds;
        compact = useCompact;
        Workspace.IsVisible = HeaderCapture.IsVisible = CompactButton.IsVisible = MaximizeButton.IsVisible = !compact;
        CompactPanel.IsVisible = compact;
        if (compact)
        {
            WindowState = WindowState.Normal;
            MinWidth = 420; Width = 420;
            CanResize = false; CanMaximize = false;
        }
        else
        {
            CanResize = true; CanMaximize = true;
            UpdateMinimumWindowSize();
            var area = WindowBoundsService.GetWorkArea(this);
            var bounds = editorBounds ?? new Rect(area.Left + Math.Max(0, (area.Width - 1240) / 2), area.Top + Math.Max(0, (area.Height - 810) / 2), Math.Min(1240, area.Width), Math.Min(810, area.Height));
            if (WindowState == WindowState.Normal)
            {
                Width = Math.Clamp(bounds.Width, MinWidth, area.Width); Height = Math.Clamp(bounds.Height, MinHeight, area.Height);
                var left = Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - Width));
                var top = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
                var scale = WindowBoundsService.GetScaling(this);
                Position = new PixelPoint((int)Math.Round(left * scale), (int)Math.Round(top * scale));
                WindowStartupLocation = WindowStartupLocation.Manual;
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
    void CompactClick(object? sender, RoutedEventArgs e) { Editor.CommitTextEdit(); ApplyWindowMode(true); }
    void ExpandClick(object? sender, RoutedEventArgs e) => ApplyWindowMode(false);
    void LanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        TextFontSelector.RefreshLanguage();
        RefreshDocumentState();
        RefreshHistory();
        RefreshHotkeyHint();
        StatusText.Text = showingUpdateStatus ? Updates.Status : I18n.F(statusKey, statusValues);
        ToolTip.SetTip(StatusText, StatusText.Text);
    });
    void SetStatus(string key, params object?[] values)
    {
        showingUpdateStatus = false; statusKey = key; statusValues = values; StatusText.Text = I18n.F(key, values); ToolTip.SetTip(StatusText, StatusText.Text);
    }
    void UpdateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (isClosed) return;
        RefreshUpdateStatus();
    });
    void RefreshUpdateStatus()
    {
        showingUpdateStatus = true;
        StatusText.Text = Updates.Status; ToolTip.SetTip(StatusText, StatusText.Text);
        RefreshUpdateControls();
    }
    void RefreshUpdateControls()
    {
        if (UpdateBanner == null) return;
        bool ready = Updates.ReadyToRestart;
        bool showCompactAction = ready && compact;
        UpdateBanner.IsVisible = ready && !compact;
        CompactUpdateButton.IsVisible = showCompactAction;
        EditorUpdateButton.IsEnabled = CompactUpdateButton.IsEnabled = ready && !Updates.IsBusy && !updating && !busy && !capturePending && !historyDragInProgress;
        RootGrid.RowDefinitions[2].Height = new GridLength(showCompactAction ? 42 : 26);
        if (compact && modeInitialized)
        {
            // Keep the capture controls at their normal size when the update action appears.
            double height = showCompactAction ? 280 : 264;
            MinHeight = height;
            if (Height != height) Height = height;
            if (showCompactAction && WindowState == WindowState.Normal && IsVisible)
            {
                var area = WindowBoundsService.GetWorkAreaPixels(this);
                var physicalHeight = (int)Math.Ceiling(height * WindowBoundsService.GetScaling(this));
                int top = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - physicalHeight));
                if (Position.Y != top) Position = new PixelPoint(Position.X, top);
            }
        }
    }
    void RefreshProperties()
    {
        if (Editor == null || ColorButtons == null || refreshingProperties) return;
        refreshingProperties = true;
        try
        {
            CropImageButton.IsEnabled = !busy && !capturePending && !Editor.IsImageCropping
                && Editor.SelectionCount == 1 && Editor.SelectedImageCount == 1;
            var shownTool = Editor.SelectedTool ?? activeTool;
            ToolLabel.Text = ToolName(shownTool);
            TextProperties.IsVisible = shownTool == AnnotationTool.Text;
            WidthProperties.IsVisible = shownTool is not (AnnotationTool.Text or AnnotationTool.Image);
            ArrowStyleButton.IsVisible = shownTool == AnnotationTool.Arrow && !Editor.IsImageCropping;
            ArrowStyleButton.IsEnabled = !busy && !capturePending && !Editor.IsImageCropping;
            if (!ArrowStyleButton.IsVisible || !ArrowStyleButton.IsEnabled) arrowStyleFlyout?.Hide();
            if (arrowStylePanel is not null)
            {
                arrowStylePanel.Value = Editor.SelectedArrowStyle ?? Editor.ArrowStyle;
                arrowStylePanel.PreviewColor = Editor.SelectedColor ?? Editor.DrawingColor;
                arrowStylePanel.PreviewWidth = Editor.SelectedStrokeWidth ?? Editor.StrokeWidth;
            }
            ColorButtons.IsVisible = shownTool is not (AnnotationTool.Image or AnnotationTool.Mosaic);
            WidthSlider.Value = Editor.SelectedStrokeWidth ?? Editor.StrokeWidth;
            if (!TextSizeBox.IsKeyboardFocusWithin) TextSizeBox.Text = (Editor.SelectedTextSize ?? Editor.TextSize).ToString("0.#", CultureInfo.CurrentCulture);
            TextFontSelector.Value = Editor.SelectedTextFontFamily ?? Editor.TextFontFamily;
            var color = Editor.SelectedColor ?? Editor.DrawingColor;
            foreach (var button in ColorButtons.Children.OfType<Button>())
            {
                if (button.Tag is not string hex) continue;
                bool selected = Color.Parse(hex) == color;
                button.BorderBrush = ThemeService.Brush(selected ? "Accent" : "Muted");
                button.BorderThickness = new Thickness(selected ? 2 : 1);
            }
            ToolTip.SetTip(MoreColorsButton, I18n.T("更多顏色") + " · " + ColorPaletteView.ToHex(color));
        }
        finally { refreshingProperties = false; }
    }
    static string ToolKey(AnnotationTool tool) => tool switch { AnnotationTool.Select => "選取／移動", AnnotationTool.Arrow => "箭頭", AnnotationTool.Rectangle => "矩形", AnnotationTool.Ellipse => "橢圓", AnnotationTool.Pen => "畫筆", AnnotationTool.Highlight => "螢光筆", AnnotationTool.Text => "文字", AnnotationTool.Image => "圖片", _ => "馬賽克" };
    static string ToolName(AnnotationTool tool) => I18n.T(ToolKey(tool));
    Task CopyImageAsync(Bitmap image)
        => ClipboardImageService.SetImageAsync(Clipboard ?? throw new InvalidOperationException(I18n.T("剪貼簿目前無法使用。")), image);
    async void CaptureClick(object? sender, RoutedEventArgs e) => await StartCaptureAsync(false);
    async void ScrollClick(object? sender, RoutedEventArgs e) => await StartCaptureAsync(true);
    async void RecordClick(object? sender, RoutedEventArgs e) => await StartRecordingAsync();
    async void CopyClick(object? sender, RoutedEventArgs e)
    {
        if (!Editor.HasImage || busy) return;
        try { await CopyImageAsync(Editor.ExportImage()); SetStatus("已複製含標註圖片"); }
        catch (Exception ex) { ReportError(ex, "複製失敗"); }
    }
    bool SaveCurrent()
    {
        if (!Editor.HasImage) return false;
        Editor.CommitTextEdit();
        var initialDirectory = Directory.Exists(SettingsStore.Current.CaptureSaveDirectory) ? SettingsStore.Current.CaptureSaveDirectory : null;
        var fileName = FileDialogs.SaveFile(this, null, I18n.T("PNG 圖片 (*.png)|*.png"), $"SnipFlow_{DateTime.Now:yyyyMMdd_HHmmss}.png", ".png", initialDirectory);
        if (fileName == null) return false;
        try
        {
            FinishAutoSaveWrite();
            var isAutoSaveFile = string.Equals(Path.GetFullPath(fileName), autoSavePath, PlatformInfo.PathComparison);
            if (isAutoSaveFile) { autoSaveSequence++; autoSavedRevision = -1; }
            var image = Editor.ExportImage(); CaptureFileStore.Save(image, fileName);
            if (!Editor.IsCollage)
            {
                if (isAutoSaveFile) autoSavedRevision = Editor.Revision;
                Editor.MarkSaved();
            }
            HistoryStore.Add(image); RefreshHistory(); SetStatus("已儲存 {0}", Path.GetFileName(fileName));
            return true;
        }
        catch (Exception ex) { ReportError(ex, "儲存失敗"); return false; }
    }
    void SaveClick(object? sender, RoutedEventArgs e) => SaveCurrent();
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
    async void OpenClick(object? sender, RoutedEventArgs e)
    {
        if (!CanChangeHistory) return;
        var fileName = FileDialogs.OpenFile(this, null, I18n.T("圖片、影片與 SnipFlow 專案|*.snipflow;*.snipvideo;*.mp4;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|影片與影片專案|*.mp4;*.snipvideo|所有檔案|*.*"));
        if (fileName != null) await OpenFileAsync(fileName);
    }
    void FileDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled || !e.DataTransfer.Contains(DataFormat.File)) return;
        e.DragEffects = DragDropEffects.Copy;
    }
    void ImageDropped(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var files = e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
        if (files is not { Length: > 0 }) return;
        e.Handled = true;
        OpenDroppedFiles(files);
    }
    async void OpenDroppedFiles(IReadOnlyList<string> files)
    {
        if (files.Any(IsVideoPath))
        {
            if (files.Count == 1) await OpenVideoEditorAsync(files[0]);
            else SetStatus("請一次開啟一部影片；影片不能加入合圖。");
            return;
        }
        if (files.Count == 1 && Path.GetExtension(files[0]).Equals(".snipflow", StringComparison.OrdinalIgnoreCase)) OpenProject(files[0]);
        else if (Editor.IsCollage || files.Count > 1) AddImageFiles(files);
        else OpenImage(files[0]);
    }
    void ToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<AnnotationTool>(tag, out var tool)
            || (tool != AnnotationTool.Select && !ToolAppearanceOptions.Supports(tool))) return;
        activeTool = tool;
        Editor.SetTool(tool);
        foreach (var candidate in ToolButtons.Children.OfType<Button>().Where(b => b.Tag is string))
        {
            candidate.Background = candidate.Tag?.ToString() == tag ? ThemeService.Brush("Selected") : Brushes.Transparent;
            candidate.Foreground = ThemeService.Brush("Ink");
        }
        RefreshProperties();
        SetStatus(ToolKey(tool));
        var previous = SettingsStore.Current.LastAnnotationTool;
        if (previous == tool) return;
        SettingsStore.Current.LastAnnotationTool = tool;
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SettingsStore.Current.LastAnnotationTool = previous;
            SettingsStore.Log(ex);
            SetStatus("工具已切換，但偏好設定未儲存。");
        }
    }
    void ColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value }) ApplyAnnotationColor(Color.Parse(value));
    }
    void StrokeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (Editor == null || refreshingProperties || busy || capturePending || Editor.IsImageCropping) return;
        var appearanceTool = Editor.AppearanceTool;
        Editor.StrokeWidth = e.NewValue;
        if (ToolAppearanceOptions.Supports(appearanceTool)) SaveToolAppearancePreferences(appearanceTool);
    }

    void SaveToolAppearancePreferences(AnnotationTool tool, List<string>? previousRecentColors = null)
    {
        var previous = SettingsStore.Current.GetToolAppearance(tool);
        var appearance = Editor.GetToolAppearance(tool);
        if (previousRecentColors is null && previous == appearance) return;
        SettingsStore.Current.SetToolAppearance(tool, appearance);
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SettingsStore.Current.SetToolAppearance(tool, previous);
            if (previousRecentColors is not null) SettingsStore.Current.RecentColors = previousRecentColors;
            SettingsStore.Log(ex);
            SetStatus("工具顏色與粗細已套用，但偏好設定未儲存。");
        }
    }
    void ArrowStyleClick(object? sender, RoutedEventArgs e)
    {
        if (Editor == null || refreshingProperties || busy || capturePending || Editor.IsImageCropping) return;
        if ((Editor.SelectedTool ?? activeTool) != AnnotationTool.Arrow) return;
        if (arrowStyleFlyout?.IsOpen == true) { arrowStyleFlyout.Hide(); return; }
        CloseColorPalette();
        Editor.EndPropertyEdit();
        if (arrowStylePanel is null)
        {
            arrowStylePanel = new ArrowStylePanel();
            arrowStylePanel.ValueChanged += ArrowStyleChanged;
            arrowStyleFlyout = new Flyout { Content = arrowStylePanel, Placement = PlacementMode.Bottom, VerticalOffset = 6 };
            if (Application.Current is { } app && app.TryGetResource(typeof(FlyoutPresenter), app.ActualThemeVariant, out var resource)
                && resource is Avalonia.Styling.ControlTheme theme)
            {
                var presenterTheme = new Avalonia.Styling.ControlTheme { TargetType = typeof(FlyoutPresenter), BasedOn = theme };
                presenterTheme.Setters.Add(new Avalonia.Styling.Setter(PaddingProperty, new Thickness(12)));
                presenterTheme.Setters.Add(new Avalonia.Styling.Setter(MaxWidthProperty, 380d));
                arrowStyleFlyout.FlyoutPresenterTheme = presenterTheme;
            }
        }
        FitArrowStylePanel();
        RefreshProperties();
        arrowStyleFlyout!.ShowAt(ArrowStyleButton);
    }
    void FitArrowStylePanel()
    {
        if (arrowStylePanel is null) return;
        arrowStylePanel.Width = Math.Min(340, Math.Max(1, Bounds.Width - 64));
        arrowStylePanel.MaxHeight = Math.Min(500, Math.Max(1, Bounds.Height - 64));
    }
    void ArrowStyleChanged(object? sender, ArrowStyleChangedEventArgs e)
    {
        if (Editor == null || refreshingProperties || busy || capturePending || Editor.IsImageCropping) return;
        if ((Editor.SelectedTool ?? activeTool) != AnnotationTool.Arrow) return;
        if (e.DirectionPreset is { } doubleEnded) Editor.DoubleEndedArrow = doubleEnded;
        else Editor.UpdateArrowStyle(e.Previous, e.Value);
        var previous = SettingsStore.Current.ArrowStyle;
        if (previous == Editor.ArrowStyle) return;
        SettingsStore.Current.ArrowStyle = Editor.ArrowStyle;
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SettingsStore.Current.ArrowStyle = previous;
            SettingsStore.Log(ex);
            SetStatus("箭頭樣式已套用，但偏好設定未儲存。");
        }
    }
    void PropertyDragStarted(object? sender, VectorEventArgs e) => Editor.BeginPropertyEdit();
    void PropertyDragCompleted(object? sender, VectorEventArgs e) => Editor.EndPropertyEdit();
    void TextSizeChanged(object? sender, RoutedEventArgs e) => ApplyTextSize();
    void TextFontChanged(object? sender, EventArgs e)
    {
        if (refreshingProperties || Editor == null || busy || capturePending || Editor.IsImageCropping
            || Editor.AppearanceTool != AnnotationTool.Text) return;
        Editor.TextFontFamily = TextFontSelector.Value;
        var previous = SettingsStore.Current.TextFontFamily;
        var font = Editor.TextFontFamily;
        if (previous == font) return;
        SettingsStore.Current.TextFontFamily = font;
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SettingsStore.Current.TextFontFamily = previous;
            SettingsStore.Log(ex);
            SetStatus("文字字型已套用，但偏好設定未儲存。");
        }
    }
    void TextSizeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyTextSize(); Editor.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { TextSizeBox.Text = (Editor.SelectedTextSize ?? Editor.TextSize).ToString("0.#", CultureInfo.CurrentCulture); Editor.Focus(); e.Handled = true; }
    }
    void ApplyTextSize()
    {
        if (refreshingProperties || Editor == null || busy || capturePending || Editor.IsImageCropping) return;
        var current = Editor.SelectedTextSize ?? Editor.TextSize;
        if (TryGetTextSizeChange(TextSizeBox.Text, current, CultureInfo.CurrentCulture, out var size))
        {
            Editor.TextSize = size;
            SaveTextSizePreference();
        }
        TextSizeBox.Text = (Editor.SelectedTextSize ?? Editor.TextSize).ToString("0.#", CultureInfo.CurrentCulture);
    }
    static bool TryGetTextSizeChange(string? input, double current, CultureInfo culture, out double normalized)
    {
        normalized = current;
        // The field displays one decimal place. An untouched value must not round an existing
        // object's font size or turn its size into the default merely because focus moved away.
        if (string.Equals(input, current.ToString("0.#", culture), StringComparison.Ordinal)
            || !double.TryParse(input, NumberStyles.Float, culture, out var size) || !double.IsFinite(size)) return false;
        size = Math.Clamp(size, 12, 240);
        if (size == current) return false;
        normalized = size;
        return true;
    }
    void SaveTextSizePreference()
    {
        var previous = SettingsStore.Current.TextSize;
        if (previous == Editor.TextSize) return;
        SettingsStore.Current.TextSize = Editor.TextSize;
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SettingsStore.Current.TextSize = previous;
            SettingsStore.Log(ex);
            SetStatus("文字字級已套用，但偏好設定未儲存。");
        }
    }
    void UndoClick(object? sender, RoutedEventArgs e) { if (!busy && !capturePending) Editor.Undo(); }
    void RedoClick(object? sender, RoutedEventArgs e) { if (!busy && !capturePending) Editor.Redo(); }
    async void HistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!selectingHistory && CanChangeHistory && HistoryList.SelectedItem is HistoryItem item)
        {
            if (item.IsRecording) await OpenVideoEditorAsync(item.Path);
            else if (!Editor.IsCollage) OpenImage(item.Path);
        }
    }
    async void OcrClick(object? sender, RoutedEventArgs e)
    {
        if (!Editor.HasImage || busy) return;
        busy = true; RefreshDocumentState(); SetStatus("正在本機辨識文字…");
        try
        {
            var text = await OcrService.RecognizeAsync(Editor.ExportImage());
            new TextResultWindow(text).ShowModal(this); SetStatus(string.IsNullOrWhiteSpace(text) ? "沒有辨識出文字。" : "文字辨識完成。");
        }
        catch (Exception ex) { ReportError(ex, "文字辨識未完成"); }
        finally { busy = false; RefreshDocumentState(); }
    }
    void SettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();
    /// <summary>Shows Settings modally; <paramref name="showUpdates"/> opens the update page.</summary>
    public void OpenSettings(bool showUpdates = false)
    {
        if (OwnedWindows.OfType<SettingsWindow>().FirstOrDefault() is { } open) { open.Activate(); return; }
        ShowMainWindow();
        new SettingsWindow(this, showUpdates).ShowModal(this); RefreshHotkeyHint(); RefreshHistory();
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
    async void ApplyUpdateClick(object? sender, RoutedEventArgs e)
    {
        try { await UpdateNowAsync(); } catch (Exception ex) { ReportError(ex, "更新尚未套用"); }
    }
    void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (busy || capturePending || historyDragInProgress) return;
        var modifiers = e.KeyModifiers;
        if (arrowStyleFlyout?.IsOpen == true) return;
        if (IsFontPickerInput(TextFontSelector.IsChoosingFont, modifiers)) return;
        if (Editor.IsImageCropping)
        {
            if (modifiers == KeyModifiers.Control && e.Key == Key.S)
            { Editor.CommitImageCrop(); SaveDocument(); e.Handled = true; }
            else if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.S)
            { Editor.CommitImageCrop(); SaveProjectCurrent(saveAs: true); e.Handled = true; }
            else if (modifiers == KeyModifiers.Control && e.Key == Key.C)
            { Editor.CommitImageCrop(); CopyClick(this, new()); e.Handled = true; }
            else if (modifiers == KeyModifiers.Control && e.Key == Key.Z)
            { Editor.Undo(); e.Handled = true; }
            else if ((modifiers == KeyModifiers.Control && e.Key == Key.Y)
                || (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Z))
            { Editor.Redo(); e.Handled = true; }
            else Editor.HandleImageCropKey(e);
            return;
        }
        if (Editor.IsTextEditing)
        {
            if (modifiers == KeyModifiers.Control && e.Key == Key.S) { Editor.CommitTextEdit(); SaveDocument(); e.Handled = true; }
            return;
        }
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            if (modifiers == KeyModifiers.Control && e.Key == Key.S)
            {
                if (TextSizeBox.IsKeyboardFocusWithin) ApplyTextSize();
                SaveDocument(); e.Handled = true;
            }
            return;
        }
        if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Z)
        { Editor.Redo(); e.Handled = true; return; }
        if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.S)
        { SaveProjectCurrent(saveAs: true); e.Handled = true; return; }
        if (modifiers == KeyModifiers.Control)
        {
            if (e.Key == Key.S) SaveDocument();
            else if (e.Key == Key.O) OpenClick(this, new());
            else if (e.Key == Key.C) CopyClick(this, new());
            else if (e.Key == Key.Z) Editor.Undo();
            else if (e.Key == Key.Y) Editor.Redo();
            else if (e.Key == Key.A) Editor.SelectAll();
            else if (e.Key == Key.D) RunObjectCommand(Editor.DuplicateSelection);
            else if (e.Key == Key.V) _ = PasteClipboardImagesAsync(allowTextFallback: true);
            else return;
            e.Handled = true; return;
        }
        if (modifiers != KeyModifiers.None) return;
        var key = e.Key switch { Key.V => "Select", Key.A => "Arrow", Key.R => "Rectangle", Key.E => "Ellipse", Key.P => "Pen", Key.H => "Highlight", Key.T => "Text", Key.M => "Mosaic", _ => null };
        if (key != null) { ToolClick(new Button { Tag = key }, new()); e.Handled = true; }
    }
    void MinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    static bool IsFontPickerInput(bool choosingFont, KeyModifiers modifiers) =>
        choosingFont && (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0;
    void MaximizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void HideClick(object? sender, RoutedEventArgs e) => Hide();
    void ExitClick(object? sender, RoutedEventArgs e) => ((App)Application.Current!).ExitRequested();
    void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Closing the window keeps SnipFlow in the tray; only an application shutdown closes it.
        if (!App.IsShuttingDown && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
        { e.Cancel = true; Hide(); }
    }
    void ReportError(Exception ex, string title) { SettingsStore.Log(ex); SetStatus(title); MessageBox.Show(this, ex.Message, I18n.T(title), MessageBoxButton.OK, MessageBoxImage.Warning); }
}

using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SnipFlow.Services;

namespace SnipFlow;

public sealed class SettingsWindow : DialogWindow
{
    readonly MainWindow shell;
    readonly List<(TextBlock Element, string Key)> localizedText = new();
    readonly List<(ContentControl Element, string Key)> localizedContent = new();
    readonly List<(Control Element, string Key)> localizedNames = new();
    readonly List<Button> navigation = new();
    readonly List<Control> panes = new();
    readonly ScrollViewer paneScroll = new();
    readonly ComboBox language = new() { Width = 184, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Right };
    readonly ComboBox theme = new() { Width = 184, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock themeNote = new();
    readonly CheckBox copy = new(), history = new(), startup = new(), autoSave = new(), autoCheck = new(), autoDownload = new();
    readonly TextBox captureFolder = new() { IsReadOnly = true, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
    readonly HotkeyInputBox shortcut = new(SettingsStore.Current.Hotkey);
    readonly TextBlock hotkeyStatus = new(), updateStatus = new(), updateDetail = new(), currentVersion = new(), latestVersion = new(), feedback = new();
    readonly Button check = new(), download = new(), restart = new(), releaseNotes = new(), desktopShortcut = new();
    readonly ProgressBar updateProgress = new() { Minimum = 0, Maximum = 100, Height = 4, BorderThickness = new Thickness(0) };
    readonly Border updateCard = new();
    readonly WrapPanel updateActions = new() { Margin = new Thickness(0, 14, 0, 0) };
    bool synchronizingLanguage, synchronizingOptions, updateActionRunning, hotkeyFailure, isClosed, feedbackIsError, desktopShortcutRunning;
    string? feedbackKey;

    public SettingsWindow(MainWindow shell, bool showUpdates = false)
    {
        this.shell = shell;
        Title = I18n.T("SnipFlow · 設定");
        Width = 760; Height = 620; MinWidth = 700; MinHeight = 550;
        ShowInTaskbar = false; Icon = shell.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None;
        CanResize = true;
        WindowAppearance.Apply(this);
        WindowChrome.EnableResizeBorder(this);

        var layout = new Grid { RowDefinitions = new RowDefinitions("48,*,Auto") };
        layout.Children.Add(BuildCaption());

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("178,1,*") };
        Grid.SetRow(body, 1); layout.Children.Add(body);

        var sidebar = new Grid { Background = ThemeService.Brush("Panel"), RowDefinitions = new RowDefinitions("*,Auto") };
        var navigationList = new StackPanel { Margin = new Thickness(12, 20, 12, 12) };
        AddNavigation(navigationList, "一般", IconKind.Settings, 0);
        AddNavigation(navigationList, "快捷鍵", IconKind.Keyboard, 1);
        AddNavigation(navigationList, "更新", IconKind.Sync, 2);
        sidebar.Children.Add(navigationList);
        var appVersion = new StackPanel { Margin = new Thickness(24, 12, 16, 24) };
        appVersion.Children.Add(new TextBlock { Text = "SnipFlow", FontWeight = FontWeight.SemiBold });
        appVersion.Children.Add(new TextBlock { Text = $"v{shell.Updates.CurrentVersion}", Foreground = ThemeService.Brush("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetRow(appVersion, 1); sidebar.Children.Add(appVersion); body.Children.Add(sidebar);
        var separator = new Border { Background = ThemeService.Brush("Line") };
        Grid.SetColumn(separator, 1); body.Children.Add(separator);

        var paneHost = new Grid();
        panes.Add(BuildGeneralPane()); panes.Add(BuildHotkeyPane()); panes.Add(BuildUpdatePane());
        foreach (var pane in panes) paneHost.Children.Add(pane);
        paneScroll.Content = paneHost; paneScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        paneScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; paneScroll.Padding = new Thickness(28, 24, 28, 20);
        Grid.SetColumn(paneScroll, 2); body.Children.Add(paneScroll);
        var footer = BuildFooter(); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = new Border { BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(1), Background = ThemeService.Brush("Bg"), Child = layout };

        shell.Updates.Changed += OnUpdateChanged;
        shell.HotkeyStateChanged += OnHotkeyStateChanged;
        I18n.Changed += OnLanguageChanged;
        SettingsStore.Changed += OnSettingsChanged;
        AttachOptionHandlers();
        Closed += (_, _) =>
        {
            isClosed = true;
            shell.Updates.Changed -= OnUpdateChanged;
            shell.HotkeyStateChanged -= OnHotkeyStateChanged;
            I18n.Changed -= OnLanguageChanged;
            SettingsStore.Changed -= OnSettingsChanged;
        };
        SelectPane(showUpdates ? 2 : 0); ApplyLanguage(); SynchronizeOptions();
    }

    Control BuildCaption()
    {
        var caption = new Grid { Background = ThemeService.Brush("Bg"), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        caption.Children.Add(BindText(new TextBlock
        {
            FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 0, 0), IsHitTestVisible = false
        }, "設定"));
        var close = new Button { Content = Icons.Create(IconKind.Dismiss, 11), Width = 42, Height = 34, Margin = new Thickness(0, 6, 7, 6) }.WithTheme("WindowControl");
        BindName(close, "關閉");
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); caption.Children.Add(close);
        WindowChrome.EnableTitleBarDrag(this, caption, allowMaximize: true);
        return caption;
    }

    void AddNavigation(Panel panel, string key, IconKind icon, int index)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Border { Width = 28, VerticalAlignment = VerticalAlignment.Center, Child = Icons.Create(icon, 16), HorizontalAlignment = HorizontalAlignment.Left });
        content.Children.Add(BindText(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, key));
        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(12, 8, 12, 8) }.WithTheme("NavigationButton");
        BindName(button, key); button.Click += (_, _) => SelectPane(index);
        navigation.Add(button); panel.Children.Add(button);
    }

    void SelectPane(int index)
    {
        for (int i = 0; i < panes.Count; i++)
        {
            panes[i].IsVisible = i == index;
            navigation[i].Background = i == index ? ThemeService.Brush("Selected") : Brushes.Transparent;
            navigation[i].FontWeight = i == index ? FontWeight.SemiBold : FontWeight.Normal;
            navigation[i].Foreground = ThemeService.Brush(i == index ? "Ink" : "Muted");
        }
        paneScroll.ScrollToHome();
    }

    Control BuildGeneralPane()
    {
        var pane = new StackPanel(); AddHeading(pane, "一般");
        language.Items.Add(new ComboBoxItem { Content = "繁體中文", Tag = "zh-TW" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.Items.Add(new ComboBoxItem { Content = "简体中文", Tag = "zh-CN" });
        SynchronizeLanguageChoice(); language.SelectionChanged += LanguageSelectionChanged;
        AddValueRow(pane, "介面語言", "切換後立即套用並儲存。", language);
        theme.Items.Add(BindContent(new ComboBoxItem { Tag = "dark" }, "暗色"));
        theme.Items.Add(BindContent(new ComboBoxItem { Tag = "light" }, "亮色"));
        theme.Items.Add(BindContent(new ComboBoxItem { Tag = "system" }, "跟隨系統"));
        AddValueRow(pane, "主題", PlatformInfo.IsWindows ? "套用 Windows 的應用程式色彩設定。" : "套用桌面環境的淺色／深色設定。", theme, themeNote);
        AddDivider(pane);
        AddToggleRow(pane, "自動複製", "截圖完成後複製到剪貼簿。", copy, SettingsStore.Current.CopyAfterCapture);
        AddToggleRow(pane, "自動儲存截圖", "截圖存為 PNG；標註修改會更新同一個檔案。", autoSave, SettingsStore.Current.AutoSaveCaptures);
        AddCaptureFolderControls(pane);
        AddDivider(pane);
        AddToggleRow(pane, "保留最近截圖", "最近截圖另行保留，最多 80 張；不影響截圖資料夾。", history, SettingsStore.Current.KeepHistory);
        AddToggleRow(pane, "登入時啟動", PlatformInfo.IsWindows ? "登入 Windows 後常駐系統匣。" : "登入桌面後在背景執行並常駐系統匣。", startup, SettingsStore.Current.StartWithWindows);
        return pane;
    }

    void AddCaptureFolderControls(Panel pane)
    {
        pane.Children.Add(BindText(new TextBlock { Margin = new Thickness(0, 10, 0, 9) }, "截圖資料夾"));
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        captureFolder.Text = SettingsStore.Current.CaptureSaveDirectory;
        ToolTip.SetTip(captureFolder, captureFolder.Text);
        captureFolder.Margin = new Thickness(0, 0, 10, 0);
        BindName(captureFolder, "截圖資料夾"); controls.Children.Add(captureFolder);
        var browse = BindContent(new Button { MinWidth = 70 }.WithTheme("QuietButton"), "選擇…");
        browse.Click += (_, _) => ChooseCaptureFolder();
        Grid.SetColumn(browse, 1); controls.Children.Add(browse); pane.Children.Add(controls);
        var open = BindContent(new Button { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 8, 0, 8), Margin = new Thickness(0, 3, 0, 4) }.WithTheme("QuietButton"), "開啟資料夾 ↗");
        open.Click += (_, _) => OpenCaptureFolder(); pane.Children.Add(open);
    }

    Control BuildHotkeyPane()
    {
        var pane = new StackPanel(); AddHeading(pane, "快捷鍵");
        pane.Children.Add(BindText(new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold }, "全域截圖快捷鍵"));
        var controls = new Grid { Margin = new Thickness(0, 14, 0, 14), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        shortcut.MinHeight = 38; shortcut.Padding = new Thickness(12, 9, 12, 9); shortcut.Margin = new Thickness(0, 0, 12, 0);
        BindName(shortcut, "全域截圖快捷鍵");
        controls.Children.Add(shortcut);
        var reset = BindContent(new Button { MinWidth = 70 }.WithTheme("QuietButton"), "預設");
        reset.Click += (_, _) => ApplyHotkey(HotkeyGesture.DefaultShortcut);
        Grid.SetColumn(reset, 1); controls.Children.Add(reset); pane.Children.Add(controls);
        pane.Children.Add(BindText(new TextBlock { Foreground = ThemeService.Brush("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19 }, "點選欄位後按下組合鍵。字母／數字須搭配 Ctrl、Alt、Shift 中至少兩個鍵；也可使用 F1–F11。"));
        pane.Children.Add(BindText(new TextBlock { Foreground = ThemeService.Brush("Muted"), FontSize = 12, Margin = new Thickness(0, 10, 0, 22) }, "輸入完整組合鍵後立即生效並儲存。"));
        hotkeyStatus.FontSize = 13; hotkeyStatus.TextWrapping = TextWrapping.Wrap; hotkeyStatus.LineHeight = 21;
        var statusBody = new StackPanel();
        statusBody.Children.Add(hotkeyStatus);
        // Linux desktops without an in-process global shortcut can run "SnipFlow --capture" from their own keyboard settings.
        desktopShortcut.Margin = new Thickness(0, 12, 0, 0); desktopShortcut.MinHeight = 34; desktopShortcut.HorizontalAlignment = HorizontalAlignment.Left;
        desktopShortcut.WithTheme("Primary");
        BindContent(desktopShortcut, "加入 GNOME 鍵盤快捷鍵");
        desktopShortcut.Click += async (_, _) => await RegisterDesktopShortcutAsync();
        statusBody.Children.Add(desktopShortcut);
        pane.Children.Add(new Border { Background = ThemeService.Brush("Raised"), CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 13, 16, 13), Child = statusBody });
        pane.Children.Add(BindText(new TextBlock { Foreground = ThemeService.Brush("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 16, 0, 0) }, "焦點在其他程式、最小化或隱藏至系統匣時也能截圖。"));
        return pane;
    }

    Control BuildUpdatePane()
    {
        var pane = new StackPanel(); AddHeading(pane, "更新");
        var versions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var installedLabel = BindText(new TextBlock { Foreground = ThemeService.Brush("Muted") }, "目前版本");
        var latestLabel = BindText(new TextBlock { Foreground = ThemeService.Brush("Muted"), Margin = new Thickness(0, 10, 0, 0) }, "最新版本");
        versions.Children.Add(installedLabel); Grid.SetRow(latestLabel, 1); versions.Children.Add(latestLabel);
        currentVersion.FontWeight = FontWeight.SemiBold; currentVersion.HorizontalAlignment = HorizontalAlignment.Right;
        latestVersion.HorizontalAlignment = HorizontalAlignment.Right; latestVersion.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetColumn(currentVersion, 1); versions.Children.Add(currentVersion);
        Grid.SetColumn(latestVersion, 1); Grid.SetRow(latestVersion, 1); versions.Children.Add(latestVersion);
        pane.Children.Add(new Border { Background = ThemeService.Brush("Raised"), CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 13, 16, 13), Margin = new Thickness(0, 0, 0, 16), Child = versions });

        var statusBody = new StackPanel();
        updateStatus.FontSize = 14; updateStatus.FontWeight = FontWeight.SemiBold; updateStatus.TextWrapping = TextWrapping.Wrap; updateStatus.LineHeight = 21;
        updateDetail.Foreground = ThemeService.Brush("Muted"); updateDetail.FontSize = 12; updateDetail.TextWrapping = TextWrapping.Wrap; updateDetail.LineHeight = 18; updateDetail.Margin = new Thickness(0, 6, 0, 0);
        statusBody.Children.Add(updateStatus); statusBody.Children.Add(updateDetail);
        updateProgress.Foreground = ThemeService.Brush("Accent"); updateProgress.Background = ThemeService.Brush("Line"); updateProgress.Margin = new Thickness(0, 12, 0, 0); statusBody.Children.Add(updateProgress);
        var actions = updateActions;
        ConfigureUpdateButton(check, "檢查更新", "QuietButton");
        ConfigureUpdateButton(download, "立即更新", "Primary");
        ConfigureUpdateButton(restart, "重新啟動並更新", "Primary");
        check.Click += async (_, _) => await PerformUpdateAsync(() => shell.Updates.CheckAsync(false));
        download.Click += async (_, _) => await PerformUpdateAsync(shell.UpdateNowAsync);
        restart.Click += async (_, _) => await PerformUpdateAsync(shell.UpdateNowAsync);
        actions.Children.Add(check); actions.Children.Add(download); actions.Children.Add(restart); statusBody.Children.Add(actions);
        updateCard.Background = ThemeService.Brush("Panel"); updateCard.BorderBrush = ThemeService.Brush("Line"); updateCard.BorderThickness = new Thickness(1); updateCard.CornerRadius = new CornerRadius(9); updateCard.Padding = new Thickness(16, 13, 16, 13); updateCard.Child = statusBody;
        pane.Children.Add(updateCard); AddDivider(pane);
        AddToggleRow(pane, "自動檢查更新", "啟動時與每 4 小時檢查。", autoCheck, SettingsStore.Current.AutoCheckUpdates);
        AddToggleRow(pane, "自動下載更新", "下載完成後，下次啟動時套用。", autoDownload, SettingsStore.Current.AutoDownloadUpdates);
        releaseNotes.WithTheme("QuietButton"); releaseNotes.Padding = new Thickness(0, 8, 0, 8); releaseNotes.HorizontalAlignment = HorizontalAlignment.Left; releaseNotes.Foreground = ThemeService.Brush("Muted");
        BindContent(releaseNotes, "版本紀錄 ↗");
        releaseNotes.Click += async (_, _) =>
        {
            try
            {
                if (Launcher is not { } launcher || !await launcher.LaunchUriAsync(new Uri(shell.Updates.ReleaseUrl)))
                    SetFeedback("無法開啟連結，請稍後重試。");
            }
            catch (Exception ex) { SettingsStore.Log(ex); SetFeedback("無法開啟連結，請稍後重試。"); }
        };
        pane.Children.Add(releaseNotes);
        return pane;
    }

    void ConfigureUpdateButton(Button button, string key, string style)
    {
        button.WithTheme(style); button.Margin = new Thickness(0, 0, 8, 2); button.MinHeight = 34;
        BindContent(button, key);
    }

    Control BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(24, 16, 24, 16), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        status.Children.Add(BindText(new TextBlock { FontSize = 12, Foreground = ThemeService.Brush("Muted"), TextWrapping = TextWrapping.Wrap }, "變更會立即套用並自動儲存。"));
        feedback.Foreground = ThemeService.Brush("Error"); feedback.FontSize = 12; feedback.TextWrapping = TextWrapping.Wrap; feedback.Margin = new Thickness(0, 5, 0, 0); feedback.IsVisible = false; status.Children.Add(feedback); footer.Children.Add(status);
        var close = BindContent(new Button { IsCancel = true, MinWidth = 84, Height = 36 }.WithTheme("Primary"), "關閉");
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); footer.Children.Add(close);
        return new Border { BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
    }

    void AttachOptionHandlers()
    {
        BindOption(copy, settings => settings.CopyAfterCapture, (settings, value) => settings.CopyAfterCapture = value);
        BindOption(history, settings => settings.KeepHistory, (settings, value) => settings.KeepHistory = value);
        BindOption(autoSave, settings => settings.AutoSaveCaptures, (settings, value) => settings.AutoSaveCaptures = value);
        BindOption(autoCheck, settings => settings.AutoCheckUpdates, (settings, value) => settings.AutoCheckUpdates = value);
        BindOption(autoDownload, settings => settings.AutoDownloadUpdates, (settings, value) => settings.AutoDownloadUpdates = value);
        startup.IsCheckedChanged += StartupChanged;
        theme.SelectionChanged += ThemeSelectionChanged;
        // TextChanged is raised asynchronously in Avalonia; a synchronous property change keeps the
        // synchronizingOptions guard effective.
        shortcut.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty && !synchronizingOptions && !isClosed) ApplyHotkey(shortcut.Shortcut);
        };
    }

    void BindOption(CheckBox box, Func<UserSettings, bool> read, Action<UserSettings, bool> apply)
    {
        void Changed(object? sender, RoutedEventArgs args)
        {
            if (synchronizingOptions || isClosed) return;
            var value = box.IsChecked == true;
            if (read(SettingsStore.Current) == value) return;
            var previous = SnapshotSettings();
            bool validatingFolder = box == autoSave && value;
            try
            {
                if (validatingFolder)
                {
                    SettingsStore.Current.CaptureSaveDirectory = ValidateCaptureFolder(SettingsStore.Current.CaptureSaveDirectory);
                    validatingFolder = false;
                }
                apply(SettingsStore.Current, value); SettingsStore.Save();
                SetFeedback("設定已自動儲存。", false);
            }
            catch (Exception ex)
            {
                RestoreSettings(previous); SettingsStore.Log(ex); SynchronizeOptions();
                SetFeedback(validatingFolder ? "截圖資料夾無法使用，請選擇可寫入的資料夾。" : "設定未儲存，請再試一次。");
            }
        }
        box.IsCheckedChanged += Changed;
    }

    void StartupChanged(object? sender, RoutedEventArgs args)
    {
        if (synchronizingOptions || isClosed) return;
        bool value = startup.IsChecked == true;
        if (SettingsStore.Current.StartWithWindows == value) return;
        var previous = SnapshotSettings();
        StartupRegistration? previousRun = null;
        bool runChanged = false;
        try
        {
            // Complete path validation before touching the existing login entry.
            var command = value ? StartupService.CreateCommand() : null;
            previousRun = StartupService.ReadRegistration();
            runChanged = true;
            StartupService.WriteCommand(command);
            SettingsStore.Current.StartWithWindows = value; SettingsStore.Save();
            SetFeedback("設定已自動儲存。", false);
        }
        catch (Exception ex)
        {
            RestoreSettings(previous); SettingsStore.Log(ex);
            bool rollbackFailed = false;
            if (runChanged && previousRun != null)
            {
                try { StartupService.Restore(previousRun); }
                catch (Exception rollbackError) { SettingsStore.Log(rollbackError); rollbackFailed = true; }
            }
            SynchronizeOptions();
            SetFeedback(rollbackFailed ? "登入啟動未完整還原，請重新設定。"
                : ex is FileNotFoundException ? "找不到可啟動的 SnipFlow 程式，登入啟動未變更。"
                : ex is InvalidOperationException ? "目前執行的是預覽程式，無法設定登入啟動。" : "登入啟動未儲存，原設定已保留。");
        }
    }

    void ApplyHotkey(string candidate)
    {
        if (synchronizingOptions || isClosed) return;
        SetFeedback(null);
        var previous = SnapshotSettings(); var previousActiveKey = shell.ActiveHotkey;
        if (!HotkeyGesture.TryParse(candidate, out var gesture))
        {
            SynchronizeOptions(); SetFeedback("請使用至少兩個修飾鍵加字母／數字，或 F1–F11。"); return;
        }
        var normalized = gesture.ToString();
        if (normalized == SettingsStore.Current.Hotkey && normalized == previousActiveKey)
        {
            hotkeyFailure = false; SynchronizeOptions(); RefreshHotkeyStatus();
            SetFeedback("設定已自動儲存。", false); return;
        }
        if (!shell.SetHotkey(normalized))
        {
            hotkeyFailure = true; SynchronizeOptions(); RefreshHotkeyStatus();
            SetFeedback("快捷鍵無法設定。"); return;
        }
        hotkeyFailure = false;
        try
        {
            SettingsStore.Current.Hotkey = normalized;
            if (previous.Hotkey != normalized) SettingsStore.Save();
            SynchronizeOptions(); RefreshHotkeyStatus(); SetFeedback("設定已自動儲存。", false);
        }
        catch (Exception ex)
        {
            RestoreSettings(previous); SettingsStore.Log(ex);
            bool rollbackFailed = false;
            try
            {
                if (previousActiveKey != null) rollbackFailed = !shell.SetHotkey(previousActiveKey);
                else shell.ReleaseHotkey();
            }
            catch (Exception rollbackError) { SettingsStore.Log(rollbackError); rollbackFailed = true; }
            hotkeyFailure = rollbackFailed; SynchronizeOptions(); RefreshHotkeyStatus();
            SetFeedback(rollbackFailed ? "快捷鍵未完整還原，請重新設定。" : "設定未儲存，請再試一次。");
        }
    }

    async Task RegisterDesktopShortcutAsync()
    {
        if (desktopShortcutRunning || isClosed) return;
        desktopShortcutRunning = true; SetFeedback(null); RefreshHotkeyStatus();
        try
        {
            // Desktop settings change only here, after the user pressed the button.
            await shell.RegisterDesktopShortcutAsync();
            if (!isClosed) SetFeedback("GNOME 鍵盤快捷鍵已更新。", false);
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            if (!isClosed) SetFeedbackText(ex is InvalidOperationException or IOException ? ex.Message : I18n.T("GNOME 鍵盤快捷鍵未更新，請稍後重試。"));
        }
        finally { desktopShortcutRunning = false; if (!isClosed) RefreshHotkeyStatus(); }
    }

    void ChooseCaptureFolder()
    {
        try
        {
            var initial = Directory.Exists(SettingsStore.Current.CaptureSaveDirectory)
                ? SettingsStore.Current.CaptureSaveDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var folder = FileDialogs.PickFolder(this, I18n.T("選擇截圖資料夾"), initial);
            if (folder == null) return;
            var normalized = ValidateCaptureFolder(folder);
            var previous = SnapshotSettings();
            try
            {
                SettingsStore.Current.CaptureSaveDirectory = normalized;
                if (previous.CaptureSaveDirectory != normalized) SettingsStore.Save();
                SynchronizeOptions(); SetFeedback("設定已自動儲存。", false);
            }
            catch (Exception ex)
            {
                RestoreSettings(previous); SettingsStore.Log(ex); SynchronizeOptions();
                SetFeedback("設定未儲存，請再試一次。");
            }
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex); SynchronizeOptions();
            SetFeedback("截圖資料夾無法使用，請選擇可寫入的資料夾。");
        }
    }

    static string ValidateCaptureFolder(string directory)
    {
        var normalized = SettingsStore.NormalizeCaptureSaveDirectory(directory);
        Directory.CreateDirectory(normalized);
        var probePath = Path.Combine(normalized, $".snipflow-write-check-{Guid.NewGuid():N}.tmp");
        using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        probe.WriteByte(0);
        return normalized;
    }

    async void OpenCaptureFolder()
    {
        try
        {
            var directory = SettingsStore.NormalizeCaptureSaveDirectory(SettingsStore.Current.CaptureSaveDirectory);
            Directory.CreateDirectory(directory);
            if (Launcher is not { } launcher || !await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory)))
                SetFeedback("無法開啟截圖資料夾。");
        }
        catch (Exception ex) { SettingsStore.Log(ex); SetFeedback("無法開啟截圖資料夾。"); }
    }

    void OnSettingsChanged(object? sender, EventArgs args)
    {
        if (isClosed) return;
        if (Dispatcher.UIThread.CheckAccess()) SynchronizeOptions();
        else Dispatcher.UIThread.Post(() => { if (!isClosed) SynchronizeOptions(); });
    }

    void SynchronizeOptions()
    {
        synchronizingOptions = true;
        try
        {
            var settings = SettingsStore.Current;
            var mode = ThemeService.NormalizeMode(settings.Theme);
            theme.SelectedItem = theme.Items.OfType<ComboBoxItem>().First(item => item.Tag as string == mode);
            themeNote.IsVisible = mode == "system";
            copy.IsChecked = settings.CopyAfterCapture; history.IsChecked = settings.KeepHistory;
            autoSave.IsChecked = settings.AutoSaveCaptures;
            try
            {
                settings.StartWithWindows = StartupService.ReadRegistration().IsEnabled;
                startup.IsChecked = settings.StartWithWindows; startup.IsEnabled = true;
            }
            catch (Exception ex)
            {
                SettingsStore.Log(ex); startup.IsChecked = settings.StartWithWindows; startup.IsEnabled = false;
                SetFeedback("無法讀取登入啟動狀態，請稍後重試。");
            }
            autoCheck.IsChecked = settings.AutoCheckUpdates; autoDownload.IsChecked = settings.AutoDownloadUpdates;
            // A package-managed Linux install never checks or downloads updates itself.
            autoCheck.IsEnabled = autoDownload.IsEnabled = !shell.Updates.IsManagedExternally;
            captureFolder.Text = settings.CaptureSaveDirectory; ToolTip.SetTip(captureFolder, captureFolder.Text);
            shortcut.Text = settings.Hotkey;
        }
        finally { synchronizingOptions = false; }
    }

    static UserSettings SnapshotSettings() => new()
    {
        Language = SettingsStore.Current.Language, Theme = SettingsStore.Current.Theme, CopyAfterCapture = SettingsStore.Current.CopyAfterCapture,
        AutoSaveCaptures = SettingsStore.Current.AutoSaveCaptures, CaptureSaveDirectory = SettingsStore.Current.CaptureSaveDirectory,
        KeepHistory = SettingsStore.Current.KeepHistory, StartWithWindows = SettingsStore.Current.StartWithWindows,
        Hotkey = SettingsStore.Current.Hotkey, AutoCheckUpdates = SettingsStore.Current.AutoCheckUpdates,
        AutoDownloadUpdates = SettingsStore.Current.AutoDownloadUpdates, HistoryLimit = SettingsStore.Current.HistoryLimit,
        RecentColors = new List<string>(SettingsStore.Current.RecentColors)
    };

    static void RestoreSettings(UserSettings previous)
    {
        SettingsStore.Current.Language = previous.Language;
        SettingsStore.Current.Theme = previous.Theme;
        SettingsStore.Current.CopyAfterCapture = previous.CopyAfterCapture;
        SettingsStore.Current.AutoSaveCaptures = previous.AutoSaveCaptures;
        SettingsStore.Current.CaptureSaveDirectory = previous.CaptureSaveDirectory;
        SettingsStore.Current.KeepHistory = previous.KeepHistory;
        SettingsStore.Current.StartWithWindows = previous.StartWithWindows;
        SettingsStore.Current.Hotkey = previous.Hotkey;
        SettingsStore.Current.AutoCheckUpdates = previous.AutoCheckUpdates;
        SettingsStore.Current.AutoDownloadUpdates = previous.AutoDownloadUpdates;
        SettingsStore.Current.HistoryLimit = previous.HistoryLimit;
        SettingsStore.Current.RecentColors = new List<string>(previous.RecentColors);
    }

    void ThemeSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (synchronizingOptions || isClosed || theme.SelectedItem is not ComboBoxItem item
            || item.Tag is not string tag || tag == ThemeService.NormalizeMode(SettingsStore.Current.Theme)) return;
        var previous = SnapshotSettings();
        try
        {
            SettingsStore.Current.Theme = tag;
            // ThemeService listens to the successful SettingsStore notification.
            SettingsStore.Save(); SynchronizeOptions(); SetFeedback("設定已自動儲存。", false);
        }
        catch (Exception ex)
        {
            RestoreSettings(previous); SettingsStore.Log(ex); SynchronizeOptions();
            SetFeedback("設定未儲存，請再試一次。");
        }
    }

    void LanguageSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (synchronizingLanguage || language.SelectedItem is not ComboBoxItem item || item.Tag is not string tag || tag == I18n.LanguageTag) return;
        var previousSetting = SettingsStore.Current.Language;
        try { SettingsStore.Current.Language = tag; SettingsStore.Save(); }
        catch (Exception ex)
        {
            SettingsStore.Current.Language = previousSetting; SynchronizeLanguageChoice(); SettingsStore.Log(ex);
            SetFeedback("語系未儲存，請再試一次。"); return;
        }
        I18n.SetLanguage(tag); SetFeedback("設定已自動儲存。", false);
    }

    void SynchronizeLanguageChoice()
    {
        synchronizingLanguage = true;
        try { language.SelectedItem = language.Items.OfType<ComboBoxItem>().First(item => item.Tag as string == I18n.LanguageTag); }
        finally { synchronizingLanguage = false; }
    }

    async Task PerformUpdateAsync(Func<Task> operation)
    {
        if (updateActionRunning || shell.Updates.IsBusy) return;
        updateActionRunning = true; SetFeedback(null); RefreshUpdates();
        try { await operation(); }
        catch (Exception ex) { SettingsStore.Log(ex); SetFeedback("更新未完成，請再試一次。"); }
        finally { updateActionRunning = false; if (!isClosed) RefreshUpdates(); }
    }

    void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplyLanguage();
        else Dispatcher.UIThread.Post(ApplyLanguage);
    }

    void OnHotkeyStateChanged(object? sender, EventArgs args)
    {
        if (!isClosed) RefreshHotkeyStatus();
    }

    void ApplyLanguage()
    {
        if (isClosed) return;
        Title = I18n.T("SnipFlow · 設定");
        foreach (var entry in localizedText) entry.Element.Text = I18n.T(entry.Key);
        foreach (var entry in localizedContent) entry.Element.Content = I18n.T(entry.Key);
        foreach (var entry in localizedNames) AutomationProperties.SetName(entry.Element, I18n.T(entry.Key));
        if (feedbackKey != null) feedback.Text = I18n.T(feedbackKey);
        feedback.Foreground = feedbackIsError ? ThemeService.Brush("Error") : ThemeService.Brush("Muted");
        RefreshSelectionBox(theme);
        SynchronizeLanguageChoice(); RefreshUpdates(); RefreshHotkeyStatus();
    }

    /// <summary>A closed ComboBox shows a copy of the selected item's content; reselect it so translated text appears.</summary>
    void RefreshSelectionBox(ComboBox box)
    {
        var selected = box.SelectedItem;
        if (selected is null) return;
        var wasSynchronizing = synchronizingOptions;
        synchronizingOptions = true;
        try { box.SelectedItem = null; box.SelectedItem = selected; }
        finally { synchronizingOptions = wasSynchronizing; }
    }

    void RefreshHotkeyStatus()
    {
        var active = shell.ActiveHotkeyDisplay is { } key ? I18n.F("目前啟用：{0}", key) : I18n.T("全域快捷鍵未啟用");
        var note = shell.HotkeyNote;
        if (note.Length > 0) active += "\n" + note;
        hotkeyStatus.Text = hotkeyFailure ? $"{shell.HotkeyError}\n{active}" : active;
        hotkeyStatus.Foreground = hotkeyFailure ? ThemeService.Brush("Error") : ThemeService.Brush("Ink");
        desktopShortcut.IsVisible = shell.CanRegisterDesktopShortcut;
        desktopShortcut.IsEnabled = !desktopShortcutRunning;
    }

    void OnUpdateChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => { if (!isClosed) RefreshUpdates(); });

    void RefreshUpdates()
    {
        var updates = shell.Updates; var busy = updateActionRunning || updates.IsBusy;
        currentVersion.Text = updates.CurrentVersion;
        if (updates.IsManagedExternally)
        {
            // Distribution packages and source builds are updated by whatever installed them.
            latestVersion.Text = "—";
            updateStatus.Text = updates.Status;
            updateStatus.Foreground = ThemeService.Brush("Ink");
            updateCard.BorderBrush = ThemeService.Brush("Line");
            updateDetail.Text = I18n.T("此安裝方式由套件管理員或下載來源提供新版本；只有 AppImage 版可在 SnipFlow 內更新。");
            updateDetail.IsVisible = true;
            updateProgress.IsVisible = false;
            updateActions.IsVisible = false;
            releaseNotes.IsEnabled = !string.IsNullOrWhiteSpace(updates.ReleaseUrl);
            return;
        }
        latestVersion.Text = updates.LatestVersion ?? (updates.State == UpdateState.UpToDate ? updates.CurrentVersion : I18n.T("尚未檢查"));
        updateStatus.Text = updates.Status;
        updateStatus.Foreground = updates.State == UpdateState.Error ? ThemeService.Brush("Error") : ThemeService.Brush("Ink");
        updateCard.BorderBrush = updates.State == UpdateState.Error ? ThemeService.Brush("Error") : ThemeService.Brush("Line");
        updateDetail.Text = updates.State == UpdateState.Error ? updates.ErrorMessage ?? ""
            : updates.State == UpdateState.Available && updates.CanUpdateNow ? I18n.T("按一下即可下載並重啟更新。")
            : updates.LastCheckedAt is { } checkedAt ? I18n.F("上次檢查：{0:t}", checkedAt.LocalDateTime) : "";
        if (updateDetail.Text == updateStatus.Text) updateDetail.Text = "";
        updateDetail.IsVisible = updateDetail.Text.Length != 0;
        updateProgress.IsVisible = updates.State is UpdateState.Checking or UpdateState.Downloading;
        updateProgress.IsIndeterminate = updates.State == UpdateState.Checking || updates.DownloadProgress == null;
        updateProgress.Value = updates.DownloadProgress ?? 0;
        check.Content = I18n.T(updates.State == UpdateState.Error ? "重試" : updates.State == UpdateState.Checking ? "檢查中…" : "檢查更新");
        check.IsEnabled = !busy;
        bool showUpdateNow = !updates.ReadyToRestart && (updates.CanUpdateNow || updates.State == UpdateState.Downloading || (busy && download.IsVisible));
        download.IsVisible = showUpdateNow; download.IsEnabled = !busy;
        restart.IsVisible = updates.ReadyToRestart; restart.IsEnabled = !busy;
        releaseNotes.IsEnabled = !busy && !string.IsNullOrWhiteSpace(updates.ReleaseUrl);
    }

    void SetFeedback(string? key, bool isError = true)
    {
        feedbackKey = key; feedbackIsError = isError;
        feedback.Text = key == null ? "" : I18n.T(key);
        feedback.Foreground = isError ? ThemeService.Brush("Error") : ThemeService.Brush("Muted");
        feedback.IsVisible = key != null;
    }

    /// <summary>Shows an already translated error, such as a message from gsettings.</summary>
    void SetFeedbackText(string text)
    {
        feedbackKey = null; feedbackIsError = true;
        feedback.Text = text; feedback.Foreground = ThemeService.Brush("Error"); feedback.IsVisible = true;
    }

    TextBlock BindText(TextBlock element, string key) { element.Text = I18n.T(key); localizedText.Add((element, key)); return element; }
    T BindContent<T>(T element, string key) where T : ContentControl { element.Content = I18n.T(key); localizedContent.Add((element, key)); return element; }
    void BindName(Control element, string key) { AutomationProperties.SetName(element, I18n.T(key)); localizedNames.Add((element, key)); }

    void AddHeading(Panel panel, string key) => panel.Children.Add(BindText(new TextBlock { FontSize = 23, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 24) }, key));
    void AddDivider(Panel panel) => panel.Children.Add(new Border { Height = 1, Background = ThemeService.Brush("Line"), Margin = new Thickness(0, 10, 0, 22) });

    void AddValueRow(Panel panel, string key, string note, Control value, TextBlock? noteElement = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        label.Children.Add(BindText(new TextBlock { FontSize = 13 }, key));
        var detail = noteElement ?? new TextBlock();
        detail.Foreground = ThemeService.Brush("Muted"); detail.FontSize = 12; detail.TextWrapping = TextWrapping.Wrap;
        detail.Margin = new Thickness(0, 5, 0, 0); detail.LineHeight = 18;
        label.Children.Add(BindText(detail, note));
        row.Children.Add(label); value.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(value, 1); row.Children.Add(value); BindName(value, key); panel.Children.Add(row);
    }

    void AddToggleRow(Panel panel, string key, string note, CheckBox box, bool value)
    {
        box.WithTheme("ToggleSwitch"); box.IsChecked = value; box.Margin = new Thickness(16, 0, 0, 0); box.VerticalAlignment = VerticalAlignment.Center;
        AddValueRow(panel, key, note, box);
    }
}

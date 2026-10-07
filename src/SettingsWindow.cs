using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Shell;
using Microsoft.Win32;
using SnipFlow.Services;

namespace SnipFlow;

public sealed class SettingsWindow : Window
{
    readonly MainWindow shell;
    readonly List<(TextBlock Element, string Key)> localizedText = new();
    readonly List<(ContentControl Element, string Key)> localizedContent = new();
    readonly List<(FrameworkElement Element, string Key)> localizedNames = new();
    readonly List<Button> navigation = new();
    readonly List<FrameworkElement> panes = new();
    readonly ScrollViewer paneScroll = new();
    readonly ComboBox language = new() { Width = 184, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Right };
    readonly CheckBox copy = new(), history = new(), startup = new(), autoCheck = new(), autoDownload = new();
    readonly HotkeyInputBox shortcut = new(SettingsStore.Current.Hotkey);
    readonly TextBlock hotkeyStatus = new(), updateStatus = new(), updateDetail = new(), currentVersion = new(), latestVersion = new(), feedback = new();
    readonly Button check = new(), download = new(), restart = new(), installer = new(), releaseNotes = new();
    readonly ProgressBar updateProgress = new() { Minimum = 0, Maximum = 100, Height = 4, BorderThickness = new Thickness(0) };
    readonly Border updateCard = new();
    bool synchronizingLanguage, updateActionRunning, hotkeyFailure, isClosed;
    string? feedbackKey;

    public SettingsWindow(MainWindow shell, bool showUpdates = false)
    {
        this.shell = shell;
        Style = Resource<Style>("AppWindow");
        Title = I18n.T("SnipFlow · 設定");
        Width = 760; Height = 620; MinWidth = 700; MinHeight = 550;
        ShowInTaskbar = false; Icon = shell.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(12), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
        });
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(BuildCaption());

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 1); layout.Children.Add(body);

        var sidebar = new Grid { Background = Resource<Brush>("Panel") };
        sidebar.RowDefinitions.Add(new RowDefinition());
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var navigationList = new StackPanel { Margin = new Thickness(12, 20, 12, 12) };
        AddNavigation(navigationList, "一般", "\uE713", 0);
        AddNavigation(navigationList, "快捷鍵", "\uE765", 1);
        AddNavigation(navigationList, "更新", "\uE895", 2);
        sidebar.Children.Add(navigationList);
        var appVersion = new StackPanel { Margin = new Thickness(24, 12, 16, 24) };
        appVersion.Children.Add(new TextBlock { Text = "SnipFlow", FontWeight = FontWeights.SemiBold });
        appVersion.Children.Add(new TextBlock { Text = $"v{shell.Updates.CurrentVersion}", Foreground = Resource<Brush>("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetRow(appVersion, 1); sidebar.Children.Add(appVersion); body.Children.Add(sidebar);
        var separator = new Border { Background = Resource<Brush>("Line") };
        Grid.SetColumn(separator, 1); body.Children.Add(separator);

        var paneHost = new Grid();
        panes.Add(BuildGeneralPane()); panes.Add(BuildHotkeyPane()); panes.Add(BuildUpdatePane());
        foreach (var pane in panes) paneHost.Children.Add(pane);
        paneScroll.Content = paneHost; paneScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        paneScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; paneScroll.Padding = new Thickness(28, 24, 28, 20);
        Grid.SetColumn(paneScroll, 2); body.Children.Add(paneScroll);
        var footer = BuildFooter(); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = new Border { BorderBrush = Resource<Brush>("Line"), BorderThickness = new Thickness(1), Background = Resource<Brush>("Bg"), Child = layout };

        shell.Updates.Changed += OnUpdateChanged;
        I18n.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            isClosed = true;
            shell.Updates.Changed -= OnUpdateChanged;
            I18n.Changed -= OnLanguageChanged;
        };
        SelectPane(showUpdates ? 2 : 0); ApplyLanguage();
    }

    FrameworkElement BuildCaption()
    {
        var caption = new Grid { Background = Resource<Brush>("Bg") };
        caption.ColumnDefinitions.Add(new ColumnDefinition());
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.Children.Add(BindText(new TextBlock
        {
            FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 0, 0), IsHitTestVisible = false
        }, "設定"));
        var close = new Button { Style = Resource<Style>("WindowControl"), Content = "\uE8BB", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Width = 42, Height = 34, Margin = new Thickness(0, 6, 7, 6) };
        BindName(close, "關閉");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); caption.Children.Add(close);
        return caption;
    }

    void AddNavigation(Panel panel, string key, string glyph, int index)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(BindText(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, key));
        var button = new Button { Style = Resource<Style>("NavigationButton"), Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Height = 42, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(12, 8, 12, 8) };
        BindName(button, key); button.Click += (_, _) => SelectPane(index);
        navigation.Add(button); panel.Children.Add(button);
    }

    void SelectPane(int index)
    {
        for (int i = 0; i < panes.Count; i++)
        {
            panes[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            navigation[i].Background = i == index ? Resource<Brush>("Selected") : Brushes.Transparent;
            navigation[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            navigation[i].Foreground = Resource<Brush>(i == index ? "Ink" : "Muted");
        }
        paneScroll.ScrollToTop();
    }

    FrameworkElement BuildGeneralPane()
    {
        var pane = new StackPanel(); AddHeading(pane, "一般");
        language.Items.Add(new ComboBoxItem { Content = "繁體中文", Tag = "zh-TW" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.Items.Add(new ComboBoxItem { Content = "简体中文", Tag = "zh-CN" });
        SynchronizeLanguageChoice(); language.SelectionChanged += LanguageSelectionChanged;
        AddValueRow(pane, "介面語言", "切換後立即套用並儲存。", language);
        AddDivider(pane);
        AddToggleRow(pane, "自動複製", "截圖完成後複製到剪貼簿。", copy, SettingsStore.Current.CopyAfterCapture);
        AddToggleRow(pane, "保留最近截圖", "最多保留 80 張截圖。", history, SettingsStore.Current.KeepHistory);
        AddToggleRow(pane, "登入時啟動", "登入 Windows 後常駐系統匣。", startup, SettingsStore.Current.StartWithWindows);
        return pane;
    }

    FrameworkElement BuildHotkeyPane()
    {
        var pane = new StackPanel(); AddHeading(pane, "快捷鍵");
        pane.Children.Add(BindText(new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold }, "全域截圖快捷鍵"));
        var controls = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        controls.ColumnDefinitions.Add(new ColumnDefinition());
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcut.MinHeight = 38; shortcut.Padding = new Thickness(12, 9, 12, 9); shortcut.Margin = new Thickness(0, 0, 12, 0);
        BindName(shortcut, "全域截圖快捷鍵");
        shortcut.TextChanged += (_, _) => { hotkeyFailure = false; RefreshHotkeyStatus(); };
        controls.Children.Add(shortcut);
        var reset = BindContent(new Button { Style = Resource<Style>("QuietButton"), MinWidth = 70 }, "預設");
        reset.Click += (_, _) => { HotkeyGesture.TryParse(HotkeyGesture.DefaultShortcut, out var gesture); shortcut.SetGesture(gesture); };
        Grid.SetColumn(reset, 1); controls.Children.Add(reset); pane.Children.Add(controls);
        pane.Children.Add(BindText(new TextBlock { Foreground = Resource<Brush>("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19 }, "點選欄位後按下組合鍵。字母／數字須搭配 Ctrl、Alt、Shift 中至少兩個鍵；也可使用 F1–F11。"));
        pane.Children.Add(BindText(new TextBlock { Foreground = Resource<Brush>("Muted"), FontSize = 12, Margin = new Thickness(0, 10, 0, 22) }, "儲存後生效。"));
        hotkeyStatus.FontSize = 13; hotkeyStatus.TextWrapping = TextWrapping.Wrap; hotkeyStatus.LineHeight = 21;
        pane.Children.Add(new Border { Background = Resource<Brush>("Raised"), CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 13, 16, 13), Child = hotkeyStatus });
        pane.Children.Add(BindText(new TextBlock { Foreground = Resource<Brush>("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 16, 0, 0) }, "焦點在其他程式、最小化或隱藏至系統匣時也能截圖。"));
        return pane;
    }

    FrameworkElement BuildUpdatePane()
    {
        var pane = new StackPanel(); AddHeading(pane, "更新");
        var versions = new Grid();
        versions.ColumnDefinitions.Add(new ColumnDefinition()); versions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        versions.RowDefinitions.Add(new RowDefinition()); versions.RowDefinitions.Add(new RowDefinition());
        var installedLabel = BindText(new TextBlock { Foreground = Resource<Brush>("Muted") }, "目前版本");
        var latestLabel = BindText(new TextBlock { Foreground = Resource<Brush>("Muted"), Margin = new Thickness(0, 10, 0, 0) }, "最新版本");
        versions.Children.Add(installedLabel); Grid.SetRow(latestLabel, 1); versions.Children.Add(latestLabel);
        currentVersion.FontWeight = FontWeights.SemiBold; currentVersion.HorizontalAlignment = HorizontalAlignment.Right;
        latestVersion.HorizontalAlignment = HorizontalAlignment.Right; latestVersion.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetColumn(currentVersion, 1); versions.Children.Add(currentVersion);
        Grid.SetColumn(latestVersion, 1); Grid.SetRow(latestVersion, 1); versions.Children.Add(latestVersion);
        pane.Children.Add(new Border { Background = Resource<Brush>("Raised"), CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 13, 16, 13), Margin = new Thickness(0, 0, 0, 16), Child = versions });

        var statusBody = new StackPanel();
        updateStatus.FontSize = 14; updateStatus.FontWeight = FontWeights.SemiBold; updateStatus.TextWrapping = TextWrapping.Wrap; updateStatus.LineHeight = 21;
        updateDetail.Foreground = Resource<Brush>("Muted"); updateDetail.FontSize = 12; updateDetail.TextWrapping = TextWrapping.Wrap; updateDetail.LineHeight = 18; updateDetail.Margin = new Thickness(0, 6, 0, 0);
        statusBody.Children.Add(updateStatus); statusBody.Children.Add(updateDetail);
        updateProgress.Foreground = Resource<Brush>("Accent"); updateProgress.Background = Resource<Brush>("Line"); updateProgress.Margin = new Thickness(0, 12, 0, 0); statusBody.Children.Add(updateProgress);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        ConfigureUpdateButton(check, "檢查更新", "QuietButton");
        ConfigureUpdateButton(download, "下載更新", "Primary");
        ConfigureUpdateButton(restart, "重新啟動並更新", "Primary");
        ConfigureUpdateButton(installer, "下載安裝程式", "Primary");
        check.Click += async (_, _) => await PerformUpdateAsync(() => shell.Updates.CheckAsync(autoDownload.IsChecked == true));
        download.Click += async (_, _) => await PerformUpdateAsync(shell.Updates.DownloadAsync);
        restart.Click += (_, _) =>
        {
            if (!shell.PrepareToDiscard()) return;
            try { shell.Updates.ApplyAndRestart(); }
            catch (Exception ex) { SettingsStore.Log(ex); RefreshUpdates(); SetFeedback("更新未完成，請再試一次。"); }
        };
        installer.Click += (_, _) =>
        {
            try { shell.Updates.OpenInstallerPage(); }
            catch (Exception ex) { SettingsStore.Log(ex); SetFeedback("無法開啟連結，請稍後重試。"); }
        };
        actions.Children.Add(check); actions.Children.Add(download); actions.Children.Add(restart); actions.Children.Add(installer); statusBody.Children.Add(actions);
        updateCard.Background = Resource<Brush>("Panel"); updateCard.BorderBrush = Resource<Brush>("Line"); updateCard.BorderThickness = new Thickness(1); updateCard.CornerRadius = new CornerRadius(9); updateCard.Padding = new Thickness(16, 13, 16, 13); updateCard.Child = statusBody;
        pane.Children.Add(updateCard); AddDivider(pane);
        AddToggleRow(pane, "自動檢查更新", "啟動時與每 4 小時檢查。", autoCheck, SettingsStore.Current.AutoCheckUpdates);
        AddToggleRow(pane, "自動下載更新", "下載完成後，下次啟動時套用。", autoDownload, SettingsStore.Current.AutoDownloadUpdates);
        releaseNotes.Style = Resource<Style>("QuietButton"); releaseNotes.Padding = new Thickness(0, 8, 0, 8); releaseNotes.HorizontalAlignment = HorizontalAlignment.Left; releaseNotes.Foreground = Resource<Brush>("Muted");
        BindContent(releaseNotes, "版本紀錄 ↗");
        releaseNotes.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(shell.Updates.ReleaseUrl) { UseShellExecute = true }); }
            catch (Exception ex) { SettingsStore.Log(ex); SetFeedback("無法開啟連結，請稍後重試。"); }
        };
        pane.Children.Add(releaseNotes);
        return pane;
    }

    void ConfigureUpdateButton(Button button, string key, string style)
    {
        button.Style = Resource<Style>(style); button.Margin = new Thickness(0, 0, 8, 2); button.MinHeight = 34;
        BindContent(button, key);
    }

    FrameworkElement BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(24, 16, 24, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        feedback.Foreground = ErrorBrush; feedback.FontSize = 12; feedback.TextWrapping = TextWrapping.Wrap; feedback.VerticalAlignment = VerticalAlignment.Center; feedback.Margin = new Thickness(0, 0, 18, 0); footer.Children.Add(feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = BindContent(new Button { IsCancel = true, Style = Resource<Style>("QuietButton"), Margin = new Thickness(0, 0, 10, 0), MinWidth = 72, Height = 36 }, "取消");
        var save = BindContent(new Button { Style = Resource<Style>("Primary"), MinWidth = 90, Height = 36 }, "儲存設定"); save.Click += (_, _) => SaveSettings();
        actions.Children.Add(cancel); actions.Children.Add(save); Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        return new Border { BorderBrush = Resource<Brush>("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
    }

    void SaveSettings()
    {
        SetFeedback(null);
        var previous = SnapshotSettings(); var previousActiveKey = shell.ActiveHotkey;
        if (!shell.SetHotkey(shortcut.Shortcut))
        {
            hotkeyFailure = true; SelectPane(1); RefreshHotkeyStatus(); SetFeedback("快捷鍵無法設定。"); return;
        }
        object? previousRunValue = null; RegistryValueKind previousRunKind = RegistryValueKind.String;
        bool runChanged = false;
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            previousRunValue = run.GetValue("SnipFlow", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (previousRunValue != null) previousRunKind = run.GetValueKind("SnipFlow");
            runChanged = true;
            if (startup.IsChecked == true)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException(I18n.T("無法取得程式路徑。"));
                var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
                var stable = parent == null ? "" : Path.Combine(parent.FullName, "SnipFlow.exe");
                if (Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) == "current" && File.Exists(stable)) executable = stable;
                run.SetValue("SnipFlow", $"\"{executable}\" --background");
            }
            else run.DeleteValue("SnipFlow", false);
            SettingsStore.Current.CopyAfterCapture = copy.IsChecked == true;
            SettingsStore.Current.KeepHistory = history.IsChecked == true;
            SettingsStore.Current.StartWithWindows = startup.IsChecked == true;
            SettingsStore.Current.Hotkey = shortcut.Shortcut;
            SettingsStore.Current.AutoCheckUpdates = autoCheck.IsChecked == true;
            SettingsStore.Current.AutoDownloadUpdates = autoDownload.IsChecked == true;
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            RestoreSettings(previous); SettingsStore.Log(ex);
            bool rollbackFailed = false;
            if (runChanged)
            {
                try
                {
                    using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    if (previousRunValue == null) run.DeleteValue("SnipFlow", false);
                    else run.SetValue("SnipFlow", previousRunValue, previousRunKind);
                }
                catch (Exception rollbackError) { SettingsStore.Log(rollbackError); rollbackFailed = true; }
            }
            try
            {
                if (previousActiveKey != null) rollbackFailed |= !shell.SetHotkey(previousActiveKey);
                else shell.ReleaseHotkey();
            }
            catch (Exception rollbackError) { SettingsStore.Log(rollbackError); rollbackFailed = true; }
            hotkeyFailure = rollbackFailed && shell.ActiveHotkey != previousActiveKey; RefreshHotkeyStatus();
            SetFeedback(rollbackFailed ? "設定未完整還原，請重新檢查登入啟動與快捷鍵。" : "設定未儲存，請再試一次。"); return;
        }
        DialogResult = true;
    }

    static UserSettings SnapshotSettings() => new()
    {
        Language = SettingsStore.Current.Language, CopyAfterCapture = SettingsStore.Current.CopyAfterCapture,
        KeepHistory = SettingsStore.Current.KeepHistory, StartWithWindows = SettingsStore.Current.StartWithWindows,
        Hotkey = SettingsStore.Current.Hotkey, AutoCheckUpdates = SettingsStore.Current.AutoCheckUpdates,
        AutoDownloadUpdates = SettingsStore.Current.AutoDownloadUpdates, HistoryLimit = SettingsStore.Current.HistoryLimit
    };

    static void RestoreSettings(UserSettings previous)
    {
        SettingsStore.Current.CopyAfterCapture = previous.CopyAfterCapture;
        SettingsStore.Current.KeepHistory = previous.KeepHistory;
        SettingsStore.Current.StartWithWindows = previous.StartWithWindows;
        SettingsStore.Current.Hotkey = previous.Hotkey;
        SettingsStore.Current.AutoCheckUpdates = previous.AutoCheckUpdates;
        SettingsStore.Current.AutoDownloadUpdates = previous.AutoDownloadUpdates;
    }

    void LanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (synchronizingLanguage || language.SelectedItem is not ComboBoxItem item || item.Tag is not string tag || tag == I18n.LanguageTag) return;
        var previousSetting = SettingsStore.Current.Language;
        try { SettingsStore.Current.Language = tag; SettingsStore.Save(); }
        catch (Exception ex)
        {
            SettingsStore.Current.Language = previousSetting; SynchronizeLanguageChoice(); SettingsStore.Log(ex);
            SetFeedback("語系未儲存，請再試一次。"); return;
        }
        I18n.SetLanguage(tag); SetFeedback(null);
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
        if (Dispatcher.CheckAccess()) ApplyLanguage();
        else Dispatcher.BeginInvoke(ApplyLanguage);
    }

    void ApplyLanguage()
    {
        if (isClosed) return;
        Title = I18n.T("SnipFlow · 設定");
        foreach (var entry in localizedText) entry.Element.Text = I18n.T(entry.Key);
        foreach (var entry in localizedContent) entry.Element.Content = I18n.T(entry.Key);
        foreach (var entry in localizedNames) AutomationProperties.SetName(entry.Element, I18n.T(entry.Key));
        feedback.Text = feedbackKey == null ? "" : I18n.T(feedbackKey);
        SynchronizeLanguageChoice(); RefreshUpdates(); RefreshHotkeyStatus();
    }

    void RefreshHotkeyStatus()
    {
        var active = shell.ActiveHotkey is { } key ? I18n.F("目前啟用：{0}", key) : I18n.T("全域快捷鍵未啟用");
        hotkeyStatus.Text = hotkeyFailure ? $"{shell.HotkeyError}\n{active}" : active;
        hotkeyStatus.Foreground = hotkeyFailure ? ErrorBrush : Resource<Brush>("Ink");
    }

    void OnUpdateChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() => { if (!isClosed) RefreshUpdates(); });

    void RefreshUpdates()
    {
        var updates = shell.Updates; var busy = updateActionRunning || updates.IsBusy;
        currentVersion.Text = updates.CurrentVersion;
        latestVersion.Text = updates.LatestVersion ?? (updates.State == UpdateState.UpToDate ? updates.CurrentVersion : I18n.T("尚未檢查"));
        updateStatus.Text = updates.Status;
        updateStatus.Foreground = updates.State == UpdateState.Error ? ErrorBrush : Resource<Brush>("Ink");
        updateCard.BorderBrush = updates.State == UpdateState.Error ? ErrorBrush : Resource<Brush>("Line");
        updateDetail.Text = updates.State == UpdateState.Error ? updates.ErrorMessage ?? ""
            : !updates.IsInstalled ? I18n.T("此版本需透過安裝程式更新。")
            : updates.LastCheckedAt is { } checkedAt ? I18n.F("上次檢查：{0:t}", checkedAt.LocalDateTime) : "";
        if (updateDetail.Text == updateStatus.Text) updateDetail.Text = "";
        updateDetail.Visibility = updateDetail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        updateProgress.Visibility = updates.State is UpdateState.Checking or UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        updateProgress.IsIndeterminate = updates.State == UpdateState.Checking || updates.DownloadProgress == null;
        updateProgress.Value = updates.DownloadProgress ?? 0;
        check.Content = I18n.T(updates.State == UpdateState.Error ? "重試" : updates.State == UpdateState.Checking ? "檢查中…" : "檢查更新");
        check.IsEnabled = !busy;
        download.Visibility = updates.CanDownload ? Visibility.Visible : Visibility.Collapsed; download.IsEnabled = !busy;
        restart.Visibility = updates.ReadyToRestart && updates.IsInstalled ? Visibility.Visible : Visibility.Collapsed; restart.IsEnabled = !busy;
        bool useInstaller = !updates.IsInstalled || (!updates.CanDownload && !updates.ReadyToRestart && (updates.State is UpdateState.Available or UpdateState.Error));
        installer.Visibility = useInstaller && updates.CanOpenInstaller ? Visibility.Visible : Visibility.Collapsed; installer.IsEnabled = !busy;
        releaseNotes.IsEnabled = !string.IsNullOrWhiteSpace(updates.ReleaseUrl);
    }

    void SetFeedback(string? key) { feedbackKey = key; feedback.Text = key == null ? "" : I18n.T(key); }
    static Brush ErrorBrush { get; } = CreateErrorBrush();
    static Brush CreateErrorBrush() { var brush = new SolidColorBrush(Color.FromRgb(242, 157, 152)); brush.Freeze(); return brush; }
    T Resource<T>(string key) where T : class => (T)FindResource(key);
    TextBlock BindText(TextBlock element, string key) { element.Text = I18n.T(key); localizedText.Add((element, key)); return element; }
    T BindContent<T>(T element, string key) where T : ContentControl { element.Content = I18n.T(key); localizedContent.Add((element, key)); return element; }
    void BindName(FrameworkElement element, string key) { AutomationProperties.SetName(element, I18n.T(key)); localizedNames.Add((element, key)); }

    void AddHeading(Panel panel, string key) => panel.Children.Add(BindText(new TextBlock { FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 24) }, key));
    void AddDivider(Panel panel) => panel.Children.Add(new Border { Height = 1, Background = Resource<Brush>("Line"), Margin = new Thickness(0, 10, 0, 22) });

    void AddValueRow(Panel panel, string key, string note, FrameworkElement value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        label.Children.Add(BindText(new TextBlock { FontSize = 13 }, key));
        label.Children.Add(BindText(new TextBlock { Foreground = Resource<Brush>("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), LineHeight = 18 }, note));
        row.Children.Add(label); value.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(value, 1); row.Children.Add(value); BindName(value, key); panel.Children.Add(row);
    }

    void AddToggleRow(Panel panel, string key, string note, CheckBox box, bool value)
    {
        box.Style = Resource<Style>("ToggleSwitch"); box.IsChecked = value; box.Margin = new Thickness(16, 0, 0, 0); box.VerticalAlignment = VerticalAlignment.Center;
        AddValueRow(panel, key, note, box);
    }
}

using System.Diagnostics;
using Microsoft.Win32;
using SnipFlow.Services;
namespace SnipFlow;

public sealed class SettingsWindow : Window
{
    readonly MainWindow shell;
    readonly TextBlock updateStatus;
    readonly Button download;
    readonly Button restart;
    readonly List<(TextBlock Element, string Key)> localizedText = new();
    readonly List<(ContentControl Element, string Key)> localizedContent = new();
    readonly ComboBox language = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8) };
    bool synchronizingLanguage;
    public SettingsWindow(MainWindow shell)
    {
        this.shell = shell;
        Style = (Style)FindResource("AppWindow");
        Title = I18n.T("SnipFlow · 設定"); Width = 660; Height = 740; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(26) };
        var heading = BindText(new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,20) }, "設定"); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var cancel = BindContent(new Button { IsCancel = true, Margin = new Thickness(0,0,10,0) }, "取消");
        var save = BindContent(new Button { Style = (Style)FindResource("Primary") }, "儲存設定"); actions.Children.Add(cancel); actions.Children.Add(save); root.Children.Add(actions);
        var body = new StackPanel(); var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        Section(body, "語言");
        var languageRow = new DockPanel { Margin = new Thickness(0,8,0,8) };
        var languageLabel = BindText(new TextBlock { Width = 132, VerticalAlignment = VerticalAlignment.Center }, "介面語言");
        DockPanel.SetDock(languageLabel, Dock.Left); languageRow.Children.Add(languageLabel);
        language.Items.Add(new ComboBoxItem { Content = "繁體中文", Tag = "zh-TW" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.Items.Add(new ComboBoxItem { Content = "简体中文", Tag = "zh-CN" });
        SynchronizeLanguageChoice();
        language.SelectionChanged += LanguageSelectionChanged;
        languageRow.Children.Add(language); body.Children.Add(languageRow);
        body.Children.Add(BindText(new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0,0,0,18) }, "切換後立即套用並儲存。"));
        Section(body, "截圖與歷史");
        var copy = AddCheck(body, "截圖完成後自動複製", SettingsStore.Current.CopyAfterCapture);
        var history = AddCheck(body, "保留最近截圖（最多 80 張）", SettingsStore.Current.KeepHistory);
        var startup = AddCheck(body, "登入 Windows 後常駐系統匣", SettingsStore.Current.StartWithWindows);
        var row = new DockPanel { Margin = new Thickness(0,16,0,20) };
        var label = BindText(new TextBlock { Width = 132, VerticalAlignment = VerticalAlignment.Center }, "框選快捷鍵"); DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
        var combo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8) };
        foreach (var key in new[] { "Ctrl + Alt + S", "Ctrl + Shift + S", "Alt + Shift + S" }) combo.Items.Add(key);
        combo.SelectedItem = SettingsStore.Current.Hotkey; row.Children.Add(combo); body.Children.Add(row);
        Section(body, "版本與更新", $"SnipFlow {typeof(App).Assembly.GetName().Version?.ToString(3)} · GitHub Releases");
        var autoCheck = AddCheck(body, "自動檢查新版本（啟動時與每 4 小時）", SettingsStore.Current.AutoCheckUpdates);
        var autoDownload = AddCheck(body, "自動下載更新，下次啟動時套用", SettingsStore.Current.AutoDownloadUpdates);
        updateStatus = new TextBlock { Text = I18n.T(shell.Updates.Status), Foreground = (Brush)FindResource("Accent"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,10) }; body.Children.Add(updateStatus);
        var updates = new WrapPanel();
        var check = BindContent(new Button { Background = (Brush)FindResource("Raised"), Margin = new Thickness(0,0,8,8) }, "檢查更新");
        download = BindContent(new Button { Margin = new Thickness(0,0,8,8) }, "下載新版本");
        restart = BindContent(new Button { Style = (Style)FindResource("Primary"), Margin = new Thickness(0,0,8,8) }, "重新啟動更新");
        check.Click += async (_, _) => { check.IsEnabled = false; try { await shell.Updates.CheckAsync(autoDownload.IsChecked == true); } finally { check.IsEnabled = true; RefreshUpdates(); } };
        download.Click += async (_, _) => { download.IsEnabled = false; try { await shell.Updates.DownloadAsync(); } finally { download.IsEnabled = true; RefreshUpdates(); } };
        restart.Click += (_, _) => { if (!shell.PrepareToDiscard()) return; try { shell.Updates.ApplyAndRestart(); } catch (Exception ex) { SettingsStore.Log(ex); MessageBox.Show(this, ex.Message, I18n.T("更新未完成")); } };
        updates.Children.Add(check); updates.Children.Add(download); updates.Children.Add(restart); body.Children.Add(updates);
        if (!string.IsNullOrWhiteSpace(shell.Updates.RepositoryUrl))
        {
            var link = BindContent(new Button { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0,8,0,8), Foreground = (Brush)FindResource("Muted") }, "查看 GitHub 專案與版本紀錄 ↗");
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(shell.Updates.RepositoryUrl) { UseShellExecute = true }); body.Children.Add(link);
        }
        save.Click += (_, _) =>
        {
            var chosenKey = combo.SelectedItem as string ?? "Ctrl + Alt + S";
            if (chosenKey != SettingsStore.Current.Hotkey && !shell.SetHotkey(chosenKey)) { MessageBox.Show(this, I18n.T("這組快捷鍵已被其他程式使用，請換一組。"), I18n.T("快捷鍵無法設定")); return; }
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (startup.IsChecked == true)
                {
                    var executable = Environment.ProcessPath ?? throw new InvalidOperationException(I18n.T("無法取得程式路徑。"));
                    var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
                    var stable = parent == null ? "" : Path.Combine(parent.FullName, "SnipFlow.exe");
                    if (Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) == "current" && File.Exists(stable)) executable = stable;
                    run.SetValue("SnipFlow", $"\"{executable}\" --background");
                }
                else run.DeleteValue("SnipFlow", false);
                SettingsStore.Current.CopyAfterCapture = copy.IsChecked == true; SettingsStore.Current.KeepHistory = history.IsChecked == true;
                SettingsStore.Current.StartWithWindows = startup.IsChecked == true; SettingsStore.Current.Hotkey = chosenKey;
                SettingsStore.Current.AutoCheckUpdates = autoCheck.IsChecked == true; SettingsStore.Current.AutoDownloadUpdates = autoDownload.IsChecked == true;
                SettingsStore.Save(); DialogResult = true;
            }
            catch (Exception ex) { SettingsStore.Log(ex); MessageBox.Show(this, ex.Message, I18n.T("設定未儲存")); }
        };
        shell.Updates.Changed += OnUpdateChanged;
        I18n.Changed += OnLanguageChanged;
        Closed += (_, _) => { shell.Updates.Changed -= OnUpdateChanged; I18n.Changed -= OnLanguageChanged; };
        Content = root; ApplyLanguage();
    }
    void LanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (synchronizingLanguage || language.SelectedItem is not ComboBoxItem item || item.Tag is not string tag || tag == I18n.LanguageTag) return;
        var previousSetting = SettingsStore.Current.Language;
        try
        {
            SettingsStore.Current.Language = tag;
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            SettingsStore.Current.Language = previousSetting;
            SynchronizeLanguageChoice(); SettingsStore.Log(ex);
            MessageBox.Show(this, ex.Message, I18n.T("語系未儲存"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        I18n.SetLanguage(tag);
    }
    void SynchronizeLanguageChoice()
    {
        synchronizingLanguage = true;
        try { language.SelectedItem = language.Items.OfType<ComboBoxItem>().First(item => item.Tag as string == I18n.LanguageTag); }
        finally { synchronizingLanguage = false; }
    }
    void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.CheckAccess()) ApplyLanguage();
        else Dispatcher.BeginInvoke(ApplyLanguage);
    }
    void ApplyLanguage()
    {
        Title = I18n.T("SnipFlow · 設定");
        foreach (var entry in localizedText) entry.Element.Text = I18n.T(entry.Key);
        foreach (var entry in localizedContent) entry.Element.Content = I18n.T(entry.Key);
        SynchronizeLanguageChoice(); RefreshUpdates();
    }
    void OnUpdateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshUpdates);
    void RefreshUpdates()
    {
        updateStatus.Text = I18n.T(shell.Updates.Status);
        restart.Visibility = shell.Updates.ReadyToRestart ? Visibility.Visible : Visibility.Collapsed;
        download.Visibility = shell.Updates.LatestVersion != null && !shell.Updates.ReadyToRestart ? Visibility.Visible : Visibility.Collapsed;
    }
    TextBlock BindText(TextBlock element, string key) { element.Text = I18n.T(key); localizedText.Add((element, key)); return element; }
    T BindContent<T>(T element, string key) where T : ContentControl { element.Content = I18n.T(key); localizedContent.Add((element, key)); return element; }
    CheckBox AddCheck(Panel panel, string label, bool value) { var box = BindContent(new CheckBox { IsChecked = value }, label); panel.Children.Add(box); return box; }
    void Section(Panel panel, string title, string note = "")
    {
        panel.Children.Add(BindText(new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,6,0,5) }, title));
        if (note.Length > 0) panel.Children.Add(new TextBlock { Text = note, Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0,0,0,10) });
    }
}

namespace SnipFlow;

/// <summary>Temporary window that exercises the shared UI foundation. Removed once the shell is ported.</summary>
internal sealed class FoundationPreviewWindow : Window
{
    public FoundationPreviewWindow()
    {
        Title = "SnipFlow foundation";
        Width = 720; Height = 420;
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        var row = new WrapPanel();
        row.Children.Add(new Button { Content = Icons.WithText(IconKind.Camera, I18n.T("新截圖"), foreground: ThemeService.Brush("AccentInk")) }.WithTheme("Primary"));
        row.Children.Add(new Button { Content = I18n.T("開啟檔案") });
        row.Children.Add(new Button { Content = I18n.T("複製含標註圖片") }.WithTheme("QuietButton"));
        row.Children.Add(new Button { Background = Brush.Parse("#FF646C") }.WithTheme("ColorSwatch"));
        row.Children.Add(new Button { Content = Icons.Create(IconKind.Dismiss, 12) }.WithTheme("WindowControl"));
        panel.Children.Add(row);
        var icons = new WrapPanel();
        foreach (var kind in Enum.GetValues<IconKind>())
            icons.Children.Add(new Border { Margin = new Thickness(4), Child = Icons.Create(kind, 20) });
        panel.Children.Add(icons);
        panel.Children.Add(new CheckBox { Content = I18n.T("自動儲存截圖"), IsChecked = true });
        panel.Children.Add(new CheckBox { Content = I18n.T("登入時啟動") }.WithTheme("ToggleSwitch"));
        panel.Children.Add(new TextBox { Text = "SnipFlow 快剪", Width = 240, HorizontalAlignment = HorizontalAlignment.Left });
        var theme = new ComboBox { ItemsSource = new[] { "dark", "light", "system" }, SelectedIndex = 0, Width = 160 };
        theme.SelectionChanged += (_, _) =>
        {
            SettingsStore.Current.Theme = theme.SelectedItem as string ?? "dark";
            SettingsStore.Save();
        };
        panel.Children.Add(theme);
        var dialog = new Button { Content = "Dialog" };
        dialog.Click += (_, _) => MessageBox.Show(this, I18n.T("目前的標註尚未儲存，要先存成 PNG 嗎？"), I18n.T("保留標註"), MessageBoxButton.YesNoCancel);
        panel.Children.Add(dialog);
        Content = new ScrollViewer { Content = panel };
    }
}

using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;

namespace SnipFlow;

public sealed class TextResultWindow : DialogWindow
{
    public TextResultWindow(string text)
    {
        Title = I18n.T("SnipFlow · 辨識文字"); Width = 680; Height = 500; MinWidth = 460; MinHeight = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None; ShowInTaskbar = false;
        if (Application.Current?.TryGetResource("SnipFlowIcon", null, out var icon) == true && icon is WindowIcon windowIcon) Icon = windowIcon;
        WindowAppearance.Apply(this); WindowChrome.EnableResizeBorder(this);
        var shell = new DockPanel();
        var caption = new DockPanel { Margin = new Thickness(20, 0, 8, 0), Height = 44, Background = Brushes.Transparent }; DockPanel.SetDock(caption, Dock.Top);
        var windowClose = new Button { Content = Icons.Create(IconKind.Dismiss, 12) }.WithTheme("WindowControl");
        Avalonia.Automation.AutomationProperties.SetName(windowClose, I18n.T("關閉"));
        windowClose.Click += (_, _) => Close(); DockPanel.SetDock(windowClose, Dock.Right); caption.Children.Add(windowClose);
        caption.Children.Add(new TextBlock { Text = I18n.T("辨識文字"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }); shell.Children.Add(caption);
        WindowChrome.EnableTitleBarDrag(this, caption);
        var root = new DockPanel { Margin = new Thickness(24, 8, 24, 24) }; shell.Children.Add(root);
        var note = new TextBlock { Text = I18n.T("可直接修正辨識結果。"), Foreground = ThemeService.Brush("Muted"), Margin = new Thickness(0, 0, 0, 18) }; DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var input = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Top };
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var close = new Button { Content = I18n.T("關閉"), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        var copy = new Button { Content = I18n.T("複製文字") }.WithTheme("Primary");
        close.Click += (_, _) => Close();
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException(I18n.T("複製失敗"));
                for (int i = 0; ; i++) { try { await clipboard.SetTextAsync(input.Text ?? ""); break; } catch (Exception) when (i < 4) { await Task.Delay(100); } }
                copy.Content = I18n.T("已複製");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, I18n.T("複製失敗")); }
        };
        actions.Children.Add(close); actions.Children.Add(copy); root.Children.Add(actions); root.Children.Add(input);
        Content = new Border { Child = shell, Background = ThemeService.Brush("Bg"), BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(1) };
    }
}

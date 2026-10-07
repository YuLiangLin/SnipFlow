using SnipFlow.Services;
namespace SnipFlow;
public sealed class TextResultWindow : Window
{
    public TextResultWindow(string text)
    {
        Style = (Style)FindResource("AppWindow");
        Title = I18n.T("SnipFlow · 辨識文字"); Width = 680; Height = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = new TextBlock { Text = I18n.T("辨識文字"), FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,8) }; DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var note = new TextBlock { Text = I18n.T("可直接修正辨識結果。"), Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0,0,0,18) }; DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var input = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var close = new Button { Content = I18n.T("關閉"), Margin = new Thickness(0,0,10,0), IsCancel = true };
        var copy = new Button { Content = I18n.T("複製文字"), Style = (Style)FindResource("Primary") };
        copy.Click += async (_, _) => { try { for (int i=0;;i++) { try { Clipboard.SetText(input.Text); break; } catch (System.Runtime.InteropServices.ExternalException) when (i<4) { await Task.Delay(100); } } copy.Content = I18n.T("已複製"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, I18n.T("複製失敗")); } };
        actions.Children.Add(close); actions.Children.Add(copy); root.Children.Add(actions); root.Children.Add(input); Content = root;
    }
}

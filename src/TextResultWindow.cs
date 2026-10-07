using SnipFlow.Services;
using System.Windows.Shell;
namespace SnipFlow;
public sealed class TextResultWindow : Window
{
    public TextResultWindow(string text)
    {
        Style = (Style)FindResource("AppWindow");
        Title = I18n.T("SnipFlow · 辨識文字"); Width = 680; Height = 500; MinWidth = 460; MinHeight = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(9), GlassFrameThickness = new Thickness(0) });
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        var shell = new DockPanel();
        var caption = new DockPanel { Margin = new Thickness(20,0,8,0), Height = 44 }; DockPanel.SetDock(caption, Dock.Top);
        var windowClose = new Button { Content = "×", FontSize = 19, Style = (Style)FindResource("WindowControl") };
        WindowChrome.SetIsHitTestVisibleInChrome(windowClose, true); windowClose.Click += (_, _) => Close(); DockPanel.SetDock(windowClose, Dock.Right); caption.Children.Add(windowClose);
        caption.Children.Add(new TextBlock { Text = I18n.T("辨識文字"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); shell.Children.Add(caption);
        var root = new DockPanel { Margin = new Thickness(24,8,24,24) }; shell.Children.Add(root);
        var note = new TextBlock { Text = I18n.T("可直接修正辨識結果。"), Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0,0,0,18) }; DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var input = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var close = new Button { Content = I18n.T("關閉"), Margin = new Thickness(0,0,10,0), IsCancel = true };
        var copy = new Button { Content = I18n.T("複製文字"), Style = (Style)FindResource("Primary") };
        copy.Click += async (_, _) => { try { for (int i=0;;i++) { try { Clipboard.SetText(input.Text); break; } catch (System.Runtime.InteropServices.ExternalException) when (i<4) { await Task.Delay(100); } } copy.Content = I18n.T("已複製"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, I18n.T("複製失敗")); } };
        actions.Children.Add(close); actions.Children.Add(copy); root.Children.Add(actions); root.Children.Add(input);
        Content = new Border { Child = shell, BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
    }
}

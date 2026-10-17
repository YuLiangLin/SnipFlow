using System.Windows.Shell;

namespace SnipFlow.Services;

public static class AppDialog
{
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        => ShowCore(Application.Current?.MainWindow, text, caption, buttons);

    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        => ShowCore(owner, text, caption, buttons);

    static MessageBoxResult ShowCore(Window? owner, string text, string caption, MessageBoxButton buttons)
    {
        var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        var dialog = new Window
        {
            Title = caption, Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Style = Application.Current?.TryFindResource("AppWindow") as Style
        };
        if (owner?.IsVisible == true) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        WindowChrome.SetWindowChrome(dialog, new WindowChrome { CaptionHeight = 44, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(9), GlassFrameThickness = new Thickness(0) });
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var captionBar = new DockPanel { Margin = new Thickness(20,0,8,0) };
        var close = new Button { Content = "×", FontSize = 19, Style = dialog.FindResource("WindowControl") as Style };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); DockPanel.SetDock(close, Dock.Right);
        close.Click += (_, _) => dialog.Close(); captionBar.Children.Add(close);
        captionBar.Children.Add(new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(captionBar);
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22, Margin = new Thickness(24,14,24,22) };
        var scroll = new ScrollViewer { Content = message, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24,0,24,22) };
        void Add(string label, MessageBoxResult answer, bool primary = false, bool cancel = false)
        {
            var button = new Button { Content = I18n.T(label), MinWidth = 72, Margin = new Thickness(8,0,0,0), IsDefault = primary, IsCancel = cancel,
                Style = dialog.FindResource(primary ? "Primary" : "QuietButton") as Style };
            button.Click += (_, _) => { result = answer; dialog.Close(); }; actions.Children.Add(button);
        }
        if (buttons == MessageBoxButton.YesNoCancel)
        { Add("取消", MessageBoxResult.Cancel, cancel: true); Add("不儲存", MessageBoxResult.No); Add("儲存", MessageBoxResult.Yes, primary: true); }
        else if (buttons == MessageBoxButton.YesNo)
        { Add("否", MessageBoxResult.No); Add("是", MessageBoxResult.Yes, primary: true); }
        else
        { if (buttons == MessageBoxButton.OKCancel) Add("取消", MessageBoxResult.Cancel, cancel: true); Add("確定", MessageBoxResult.OK, primary: true); }
        Grid.SetRow(actions, 2); layout.Children.Add(actions);
        dialog.Content = new Border { Child = layout, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = (Brush)dialog.FindResource("Line"), Background = (Brush)dialog.FindResource("Panel") };
        dialog.SourceInitialized += (_, _) => WindowAppearance.Apply(dialog);
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; dialog.Close(); } };
        dialog.ShowDialog(); return result;
    }
}

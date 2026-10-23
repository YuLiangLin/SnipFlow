using Avalonia.Input;
using Avalonia.Layout;

namespace SnipFlow.Ui
{
    public enum MessageBoxButton { OK, OKCancel, YesNoCancel, YesNo }
    public enum MessageBoxResult { None, OK, Cancel, Yes, No }
    public enum MessageBoxImage { None, Information, Question, Warning, Error }
}

namespace SnipFlow.Services
{
    /// <summary>Themed, blocking message box used in place of the platform message box (alias: MessageBox).</summary>
    public static class AppDialog
    {
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(Modal.DefaultOwner(), text, caption, buttons);

        public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(owner, text, caption, buttons);

        static MessageBoxResult ShowCore(Window? owner, string text, string caption, MessageBoxButton buttons)
        {
            var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
            var dialog = new DialogWindow
            {
                Title = caption, Width = 460, SizeToContent = SizeToContent.Height,
                CanResize = false, WindowDecorations = WindowDecorations.None, ShowInTaskbar = owner is not { IsVisible: true },
                WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Topmost = owner is not { IsVisible: true },
            };
            if (Application.Current?.TryGetResource("SnipFlowIcon", null, out var icon) == true && icon is WindowIcon windowIcon)
                dialog.Icon = windowIcon;
            var layout = new Grid { RowDefinitions = new RowDefinitions("44,Auto,Auto") };
            var captionBar = new DockPanel { Margin = new Thickness(20, 0, 8, 0), Background = Brushes.Transparent };
            var close = new Button { Content = Icons.Create(IconKind.Dismiss, 12) }.WithTheme("WindowControl");
            DockPanel.SetDock(close, Dock.Right);
            close.Click += (_, _) => dialog.Close();
            captionBar.Children.Add(close);
            captionBar.Children.Add(new TextBlock { Text = caption, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            WindowChrome.EnableTitleBarDrag(dialog, captionBar, allowMaximize: false);
            layout.Children.Add(captionBar);
            var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22, Margin = new Thickness(24, 14, 24, 22) };
            var scroll = new ScrollViewer { Content = message, MaxHeight = 320, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 0, 24, 22) };
            Button? defaultButton = null;
            void Add(string label, MessageBoxResult answer, bool primary = false, bool cancel = false)
            {
                var button = new Button { Content = I18n.T(label), MinWidth = 72, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary, IsCancel = cancel }
                    .WithTheme(primary ? "Primary" : "QuietButton");
                button.Click += (_, _) => { result = answer; dialog.Close(); };
                actions.Children.Add(button);
                if (primary) defaultButton = button;
            }
            if (buttons == MessageBoxButton.YesNoCancel)
            { Add("取消", MessageBoxResult.Cancel, cancel: true); Add("不儲存", MessageBoxResult.No); Add("儲存", MessageBoxResult.Yes, primary: true); }
            else if (buttons == MessageBoxButton.YesNo)
            { Add("否", MessageBoxResult.No); Add("是", MessageBoxResult.Yes, primary: true); }
            else
            { if (buttons == MessageBoxButton.OKCancel) Add("取消", MessageBoxResult.Cancel, cancel: true); Add("確定", MessageBoxResult.OK, primary: true); }
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);
            dialog.Content = new Border { Child = layout, BorderThickness = new Thickness(1), BorderBrush = ThemeService.Brush("Line"), Background = ThemeService.Brush("Panel") };
            dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); } };
            dialog.Opened += (_, _) => defaultButton?.Focus();
            Modal.ShowDialog(dialog, owner);
            return result;
        }
    }
}

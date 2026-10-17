using System.Windows.Controls.Primitives;
using SnipFlow.Capture;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    void CaptureMenuClick(object sender, RoutedEventArgs e)
    {
        if (busy || capturePending || !CanStartCapture || sender is not Button button) return;
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = button.Tag is "CaptureRight" ? PlacementMode.Right : PlacementMode.Bottom
        };
        foreach (var option in new[]
        {
            ("框選截圖", CaptureMode.Region),
            ("視窗截圖", CaptureMode.Window),
            ("單一螢幕截圖", CaptureMode.Monitor),
            ("全部螢幕截圖", CaptureMode.AllMonitors)
        })
        {
            var item = new MenuItem { Header = I18n.T(option.Item1) };
            item.Click += async (_, _) => await StartCaptureAsync(option.Item2);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        AddScrollCaptureOptions(menu, code: false);
        menu.IsOpen = true;
    }

    void ScrollMenuClick(object sender, RoutedEventArgs e) => ShowScrollCaptureMenu(sender, code: false);
    void CodeMenuClick(object sender, RoutedEventArgs e) => ShowScrollCaptureMenu(sender, code: true);

    void ShowScrollCaptureMenu(object sender, bool code)
    {
        if (busy || capturePending || !CanStartCapture || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        AddScrollCaptureOptions(menu, code);
        menu.IsOpen = true;
    }

    void AddScrollCaptureOptions(ContextMenu menu, bool code)
    {
        foreach (var option in new[] { ("視窗長截圖", CaptureMode.Window), ("框選長截圖", CaptureMode.Region) })
        {
            var item = new MenuItem { Header = I18n.T(option.Item1) };
            item.Click += async (_, _) =>
            {
                if (code) await StartCodeCaptureAsync(option.Item2);
                else await StartScrollCaptureAsync(option.Item2);
            };
            menu.Items.Add(item);
        }
    }

    async void WindowScrollMenuItemClick(object sender, RoutedEventArgs e) => await StartScrollCaptureAsync(CaptureMode.Window);
    async void RegionScrollMenuItemClick(object sender, RoutedEventArgs e) => await StartScrollCaptureAsync(CaptureMode.Region);
    async void WindowCodeMenuItemClick(object sender, RoutedEventArgs e) => await StartCodeCaptureAsync(CaptureMode.Window);
    async void RegionCodeMenuItemClick(object sender, RoutedEventArgs e) => await StartCodeCaptureAsync(CaptureMode.Region);
}

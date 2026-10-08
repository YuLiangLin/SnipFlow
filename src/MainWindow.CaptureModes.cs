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
        menu.IsOpen = true;
    }
}

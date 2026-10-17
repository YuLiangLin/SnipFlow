using SnipFlow.Capture;

namespace SnipFlow.Recording;

/// <summary>Windows: the toolbar is excluded from capture, so it may stay at the top-right of the work area.</summary>
public sealed partial class RecordingWindow
{
    partial void ConfigureToolbarWindow()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _excludedFromCapture = handle != IntPtr.Zero && CaptureNative.TryExcludeFromCapture(handle);
        var screen = Screens.ScreenFromBounds(_bounds) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var scale = RenderScaling > 0 ? RenderScaling : screen.Scaling > 0 ? screen.Scaling : 1;
            var physicalWidth = (int)Math.Ceiling(Width * scale);
            var x = Math.Max(area.X, area.Right - physicalWidth - 16);
            var y = area.Y + 16;
            Position = new PixelPoint(x, y);
        }
        // Loaded may already have disabled Start while exclusion was still being initialized.
        SetBusy(_busy);
    }
}

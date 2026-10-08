using System.Windows.Interop;
using SnipFlow.Services;
using GdiBitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiRectangle = System.Drawing.Rectangle;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace SnipFlow.Capture;

public static class ScreenshotService
{
    private static readonly SemaphoreSlim SelectionGate = new(1, 1);

    /// <summary>
    /// Selects a region only, for callers such as scrolling capture and recording.
    /// </summary>
    public static Task<CaptureResult?> CaptureRegionAsync() => CaptureAsync(CaptureMode.Region, false);

    /// <summary>
    /// Captures a region, window, chosen monitor, or the complete virtual desktop.
    /// Selection overlays allow R/W/S/A to change mode. Escape, a right click,
    /// or a concurrent request returns null.
    /// </summary>
    public static Task<CaptureResult?> CaptureAsync(CaptureMode mode) => CaptureAsync(mode, true);

    private static async Task<CaptureResult?> CaptureAsync(CaptureMode mode, bool allowModeSwitch)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!await SelectionGate.WaitAsync(0).ConfigureAwait(false))
            return null;

        try
        {
            var dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException(I18n.T("螢幕框選需要在 WPF 應用程式中執行。"));

            if (dispatcher.CheckAccess())
                return await CaptureCoreAsync(mode, allowModeSwitch);

            var operation = dispatcher.InvokeAsync(() => CaptureCoreAsync(mode, allowModeSwitch));
            return await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            SelectionGate.Release();
        }
    }

    /// <summary>Captures a rectangle expressed in physical desktop pixels, including negative coordinates.</summary>
    public static BitmapSource CaptureRectangle(GdiRectangle bounds) =>
        CaptureRectangle(bounds, CaptureNative.GetMonitors());

    private static BitmapSource CaptureRectangle(GdiRectangle bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), I18n.T("擷取範圍必須有正數的寬度與高度。"));

        using var bitmap = new GdiBitmap(bounds.Width, bounds.Height, GdiPixelFormat.Format32bppRgb);
        using (var graphics = GdiGraphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Black);
            var destination = graphics.GetHdc();
            try
            {
                // Copy each physical display separately. Virtual-desktop gaps stay black
                // instead of depending on undefined pixels from the desktop DC.
                foreach (var monitor in monitors)
                {
                    var intersection = GdiRectangle.Intersect(bounds, monitor.Bounds);
                    if (intersection.Width > 0 && intersection.Height > 0)
                        CaptureNative.CopyDesktopPixels(destination, intersection,
                            intersection.X - bounds.X, intersection.Y - bounds.Y);
                }
            }
            finally
            {
                graphics.ReleaseHdc(destination);
            }
        }

        var handle = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            CaptureNative.DeleteObject(handle);
        }
    }

    private static async Task<CaptureResult?> CaptureCoreAsync(CaptureMode mode, bool allowModeSwitch)
    {
        var monitors = CaptureNative.GetMonitors();
        var desktopBounds = monitors.Select(monitor => monitor.Bounds).Aggregate(GdiRectangle.Union);
        var desktopImage = CaptureRectangle(desktopBounds, monitors);

        if (mode == CaptureMode.AllMonitors)
            return new CaptureResult(desktopImage, desktopBounds);

        var windows = allowModeSwitch || mode == CaptureMode.Window
            ? CaptureNative.GetWindows() : Array.Empty<CaptureWindow>();
        using var session = new CaptureSession(monitors, desktopBounds, desktopImage, windows,
            mode, allowModeSwitch);
        var selection = await session.SelectAsync();
        if (selection is not GdiRectangle bounds)
            return null;

        if (session.SelectedWindow is { } target)
            return await WindowScreenshotService.CaptureAsync(target);

        // Copy the chosen pixels so history entries do not retain the entire virtual desktop bitmap.
        var crop = new Int32Rect(bounds.X - desktopBounds.X, bounds.Y - desktopBounds.Y,
            bounds.Width, bounds.Height);
        var stride = checked((bounds.Width * desktopImage.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[checked(stride * bounds.Height)];
        desktopImage.CopyPixels(crop, pixels, stride, 0);
        var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, desktopImage.Format,
            desktopImage.Palette, pixels, stride);
        image.Freeze();
        return new CaptureResult(image, bounds);
    }
}

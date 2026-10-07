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
    /// Captures the desktop before presenting pixel-based selection overlays.
    /// A second concurrent request, Escape, a right click, or an empty selection returns null.
    /// </summary>
    public static async Task<CaptureResult?> CaptureRegionAsync()
    {
        if (!await SelectionGate.WaitAsync(0).ConfigureAwait(false))
            return null;

        try
        {
            var dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException(I18n.T("螢幕框選需要在 WPF 應用程式中執行。"));

            if (dispatcher.CheckAccess())
                return await CaptureRegionCoreAsync();

            var operation = dispatcher.InvokeAsync(CaptureRegionCoreAsync);
            return await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            SelectionGate.Release();
        }
    }

    /// <summary>Captures a rectangle expressed in physical desktop pixels, including negative coordinates.</summary>
    public static BitmapSource CaptureRectangle(GdiRectangle bounds)
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
                CaptureNative.CopyDesktopPixels(destination, bounds);
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

    private static async Task<CaptureResult?> CaptureRegionCoreAsync()
    {
        var monitors = CaptureNative.GetMonitors();
        var desktopBounds = monitors.Select(monitor => monitor.Bounds).Aggregate(GdiRectangle.Union);
        var desktopImage = CaptureRectangle(desktopBounds);

        using var session = new CaptureSession(monitors, desktopBounds, desktopImage);
        var selection = await session.SelectAsync();
        if (selection is not GdiRectangle bounds)
            return null;

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

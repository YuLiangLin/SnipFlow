using System.Windows.Interop;
using System.Windows.Threading;
using SnipFlow.Services;
using GdiBitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiRectangle = System.Drawing.Rectangle;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace SnipFlow.Capture;

public static class ScreenshotService
{
    private static readonly SemaphoreSlim SelectionGate = new(1, 1);
    private static Task? _desktopFlush;

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

    /// <summary>Selects a window, then a visible client-area viewport bound to that exact HWND.</summary>
    public static Task<CaptureResult?> CaptureWindowRegionAsync() =>
        CaptureWithGateAsync(CaptureWindowRegionCoreAsync);

    private static Task<CaptureResult?> CaptureAsync(CaptureMode mode, bool allowModeSwitch)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        return CaptureWithGateAsync(() => CaptureCoreAsync(mode, allowModeSwitch));
    }

    private static async Task<CaptureResult?> CaptureWithGateAsync(Func<Task<CaptureResult?>> capture)
    {
        if (!await SelectionGate.WaitAsync(0).ConfigureAwait(false))
            return null;

        try
        {
            var dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException(I18n.T("螢幕框選需要在 WPF 應用程式中執行。"));

            if (dispatcher.CheckAccess())
                return await capture();

            var operation = dispatcher.InvokeAsync(capture);
            return await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            SelectionGate.Release();
        }
    }

    private static async Task<CaptureResult?> CaptureWindowRegionCoreAsync()
    {
        var monitors = CaptureNative.GetMonitors();
        var desktopBounds = monitors.Select(monitor => monitor.Bounds).Aggregate(GdiRectangle.Union);
        var desktopImage = CaptureRectangle(desktopBounds, monitors);
        CaptureWindow selected;
        using (var selector = new CaptureSession(monitors, desktopBounds, desktopImage,
                   CaptureNative.GetWindows(), CaptureMode.Window, allowModeSwitch: false, selectWindowOnly: true))
        {
            if (await selector.SelectAsync() is null) return null;
            selected = selector.SelectedWindow
                ?? throw new InvalidOperationException(I18n.T("選取的視窗資訊遺失，請重新選取。"));
        }

        var target = CaptureNative.GetWindowTarget(selected);
        if (!CaptureNative.ActivateSelectedWindow(target))
            throw new InvalidOperationException(I18n.T("無法啟用選定視窗，請先把它移到前景後重試。"));

        // Let the first overlays close and the selected window become visible before
        // taking the second selector's background. No viewport is inferred from a title.
        await Dispatcher.Yield(DispatcherPriority.Render);
        await FlushDesktopChangesAsync();
        if (!CaptureNative.IsCurrentTarget(target))
            throw new InvalidOperationException(I18n.T("選定視窗的位置、大小或縮放已改變，請重新選取。"));
        if (!CaptureNative.IsForegroundTarget(target)
            || !CaptureNative.SameMonitorLayout(monitors, CaptureNative.GetMonitors())) return null;
        desktopImage = CaptureRectangle(desktopBounds, monitors);
        if (!CaptureNative.IsCurrentTarget(target) || !CaptureNative.IsForegroundTarget(target)
            || !CaptureNative.SameMonitorLayout(monitors, CaptureNative.GetMonitors())) return null;

        GdiRectangle region;
        using (var selector = new CaptureSession(monitors, desktopBounds, desktopImage,
                   Array.Empty<CaptureWindow>(), CaptureMode.Region, allowModeSwitch: false, restrictedTarget: target))
        {
            if (await selector.SelectAsync() is not GdiRectangle selection) return null;
            region = selection;
        }

        // The initial frame is fresh visible desktop pixels, matching subsequent scroll
        // observations. Do not return the frozen selector image or a WGC window bitmap.
        await Dispatcher.Yield(DispatcherPriority.Render);
        await FlushDesktopChangesAsync();
        if (!CaptureNative.IsForegroundTarget(target)) return null;
        if (!CaptureNative.CanCaptureWindowRegion(target, region, monitors, null, out var reason))
            throw new InvalidOperationException(I18n.T(reason));
        var image = CaptureRectangle(region, monitors);
        if (!CaptureNative.CanCaptureWindowRegion(target, region, monitors, null, out reason))
            throw new InvalidOperationException(I18n.T(reason));
        if (!CaptureNative.IsForegroundTarget(target)) return null;
        return new CaptureResult(image, region) { Target = target };
    }

    private static async Task FlushDesktopChangesAsync()
    {
        // Reuse a still-pending native flush after a timeout instead of creating an
        // unbounded number of blocked workers if the compositor is unavailable.
        var flush = _desktopFlush;
        if (flush is null || flush.IsCompleted)
            flush = _desktopFlush = Task.Run(CaptureNative.FlushDesktopChanges);
        try
        {
            await flush.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            _ = ObserveDesktopFlushAsync(flush);
            throw new TimeoutException(I18n.T("桌面畫面未能更新，請稍後重新選取。"));
        }
    }

    private static async Task ObserveDesktopFlushAsync(Task flush)
    {
        try { await flush.ConfigureAwait(false); }
        catch (Exception exception) { SettingsStore.Log(exception); }
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

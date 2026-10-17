namespace SnipFlow.Capture;

public static partial class ScreenshotService
{
    private static readonly SemaphoreSlim SelectionGate = new(1, 1);
    private static Task? _desktopFlush;

    /// <summary>
    /// True when <see cref="CaptureWindowRegionAsync"/> can bind a selection to one window
    /// (<see cref="CaptureResult.Target"/>). False in Wayland sessions, where other windows cannot be
    /// identified: the method then falls back to a plain region selection with a null target.
    /// </summary>
    public static bool SupportsWindowTargets => !PlatformInfo.IsWayland;

    /// <summary>True when a single window can be captured on its own (WGC / XComposite / the desktop picker).</summary>
    public static bool IsWindowCaptureSupported => WindowScreenshotService.IsSupported || PlatformInfo.IsWayland;

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

    /// <summary>Selects a window, then a visible client-area viewport bound to that exact window.</summary>
    /// <remarks>In Wayland sessions (<see cref="SupportsWindowTargets"/> is false) this selects a region
    /// and returns a result without <see cref="CaptureResult.Target"/>.</remarks>
    public static Task<CaptureResult?> CaptureWindowRegionAsync() =>
        CaptureWithGateAsync(SupportsWindowTargets ? CaptureWindowRegionCoreAsync : CaptureUnboundRegionAsync);

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
            if (Application.Current is null)
                throw new InvalidOperationException(I18n.T("螢幕框選需要在 SnipFlow 應用程式中執行。"));

            var dispatcher = Dispatcher.UIThread;
            try
            {
                if (dispatcher.CheckAccess())
                    return await capture();
                return await dispatcher.InvokeAsync(capture).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The desktop's own screenshot dialog (Wayland portal) was dismissed.
                return null;
            }
        }
        finally
        {
            SelectionGate.Release();
        }
    }

    private static async Task<CaptureResult?> CaptureWindowRegionCoreAsync()
    {
        var monitors = CaptureNative.GetMonitors();
        var desktopBounds = CaptureNative.DesktopBounds(monitors);
        var desktopImage = await CaptureRectangleAsync(desktopBounds, monitors);
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
        await YieldToRenderAsync();
        await FlushDesktopChangesAsync();
        if (!CaptureNative.IsCurrentTarget(target))
            throw new InvalidOperationException(I18n.T("選定視窗的位置、大小或縮放已改變，請重新選取。"));
        if (!CaptureNative.IsForegroundTarget(target)
            || !CaptureNative.SameMonitorLayout(monitors, CaptureNative.GetMonitors())) return null;
        desktopImage = await CaptureRectangleAsync(desktopBounds, monitors);
        if (!CaptureNative.IsCurrentTarget(target) || !CaptureNative.IsForegroundTarget(target)
            || !CaptureNative.SameMonitorLayout(monitors, CaptureNative.GetMonitors())) return null;

        PixelRect region;
        using (var selector = new CaptureSession(monitors, desktopBounds, desktopImage,
                   Array.Empty<CaptureWindow>(), CaptureMode.Region, allowModeSwitch: false, restrictedTarget: target))
        {
            if (await selector.SelectAsync() is not PixelRect selection) return null;
            region = selection;
        }

        // The initial frame is fresh visible desktop pixels, matching subsequent scroll
        // observations. Do not return the frozen selector image or a WGC window bitmap.
        await YieldToRenderAsync();
        await FlushDesktopChangesAsync();
        if (!CaptureNative.IsForegroundTarget(target)) return null;
        if (!CaptureNative.CanCaptureWindowRegion(target, region, monitors, null, out var reason))
            throw new InvalidOperationException(I18n.T(reason));
        var image = await CaptureRectangleAsync(region, monitors);
        if (!CaptureNative.CanCaptureWindowRegion(target, region, monitors, null, out reason))
            throw new InvalidOperationException(I18n.T(reason));
        if (!CaptureNative.IsForegroundTarget(target)) return null;
        return new CaptureResult(image, region) { Target = target };
    }

    /// <summary>Wayland fallback for window-bound scrolling: a region without a window identity.</summary>
    private static async Task<CaptureResult?> CaptureUnboundRegionAsync()
    {
        var monitors = CaptureNative.GetMonitors();
        var desktopBounds = CaptureNative.DesktopBounds(monitors);
        var desktopImage = await CaptureRectangleAsync(desktopBounds, monitors);
        using var session = new CaptureSession(monitors, desktopBounds, desktopImage, Array.Empty<CaptureWindow>(),
            CaptureMode.Region, allowModeSwitch: false,
            initialHint: I18n.T("Wayland 無法鎖定單一視窗，請直接框選要捲動的內容範圍。"));
        if (await session.SelectAsync() is not PixelRect bounds)
            return null;
        return new CaptureResult(CropDesktopImage(desktopImage, desktopBounds, bounds), bounds);
    }

    private static async Task YieldToRenderAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

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

    /// <summary>
    /// Captures FRESH pixels of a rectangle expressed in physical desktop pixels, including negative
    /// coordinates. Scrolling capture calls this repeatedly. In a Wayland session it reads the live
    /// ScreenCast stream while one is running (<see cref="StartLiveCaptureAsync"/>) and otherwise asks the
    /// screenshot portal, which blocks the caller; prefer <see cref="CaptureRectangleAsync(PixelRect)"/> there.
    /// The image can be larger than <paramref name="bounds"/> on scaled Wayland desktops.
    /// </summary>
    public static Bitmap CaptureRectangle(PixelRect bounds) =>
        CaptureRectangle(bounds, CaptureNative.GetMonitors());

    /// <summary><see cref="CaptureRectangle(PixelRect)"/> without blocking the calling thread.</summary>
    public static Task<Bitmap> CaptureRectangleAsync(PixelRect bounds) =>
        CaptureRectangleAsync(bounds, CaptureNative.GetMonitors());

    private static Bitmap CaptureRectangle(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), I18n.T("擷取範圍必須有正數的寬度與高度。"));
        return CaptureNative.CopyDesktop(bounds, monitors).ToBitmap();
    }

    private static async Task<Bitmap> CaptureRectangleAsync(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), I18n.T("擷取範圍必須有正數的寬度與高度。"));
        var pixels = await CaptureNative.CopyDesktopAsync(bounds, monitors).ConfigureAwait(false);
        return pixels.ToBitmap();
    }

    /// <summary>
    /// Keeps a live screen stream for repeated <see cref="CaptureRectangle(PixelRect)"/> calls. In a Wayland
    /// session this starts an xdg-desktop-portal ScreenCast (the desktop may ask once which screen to share;
    /// the choice is remembered when the portal supports it) and returns null when the user cancels.
    /// Elsewhere desktop pixels are always readable and a no-op lease is returned. Dispose the lease when done.
    /// </summary>
    public static Task<IDisposable?> StartLiveCaptureAsync(CancellationToken cancellationToken = default) =>
        StartLiveCaptureCoreAsync(cancellationToken);

    private static partial Task<IDisposable?> StartLiveCaptureCoreAsync(CancellationToken cancellationToken);

    private static async Task<CaptureResult?> CaptureCoreAsync(CaptureMode mode, bool allowModeSwitch)
    {
        if (mode == CaptureMode.Window && PlatformInfo.IsWayland)
            return await CaptureWithSystemPickerAsync();

        var monitors = CaptureNative.GetMonitors();
        var desktopBounds = CaptureNative.DesktopBounds(monitors);
        var desktopImage = await CaptureRectangleAsync(desktopBounds, monitors);

        if (mode == CaptureMode.AllMonitors)
            return new CaptureResult(desktopImage, desktopBounds);

        var windows = allowModeSwitch || mode == CaptureMode.Window
            ? CaptureNative.GetWindows() : Array.Empty<CaptureWindow>();
        using var session = new CaptureSession(monitors, desktopBounds, desktopImage, windows,
            mode, allowModeSwitch);
        var selection = await session.SelectAsync();
        if (selection is not PixelRect bounds)
            return null;

        if (session.UseSystemWindowPicker)
            return await CaptureWithSystemPickerAsync();

        if (session.SelectedWindow is { } target)
            return await WindowScreenshotService.CaptureAsync(target);

        // Copy the chosen pixels so history entries do not retain the entire virtual desktop bitmap.
        return new CaptureResult(CropDesktopImage(desktopImage, desktopBounds, bounds), bounds);
    }

    /// <summary>Wayland: the desktop's interactive screenshot UI picks a window (or any area).</summary>
    private static partial Task<CaptureResult?> CaptureWithSystemPickerAsync();

    /// <summary>Crops desktop coordinates out of a desktop image, honoring its pixel scale.</summary>
    private static Bitmap CropDesktopImage(Bitmap desktopImage, PixelRect desktopBounds, PixelRect bounds)
    {
        var size = desktopImage.PixelSize;
        var scaleX = size.Width / (double)desktopBounds.Width;
        var scaleY = size.Height / (double)desktopBounds.Height;
        var left = (int)Math.Round((bounds.X - desktopBounds.X) * scaleX);
        var top = (int)Math.Round((bounds.Y - desktopBounds.Y) * scaleY);
        var right = (int)Math.Round((bounds.Right - desktopBounds.X) * scaleX);
        var bottom = (int)Math.Round((bounds.Bottom - desktopBounds.Y) * scaleY);
        left = Math.Clamp(left, 0, size.Width - 1);
        top = Math.Clamp(top, 0, size.Height - 1);
        right = Math.Clamp(right, left + 1, size.Width);
        bottom = Math.Clamp(bottom, top + 1, size.Height);
        return ImageTools.Crop(desktopImage, new PixelRect(left, top, right - left, bottom - top));
    }
}

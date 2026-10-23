using SnipFlow.Platform.Linux.Capture;

namespace SnipFlow.Capture;

/// <summary>
/// X11 window snapshot. With a compositing manager every top-level window renders into its own
/// XComposite pixmap, which is read directly: covered or partly off-screen windows come out right,
/// like Windows.Graphics.Capture. Without a compositor only the visible screen exists, so the window
/// is activated (raised) and copied from the desktop only when nothing covers it.
/// </summary>
internal static partial class WindowScreenshotService
{
    private static readonly SemaphoreSlim NativeGate = new(1, 1);

    // Wayland sessions use the desktop's own picker instead (see ScreenshotService).
    internal static partial bool IsSupported => !PlatformInfo.IsWayland;

    internal static partial async Task<CaptureResult> CaptureAsync(CaptureWindow target)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(I18n.T("Wayland 不允許程式直接讀取其他視窗，請改用框選或螢幕擷取。"));
        if (!await NativeGate.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException(I18n.T("視窗擷取元件仍在結束上一個工作，請稍後重試。"));
        try
        {
            if (!CaptureNative.IsCurrentWindow(target))
                throw new InvalidOperationException(I18n.T("視窗已關閉、隱藏或移動，請重新選取。"));
            if (target.Bounds.Width > 16384 || target.Bounds.Height > 16384
                || (long)target.Bounds.Width * target.Bounds.Height > 64_000_000)
                throw new InvalidOperationException(I18n.T("視窗範圍過大，請縮小視窗或改用框選。"));

            var pixels = await Task.Run(() => X11Desktop.TryCopyWindowPixmap(target.Handle, target.Bounds)).ConfigureAwait(false);
            if (pixels is null)
                pixels = await CaptureVisibleAsync(target).ConfigureAwait(false);
            if (!CaptureNative.IsCurrentWindow(target))
                throw new InvalidOperationException(I18n.T("視窗已關閉、隱藏或移動，請重新選取。"));
            return new CaptureResult(pixels.ToBitmap(), target.Bounds);
        }
        finally
        {
            NativeGate.Release();
        }
    }

    /// <summary>No compositor: raise the window, wait for it to repaint, and accept only uncovered pixels.</summary>
    private static async Task<PixelBuffer> CaptureVisibleAsync(CaptureWindow window)
    {
        var monitors = CaptureNative.GetMonitors();
        if (!CaptureNative.TryGetWindowTarget(window.Handle, out var target) || target.WindowBounds != window.Bounds)
            throw new InvalidOperationException(I18n.T("視窗已關閉、隱藏或移動，請重新選取。"));
        // The selection overlays are closing on the UI thread; activation and repaint happen meanwhile.
        await Task.Run(() =>
        {
            CaptureNative.ActivateSelectedWindow(target);
            CaptureNative.FlushDesktopChanges();
        }).ConfigureAwait(false);
        var above = CaptureNative.GetWindowsAbove(window.Handle);
        if (above is null || above.Any(layer => CaptureGeometry.IntersectsWith(layer.Bounds, window.Bounds))
            || !CaptureNative.IsOnMonitors(window.Bounds, monitors))
            throw new InvalidOperationException(I18n.T("沒有執行合成管理器時，只能擷取完整顯示在螢幕上、未被遮住的視窗。請把視窗移到前景並完整顯示，或改用框選。"));
        return CaptureNative.CopyDesktop(window.Bounds, monitors);
    }
}

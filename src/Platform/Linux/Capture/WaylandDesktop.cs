using SnipFlow.Capture;
using SnipFlow.Platform.Linux.Portal;
using SnipFlow.Platform.Linux.Wayland;
using Tmds.DBus.Protocol;

namespace SnipFlow.Platform.Linux.Capture;

/// <summary>
/// Desktop pixels in a Wayland session. SnipFlow runs on XWayland, which cannot read other clients'
/// pixels, so screenshots come from org.freedesktop.portal.Screenshot and repeated captures from a
/// live ScreenCast stream (<see cref="WaylandScreenStream"/>) while one runs.
/// </summary>
/// <remarks>
/// The portal image covers the compositor's whole layout at its own (physical) resolution, while the
/// monitors and window positions SnipFlow sees come from XWayland, which may use logical (scaled)
/// coordinates. Desktop rectangles are therefore mapped with one scale per axis, and crops keep the
/// full portal resolution.
/// </remarks>
internal static class WaylandDesktop
{
    private const string ScreenshotInterface = "org.freedesktop.portal.Screenshot";

    internal static PixelBuffer CopyDesktop(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        if (TryCopyFromStream(bounds) is { } live)
            return live;
        // Synchronous callers (scrolling capture without a stream): the portal call does not need the
        // UI thread, so blocking on it from there cannot deadlock.
        return Task.Run(() => CopyFromPortalAsync(bounds, monitors)).GetAwaiter().GetResult();
    }

    internal static async Task<PixelBuffer> CopyDesktopAsync(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        if (TryCopyFromStream(bounds) is { } live)
            return live;
        return await CopyFromPortalAsync(bounds, monitors).ConfigureAwait(false);
    }

    private static PixelBuffer? TryCopyFromStream(PixelRect bounds) =>
        WaylandScreenStream.Active is { } stream && stream.TryGetLatestFrame(bounds, out PixelBuffer? frame) ? frame : null;

    private static async Task<PixelBuffer> CopyFromPortalAsync(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors)
    {
        var image = await TakeScreenshotAsync(interactive: false).ConfigureAwait(false)
            ?? throw new OperationCanceledException(I18n.T("已取消系統截圖。"));
        var desktop = CaptureNative.DesktopBounds(monitors);
        var crop = MapToImage(bounds, desktop, image.Size);
        return crop == new PixelRect(image.Size) ? image : image.Crop(crop);
    }

    /// <summary>Maps desktop (XWayland) coordinates into a portal image of the whole desktop.</summary>
    internal static PixelRect MapToImage(PixelRect bounds, PixelRect desktop, PixelSize image)
    {
        var scaleX = image.Width / (double)desktop.Width;
        var scaleY = image.Height / (double)desktop.Height;
        var left = Math.Clamp((int)Math.Round((bounds.X - desktop.X) * scaleX), 0, image.Width - 1);
        var top = Math.Clamp((int)Math.Round((bounds.Y - desktop.Y) * scaleY), 0, image.Height - 1);
        var right = Math.Clamp((int)Math.Round((bounds.Right - desktop.X) * scaleX), left + 1, image.Width);
        var bottom = Math.Clamp((int)Math.Round((bounds.Bottom - desktop.Y) * scaleY), top + 1, image.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The desktop's interactive screenshot UI: the user picks a window, screen or area with the
    /// compositor's own tools. The result has no desktop position, so its bounds start at (0, 0).
    /// </summary>
    internal static async Task<CaptureResult?> CaptureInteractiveAsync()
    {
        var image = await TakeScreenshotAsync(interactive: true).ConfigureAwait(false);
        if (image is null) return null;
        return new CaptureResult(image.ToBitmap(), new PixelRect(image.Size));
    }

    /// <summary>
    /// Calls Screenshot(parent_window, {interactive}) and loads the PNG the portal wrote. Returns null
    /// when the user cancelled. Files the portal created for this request are deleted after loading
    /// (some desktops store them in the Pictures folder).
    /// </summary>
    internal static async Task<PixelBuffer?> TakeScreenshotAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        var requested = DateTime.UtcNow;
        PortalResponse response;
        try
        {
            response = await DesktopPortal.RequestAsync(ScreenshotInterface, "Screenshot", "sa{sv}",
                static (ref MessageWriter writer) => writer.WriteString(""),
                new Dictionary<string, VariantValue>
                {
                    ["interactive"] = VariantValue.Bool(interactive),
                    ["modal"] = VariantValue.Bool(true)
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException exception)
        {
            throw new InvalidOperationException(MissingPortalMessage(), exception);
        }
        catch (Exception exception) when (exception is DBusExceptionBase or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(MissingPortalMessage(), exception);
        }

        if (response.Cancelled)
            return null;
        if (!response.Succeeded)
            throw new InvalidOperationException(I18n.T(interactive
                ? "桌面的截圖工具無法完成擷取，請改用框選或螢幕擷取。"
                : "系統拒絕了截圖要求。請在系統設定的隱私權（螢幕擷取）中允許 SnipFlow，然後重試。"));
        var path = response.Results.TryGetValue("uri", out var uri) ? DesktopPortal.LocalPath(uri) : null;
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException(I18n.T("系統截圖服務沒有傳回圖片，請重試。"));
        try
        {
            using var bitmap = ImageTools.Load(path);
            return PixelBuffer.FromBitmap(bitmap);
        }
        finally
        {
            DeleteIfCreatedForRequest(path, requested);
        }
    }

    private static void DeleteIfCreatedForRequest(string path, DateTime requested)
    {
        try
        {
            // Only a file written after this request started is the portal's output for it; never
            // touch older files that the portal may hand back from elsewhere.
            var info = new FileInfo(path);
            if (info.Exists && info.LastWriteTimeUtc >= requested.AddSeconds(-2))
                info.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SettingsStore.Log(exception);
        }
    }

    internal static string MissingPortalMessage() => I18n.T(PlatformInfo.IsGnome
        ? "找不到系統截圖服務（xdg-desktop-portal）。請安裝：sudo apt install xdg-desktop-portal xdg-desktop-portal-gnome，登出後重試。"
        : PlatformInfo.IsKde
            ? "找不到系統截圖服務（xdg-desktop-portal）。請安裝：sudo apt install xdg-desktop-portal xdg-desktop-portal-kde，登出後重試。"
            : "找不到系統截圖服務（xdg-desktop-portal）。請安裝 xdg-desktop-portal 與桌面對應的後端（例如 xdg-desktop-portal-gnome、-kde 或 -wlr），登出後重試。");
}

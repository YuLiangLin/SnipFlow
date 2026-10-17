namespace SnipFlow.Capture;

/// <summary>
/// One snapshot of the selected top-level window; never substitutes mixed desktop pixels for
/// hidden parts. Windows uses Windows.Graphics.Capture (ScreenRecorderLib); X11 uses the
/// compositor's XComposite window pixmap, or the visible window when no compositor runs.
/// </summary>
internal static partial class WindowScreenshotService
{
    internal static partial bool IsSupported { get; }

    internal static partial Task<CaptureResult> CaptureAsync(CaptureWindow target);
}

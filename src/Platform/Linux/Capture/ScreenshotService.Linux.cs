using SnipFlow.Platform.Linux.Capture;
using SnipFlow.Platform.Linux.Wayland;

namespace SnipFlow.Capture;

public static partial class ScreenshotService
{
    private sealed class NoLiveCapture : IDisposable
    {
        public void Dispose() { }
    }

    private static partial async Task<IDisposable?> StartLiveCaptureCoreAsync(CancellationToken cancellationToken)
    {
        // X11: XShm/XGetImage read current pixels on every call; no stream is needed.
        if (!PlatformInfo.IsWayland)
            return new NoLiveCapture();
        var stream = new WaylandScreenStream();
        try
        {
            if (!await stream.StartAsync(cancellationToken).ConfigureAwait(false))
            {
                stream.Dispose();
                return null;
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static partial Task<CaptureResult?> CaptureWithSystemPickerAsync() => WaylandDesktop.CaptureInteractiveAsync();
}

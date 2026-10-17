namespace SnipFlow.Capture;

public static partial class ScreenshotService
{
    private sealed class NoLiveCapture : IDisposable
    {
        public void Dispose() { }
    }

    // GDI reads current desktop pixels on every call; no stream is needed.
    private static partial Task<IDisposable?> StartLiveCaptureCoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IDisposable?>(new NoLiveCapture());

    private static partial Task<CaptureResult?> CaptureWithSystemPickerAsync() =>
        throw new PlatformNotSupportedException();
}

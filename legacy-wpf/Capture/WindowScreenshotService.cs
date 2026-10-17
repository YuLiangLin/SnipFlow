using ScreenRecorderLib;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Capture;

/// <summary>One WGC frame from the selected HWND; never substitutes mixed desktop pixels.</summary>
internal static class WindowScreenshotService
{
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
        && Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported();

    internal static async Task<CaptureResult> CaptureAsync(CaptureWindow target)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(I18n.T("目前 Windows 工作階段不支援視窗擷取，請改用框選或螢幕擷取。"));
        if (!await NativeGate.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException(I18n.T("視窗擷取元件仍在結束上一個工作，請稍後重試。"));

        // Native creation, encoding and Dispose (which joins its worker) stay off the UI thread.
        // The native lease is released only after shutdown is confirmed. A stalled native
        // shutdown cannot block the UI or allow another snapshot to reuse its live resources.
        var operation = Task.Run(() => CaptureCoreAsync(target));
        try
        {
            return await operation.WaitAsync(OperationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = ObserveShutdownAsync(operation);
            throw new TimeoutException(I18n.T("視窗擷取逾時，請稍後重試；若仍無法擷取，請改用框選。"));
        }
    }

    private static async Task<CaptureResult> CaptureCoreAsync(CaptureWindow target)
    {
        var stream = new MemoryStream();
        Recorder? recorder = null;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownConfirmed = true;
        void Completed(object? sender, RecordingCompleteEventArgs args) => completion.TrySetResult(true);
        void Failed(object? sender, RecordingFailedEventArgs args) => completion.TrySetException(
            new InvalidOperationException(I18n.T("無法擷取此視窗，請改用框選。") + " " + args.Error));

        try
        {
            if (!CaptureNative.IsCurrentWindow(target))
                throw new InvalidOperationException(I18n.T("視窗已關閉、隱藏或移動，請重新選取。"));
            if (target.Bounds.Width > 16384 || target.Bounds.Height > 16384
                || (long)target.Bounds.Width * target.Bounds.Height > 64_000_000)
                throw new InvalidOperationException(I18n.T("視窗範圍過大，請縮小視窗或改用框選。"));

            // SRL rounds source/output dimensions down to even pixels. Pad upwards
            // before that step, keep the content at top-left, then remove only padding.
            var outputWidth = (target.Bounds.Width + 1) & ~1;
            var outputHeight = (target.Bounds.Height + 1) & ~1;
            var source = new WindowRecordingSource(target.Handle)
            {
                IsCursorCaptureEnabled = false,
                IsBorderRequired = false,
                OutputSize = new ScreenSize(outputWidth, outputHeight),
                AnchorPoint = Anchor.TopLeft,
                Stretch = StretchMode.None
            };
            recorder = Recorder.CreateRecorder(new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { source } },
                OutputOptions = new OutputOptions
                {
                    RecorderMode = RecorderMode.Screenshot,
                    OutputFrameSize = new ScreenSize(outputWidth, outputHeight),
                    Stretch = StretchMode.None
                },
                SnapshotOptions = new SnapshotOptions { SnapshotFormat = ImageFormat.PNG, SnapshotsWithVideo = false },
                AudioOptions = new AudioOptions { IsAudioEnabled = false },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = false, IsMouseClicksDetected = false },
                LogOptions = new LogOptions { IsLogEnabled = false }
            });
            shutdownConfirmed = false;
            recorder.OnRecordingComplete += Completed;
            recorder.OnRecordingFailed += Failed;
            recorder.Record(stream);
            await completion.Task.WaitAsync(FrameTimeout).ConfigureAwait(false);
            if (!CaptureNative.IsCurrentWindow(target))
                throw new InvalidOperationException(I18n.T("視窗已關閉、隱藏或移動，請重新選取。"));

            // Decode only after the native encoder has finished writing the in-memory PNG.
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames[0].PixelWidth < target.Bounds.Width
                || decoder.Frames[0].PixelHeight < target.Bounds.Height)
                throw new InvalidOperationException(I18n.T("無法擷取此視窗，請改用框選。"));
            var image = new CroppedBitmap(decoder.Frames[0],
                new Int32Rect(0, 0, target.Bounds.Width, target.Bounds.Height));
            image.Freeze();
            return new CaptureResult(image, new DrawingRectangle(target.Bounds.X, target.Bounds.Y,
                image.PixelWidth, image.PixelHeight));
        }
        finally
        {
            if (recorder is not null)
            {
                try
                {
                    if (!completion.Task.IsCompleted)
                        recorder.Stop();
                }
                catch (Exception exception)
                {
                    SettingsStore.Log(new InvalidOperationException("Window snapshot stop failed.", exception));
                }

                recorder.OnRecordingComplete -= Completed;
                recorder.OnRecordingFailed -= Failed;
                await Task.Run(recorder.Dispose).ConfigureAwait(false);
                shutdownConfirmed = true;
            }
            if (shutdownConfirmed)
            {
                stream.Dispose();
                NativeGate.Release();
            }
        }
    }

    private static async Task ObserveShutdownAsync(Task<CaptureResult> operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception exception)
        {
            SettingsStore.Log(new InvalidOperationException("Window snapshot shutdown failed.", exception));
        }
    }
}

using SnipFlow.Platform.Linux.Recording;

namespace SnipFlow.Recording;

/// <summary>ffmpeg recorder for Linux: x11grab in X11 sessions, ScreenCast portal frames in Wayland sessions.</summary>
public sealed partial class ScreenRecordingService
{
    private partial void EnsurePlatformSupported()
    {
        if (ExternalTool.Find("ffmpeg") is null)
            throw new PlatformNotSupportedException(T("螢幕錄影需要 ffmpeg。請安裝：sudo apt install ffmpeg"));
        if (!PlatformInfo.IsWayland && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new PlatformNotSupportedException(T("無法連線到 X 顯示伺服器（DISPLAY），無法錄影。"));
    }

    private partial async Task<ScreenRecorderBackend> CreateRecorderAsync(PixelRect target, RecordingOptions options,
        CancellationToken cancellationToken) =>
        await FfmpegScreenRecorder.CreateAsync(target, options, _translate, cancellationToken).ConfigureAwait(false);

    private partial string? DescribePlatformError(Exception exception) => null;
}

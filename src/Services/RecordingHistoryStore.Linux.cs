using System.Globalization;

namespace SnipFlow.Services;

public static partial class RecordingHistoryStore
{
    private static async partial Task<Bitmap?> CreateThumbnailAsync(string path, TimeSpan? duration, CancellationToken cancellationToken)
    {
        // Without ffmpeg the entry still works and shows the placeholder.
        if (ExternalTool.Find("ffmpeg") is not { } ffmpeg) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            // One frame a second in skips the first, often blank, recording frame; short clips use the start.
            var at = duration is { } length && length >= TimeSpan.FromSeconds(2) ? 1d : 0d;
            var (exit, png, _) = await ExternalTool.RunBinaryAsync(ffmpeg,
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", "file:" + path, "-frames:v", "1", "-an", "-sn", "-dn",
                "-vf", "scale=w=240:h=240:force_original_aspect_ratio=decrease", "-f", "image2pipe", "-c:v", "png", "pipe:1"
            ], timeout.Token).ConfigureAwait(false);
            if (exit != 0 || png.Length == 0 || png.Length > MaximumThumbnailBytes) return null;
            return await Task.Run(() => DecodeThumbnail(png), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        { SettingsStore.Log(exception); return null; }
    }
}

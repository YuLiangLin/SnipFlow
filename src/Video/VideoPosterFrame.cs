using System.Globalization;

namespace SnipFlow.Video;

/// <summary>
/// Single preview frames decoded by ffmpeg. Used when libvlc is not installed, so the editor can still
/// show the picture at the playhead while trimming and placing overlays.
/// </summary>
internal static class VideoPosterFrame
{
    internal const int MaximumSide = 1280;

    internal static bool IsAvailable => ExternalTool.IsAvailable("ffmpeg");

    /// <summary>The frame at <paramref name="time"/> fitted within <see cref="MaximumSide"/>, or null.</summary>
    internal static async Task<Bitmap?> ExtractAsync(VideoInfo source, TimeSpan time, CancellationToken cancellationToken)
    {
        if (ExternalTool.Find("ffmpeg") is not { } ffmpeg) return null;
        var scale = Math.Min(1d, MaximumSide / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(2, (int)Math.Round(source.Width * scale) & ~1);
        var height = Math.Max(2, (int)Math.Round(source.Height * scale) & ~1);
        var seconds = Math.Clamp(time.TotalSeconds, 0, Math.Max(0, source.Duration.TotalSeconds - 0.001));
        var (exit, pixels, error) = await ExternalTool.RunBinaryAsync(ffmpeg,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", "file:" + source.SourcePath, "-frames:v", "1", "-an", "-sn", "-dn",
            "-vf", $"scale={width}:{height}", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"
        ], cancellationToken, timeout: TimeSpan.FromSeconds(20));
        if (exit != 0 || pixels.Length < width * height * 4)
        {
            if (exit != 0) SettingsStore.Log(new InvalidOperationException("ffmpeg poster frame: " + error.Trim()));
            return null;
        }
        return ImageTools.FromBgra(pixels, width, height);
    }
}

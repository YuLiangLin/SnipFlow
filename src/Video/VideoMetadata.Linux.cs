using System.Globalization;
using System.Text.Json;

namespace SnipFlow.Video;

/// <summary>Stream facts the Linux export needs in addition to <see cref="VideoInfo"/>.</summary>
internal sealed record VideoProbe(VideoInfo Info, bool HasAudio, int AudioChannels, int AudioSampleRate, double FrameRate);

internal static partial class VideoMetadata
{
    private const int MaximumProbeBytes = 4 * 1024 * 1024;

    private static async partial Task<VideoInfo> LoadNativeAsync(string full, CancellationToken cancellationToken)
        => (await ProbeAsync(full, cancellationToken)).Info;

    /// <summary>Reads size, duration, frame rate and audio presence of a local file with ffprobe.</summary>
    internal static async Task<VideoProbe> ProbeAsync(string full, CancellationToken cancellationToken)
    {
        var ffprobe = ExternalTool.Find("ffprobe")
            ?? throw new InvalidOperationException(I18n.T("讀取影片需要 ffprobe。請安裝 ffmpeg：sudo apt install ffmpeg"));
        // "file:" keeps names that contain ':' from being read as a protocol.
        var result = await ExternalTool.RunAsync(ffprobe,
            ["-hide_banner", "-v", "error", "-show_streams", "-show_format", "-of", "json", "file:" + full],
            cancellationToken, timeout: TimeSpan.FromSeconds(30));
        if (!result.Succeeded)
            throw new InvalidOperationException(I18n.F("無法讀取影片資訊：{0}", ErrorSummary(result.StandardError)));
        if (result.StandardOutput.Length > MaximumProbeBytes)
            throw new InvalidOperationException(I18n.F("無法讀取影片資訊：{0}", "ffprobe"));
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            JsonElement? video = null;
            var hasAudio = false;
            int channels = 0, sampleRate = 0;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = Text(stream, "codec_type");
                    if (type == "video" && video is null && !IsAttachedPicture(stream)) video = stream;
                    else if (type == "audio" && !hasAudio)
                    {
                        hasAudio = true;
                        channels = (int)Math.Clamp(Number(stream, "channels") ?? 2, 1, 64);
                        sampleRate = (int)Math.Clamp(Number(stream, "sample_rate") ?? 48000, 1, 768000);
                    }
                }
            if (video is not { } v) throw new InvalidOperationException(I18n.T("影片沒有可用的視訊軌。"));
            var width = (int)Math.Clamp(Number(v, "width") ?? 0, 0, int.MaxValue);
            var height = (int)Math.Clamp(Number(v, "height") ?? 0, 0, int.MaxValue);
            // Decoders apply the display matrix, so a quarter turn swaps the visible width and height.
            if (Math.Abs(Rotation(v)) % 180 == 90) (width, height) = (height, width);
            var seconds = root.TryGetProperty("format", out var format) ? Number(format, "duration") : null;
            seconds ??= Number(v, "duration");
            if (seconds is not { } value || !double.IsFinite(value) || value <= 0 || value > TimeSpan.FromDays(7).TotalSeconds)
                throw new InvalidOperationException(I18n.T("影片尺寸或長度不受支援。"));
            var frameRate = Fraction(Text(v, "avg_frame_rate")) ?? Fraction(Text(v, "r_frame_rate")) ?? 30;
            if (!double.IsFinite(frameRate) || frameRate <= 0 || frameRate > 1000) frameRate = 30;
            var duration = TimeSpan.FromTicks((long)Math.Round(value * TimeSpan.TicksPerSecond));
            return new VideoProbe(new VideoInfo(full, width, height, duration), hasAudio, channels, sampleRate, frameRate);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(I18n.F("無法讀取影片資訊：{0}", error.Message), error);
        }
    }

    private static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object
            && disposition.TryGetProperty("attached_pic", out var attached) && attached.ValueKind == JsonValueKind.Number
            && attached.GetInt32() == 1;

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            foreach (var item in sideData.EnumerateArray())
                if (Number(item, "rotation") is { } rotation && double.IsFinite(rotation))
                    return (int)Math.Round(rotation);
        if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            && Number(tags, "rotate") is { } rotate && double.IsFinite(rotate))
            return (int)Math.Round(rotate);
        return 0;
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;

    /// <summary>ffprobe writes most numbers as strings ("8.000000") and some as JSON numbers.</summary>
    private static double? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static double? Fraction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
            return numerator / denominator is var rate && rate > 0 ? rate : null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && plain > 0 ? plain : null;
    }

    internal static string ErrorSummary(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

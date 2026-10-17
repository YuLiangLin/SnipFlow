using System.Text.Json;
using System.Text.Json.Serialization;
using SnipFlow.Services;

namespace SnipFlow.Video;

internal static class VideoProjectStore
{
    private const long MaximumProjectBytes = 100L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, MaxDepth = 16,
        Converters = { new OverlayArrayConverter(), new BoundedStringConverter(), new ImageBytesConverter() }
    };

    internal static async Task<VideoEditDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".snipvideo", StringComparison.OrdinalIgnoreCase))
            return new(await VideoMetadata.LoadAsync(full, cancellationToken));
        var file = new FileInfo(full);
        if (!file.Exists || file.Length < 2 || file.Length > MaximumProjectBytes)
            throw new InvalidOperationException(I18n.T("影片專案不存在或過大。"));
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var saved = await JsonSerializer.DeserializeAsync<ProjectData>(stream, Json, cancellationToken)
            ?? throw new InvalidOperationException(I18n.T("影片專案格式無效。"));
        if (saved.Version != 1 || string.IsNullOrWhiteSpace(saved.SourcePath) || saved.Overlays is null
            || saved.Overlays.Length > VideoEditDocument.MaximumOverlays)
            throw new InvalidOperationException(I18n.T("影片專案格式無效。"));
        VideoFileGuard.ValidateProjectSource(saved.SourcePath);
        // The video stays at its original location. Images are embedded in the project.
        var source = await VideoMetadata.LoadAsync(saved.SourcePath, cancellationToken);
        if (source.Width != saved.Width || source.Height != saved.Height
            || Math.Abs((source.Duration - saved.SourceDuration).TotalMilliseconds) > 100)
            throw new InvalidOperationException(I18n.T("原始影片已變更，無法套用這份編輯專案。"));
        var document = new VideoEditDocument(source) { TrimStart = saved.TrimStart, TrimEnd = saved.TrimEnd, ProjectPath = full };
        foreach (var overlay in saved.Overlays)
        {
            if (overlay is null) throw new InvalidOperationException(I18n.T("影片專案格式無效。"));
            document.Overlays.Add(overlay);
        }
        document.Validate();
        document.IsDirty = false;
        return document;
    }

    internal static async Task<string> SaveAsync(VideoEditDocument document, string destination, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var snapshot = document.CreateSnapshot();
        snapshot.Validate();
        VideoFileGuard.ValidateProjectSource(snapshot.Source.SourcePath);
        var output = VideoFileGuard.ValidateOutput(destination, snapshot.Source.SourcePath, ".snipvideo", overwrite);
        var partial = VideoFileGuard.CreatePartial(output, ".snipvideo");
        var published = false;
        try
        {
            var data = new ProjectData
            {
                Version = 1, SourcePath = snapshot.Source.SourcePath, Width = snapshot.Source.Width,
                Height = snapshot.Source.Height, SourceDuration = snapshot.Source.Duration,
                TrimStart = snapshot.TrimStart, TrimEnd = snapshot.TrimEnd, Overlays = snapshot.Overlays.ToArray()
            };
            await using (var stream = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, data, Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                if (stream.Length > MaximumProjectBytes) throw new InvalidOperationException(I18n.T("影片專案不存在或過大。"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            VideoFileGuard.ValidateOutput(output, snapshot.Source.SourcePath, ".snipvideo", overwrite);
            File.Move(partial, output, overwrite);
            published = true;
            document.ProjectPath = output;
            document.IsDirty = false;
            return output;
        }
        finally { if (!published) VideoFileGuard.DeleteOwnedFile(partial); }
    }

    private sealed class ProjectData
    {
        public ProjectData() { }
        public int Version { get; set; }
        public string SourcePath { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public TimeSpan SourceDuration { get; set; }
        public TimeSpan TrimStart { get; set; }
        public TimeSpan TrimEnd { get; set; }
        public VideoOverlay[]? Overlays { get; set; }
    }

    private sealed class OverlayArrayConverter : JsonConverter<VideoOverlay[]>
    {
        public override VideoOverlay[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an overlay array.");
            var result = new List<VideoOverlay>(VideoEditDocument.MaximumOverlays);
            long bytes = 0, pixels = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return result.ToArray();
                if (result.Count >= VideoEditDocument.MaximumOverlays)
                    throw new JsonException("Too many video overlays.");
                var item = JsonSerializer.Deserialize<VideoOverlay>(ref reader, options)
                    ?? throw new JsonException("Null video overlay.");
                if (item.Text.Length > 8000 || item.ColorHex.Length > 9)
                    throw new JsonException("Video overlay text is too large.");
                if (item.ImagePng is { } image)
                {
                    var dimensions = VideoOverlayRenderer.ValidatePng(image);
                    bytes += image.Length;
                    pixels += (long)dimensions.Width * dimensions.Height;
                    if (bytes > VideoEditDocument.MaximumTotalImageBytes || pixels > 40_000_000)
                        throw new JsonException("Video project images are too large.");
                }
                result.Add(item);
            }
            throw new JsonException("Incomplete overlay array.");
        }

        public override void Write(Utf8JsonWriter writer, VideoOverlay[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var overlay in value) JsonSerializer.Serialize(writer, overlay, options);
            writer.WriteEndArray();
        }
    }

    private sealed class BoundedStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || TokenBytes(reader) > 65536)
                throw new JsonException("Video project text is too large.");
            return reader.GetString()!;
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    private sealed class ImageBytesConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || TokenBytes(reader) > ((long)VideoEditDocument.MaximumImageBytes + 2) / 3 * 4)
                throw new JsonException("Video project image is too large.");
            var image = reader.GetBytesFromBase64();
            if (image.Length > VideoEditDocument.MaximumImageBytes) throw new JsonException("Video project image is too large.");
            return image;
        }
        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => writer.WriteBase64StringValue(value);
    }

    private static long TokenBytes(Utf8JsonReader reader) => reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
}

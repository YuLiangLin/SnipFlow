using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnipFlow.Editor;

namespace SnipFlow.Services;

/// <summary>An internal checkpoint used only to carry the current document across an update restart.</summary>
internal static class EditorSessionStore
{
    private const string SessionKind = "SnipFlow.UpdateSession";
    private const int SchemaVersion = 1;
    private const long MaxManifestBytes = 64 * 1024 * 1024;
    private const long MaxImageBytes = 512 * 1024 * 1024;
    private const long MaxPixels = 200_000_000;
    private const int MaxAnnotations = 10_000;
    private const int MaxPoints = 1_000_000;
    private const double MaxCoordinate = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static string Root => Path.GetFullPath(Path.Combine(SettingsStore.DataRoot, "UpdateSessions"));

    internal static string Save(EditorView editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var snapshot = editor.CaptureSession();
        ValidateImageSize(snapshot.Image.PixelWidth, snapshot.Image.PixelHeight);
        if (snapshot.Annotations.Count > MaxAnnotations) throw InvalidCheckpoint();
        var manifest = new SessionManifest
        {
            SchemaVersion = SchemaVersion,
            Kind = SessionKind,
            SessionId = Guid.NewGuid().ToString("N"),
            PixelWidth = snapshot.Image.PixelWidth,
            PixelHeight = snapshot.Image.PixelHeight,
            HasEdits = snapshot.HasEdits,
            Annotations = snapshot.Annotations.Select(ToData).ToList()
        };
        ValidateAnnotations(manifest.Annotations);
        Directory.CreateDirectory(SettingsStore.DataRoot);
        RejectReparsePoint(SettingsStore.DataRoot);
        Directory.CreateDirectory(Root);
        RejectReparsePoint(Root);
        var directory = Path.Combine(Root, manifest.SessionId);
        if (Directory.Exists(directory) || File.Exists(directory)) throw InvalidCheckpoint();
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);
        WritePng(snapshot.Image, Path.Combine(directory, "original.png"));
        for (var index = 0; index < snapshot.Annotations.Count; index++)
        {
            var item = snapshot.Annotations[index];
            if (item.Tool != AnnotationTool.Mosaic) continue;
            if (item.Mosaic is null) throw InvalidCheckpoint();
            var fileName = MosaicFileName(index);
            manifest.Annotations[index].MosaicFile = fileName;
            WritePng(item.Mosaic, Path.Combine(directory, fileName));
        }

        // Assets are durable before the manifest is atomically published. No existing file is replaced.
        var temporary = Path.Combine(directory, "manifest.json.tmp");
        using (var stream = CreateNewFile(temporary))
        {
            JsonSerializer.Serialize(stream, manifest, JsonOptions);
            if (stream.Length > MaxManifestBytes) throw InvalidCheckpoint();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path.Combine(directory, "manifest.json"));
        return directory;
    }

    internal static void Restore(EditorView editor, string directory)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.VerifyAccess();
        var checkpoint = ValidateDirectory(directory);
        SessionManifest manifest;
        using (var stream = OpenFile(Path.Combine(checkpoint, "manifest.json"), MaxManifestBytes))
            manifest = JsonSerializer.Deserialize<SessionManifest>(stream, JsonOptions) ?? throw InvalidCheckpoint();
        if (manifest.SchemaVersion != SchemaVersion || manifest.Kind != SessionKind
            || manifest.SessionId != Path.GetFileName(checkpoint) || manifest.Annotations is null
            || manifest.Annotations.Count > MaxAnnotations)
            throw InvalidCheckpoint();
        ValidateImageSize(manifest.PixelWidth, manifest.PixelHeight);
        ValidateAnnotations(manifest.Annotations);
        var original = ReadPng(Path.Combine(checkpoint, "original.png"));
        if (original.PixelWidth != manifest.PixelWidth || original.PixelHeight != manifest.PixelHeight)
            throw InvalidCheckpoint();
        var annotations = new List<AnnotationItem>(manifest.Annotations.Count);
        for (var index = 0; index < manifest.Annotations.Count; index++)
        {
            var data = manifest.Annotations[index];
            var item = FromData(data);
            if (item.Tool == AnnotationTool.Mosaic)
            {
                // Manifest values cannot select a path; only this checkpoint's generated asset names are allowed.
                var fileName = MosaicFileName(index);
                if (data.MosaicFile != fileName) throw InvalidCheckpoint();
                item.Mosaic = ReadPng(Path.Combine(checkpoint, fileName));
            }
            else if (data.MosaicFile is not null)
                throw InvalidCheckpoint();
            annotations.Add(item);
        }
        editor.RestoreSession(original, annotations, manifest.HasEdits);
    }

    private static string ValidateDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw InvalidCheckpoint();
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(normalized, Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(normalized), Root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(normalized), "N", out _))
            throw InvalidCheckpoint();
        RejectReparsePoint(SettingsStore.DataRoot);
        RejectReparsePoint(Root);
        RejectReparsePoint(normalized);
        if (!Directory.Exists(normalized)) throw InvalidCheckpoint();
        return normalized;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw InvalidCheckpoint();
    }

    private static FileStream CreateNewFile(string path) => new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 64 * 1024, FileOptions.WriteThrough);

    private static FileStream OpenFile(string path, long maximumBytes)
    {
        RejectReparsePoint(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 0 && stream.Length <= maximumBytes) return stream;
        stream.Dispose();
        throw InvalidCheckpoint();
    }

    private static void WritePng(BitmapSource image, string path)
    {
        ValidateImageSize(image.PixelWidth, image.PixelHeight);
        using var stream = CreateNewFile(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        if (stream.Length > MaxImageBytes) throw InvalidCheckpoint();
        stream.Flush(flushToDisk: true);
    }

    private static BitmapSource ReadPng(string path)
    {
        using var stream = OpenFile(path, MaxImageBytes);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(header[8..12]) != 13
            || !header[12..16].SequenceEqual("IHDR"u8))
            throw InvalidCheckpoint();
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        ValidateImageSize(width, height);
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != width || decoder.Frames[0].PixelHeight != height)
            throw InvalidCheckpoint();
        var image = decoder.Frames[0].CloneCurrentValue();
        image.Freeze();
        return image;
    }

    private static void ValidateImageSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxCoordinate || height > MaxCoordinate || (long)width * height > MaxPixels)
            throw InvalidCheckpoint();
    }

    private static void ValidateAnnotations(List<AnnotationData> annotations)
    {
        long points = 0;
        foreach (var data in annotations)
        {
            if (data is null || !Enum.TryParse<AnnotationTool>(data.Tool, out var tool)
                || !Enum.IsDefined(tool) || tool == AnnotationTool.Select || data.Tool != tool.ToString()
                || data.Start is null || data.End is null || data.Points is null || data.Text is null
                || data.Text.Length > MaxPoints || !double.IsFinite(data.Width) || data.Width is < 1 or > 32
                || !double.IsFinite(data.FontSize) || data.FontSize is < 12 or > 240
                || !ValidExtent(data.TextWidth) || !ValidExtent(data.TextBoxHeight))
                throw InvalidCheckpoint();
            ValidatePoint(data.Start);
            ValidatePoint(data.End);
            points += data.Points.Count;
            if (points > MaxPoints) throw InvalidCheckpoint();
            foreach (var point in data.Points) ValidatePoint(point);
            if (tool is AnnotationTool.Pen or AnnotationTool.Highlight)
            {
                if (data.Points.Count == 0) throw InvalidCheckpoint();
            }
            else if (data.Points.Count != 0)
                throw InvalidCheckpoint();
        }
    }

    private static bool ValidExtent(double value) => double.IsFinite(value) && value >= 0 && value <= MaxCoordinate;
    private static void ValidatePoint(PointData point)
    {
        if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > MaxCoordinate || Math.Abs(point.Y) > MaxCoordinate)
            throw InvalidCheckpoint();
    }

    private static AnnotationData ToData(AnnotationItem item) => new()
    {
        Tool = item.Tool.ToString(), Start = ToData(item.Start), End = ToData(item.End),
        Argb = ((uint)item.Color.A << 24) | ((uint)item.Color.R << 16) | ((uint)item.Color.G << 8) | item.Color.B,
        Width = item.Width, Points = item.Points.Select(ToData).ToList(), Text = item.Text,
        FontSize = item.FontSize, TextWidth = item.TextWidth, TextBoxHeight = item.TextBoxHeight, MosaicFile = null
    };
    private static PointData ToData(Point point) => new() { X = point.X, Y = point.Y };
    private static AnnotationItem FromData(AnnotationData data) => new()
    {
        Tool = Enum.Parse<AnnotationTool>(data.Tool), Start = FromData(data.Start), End = FromData(data.End),
        Color = Color.FromArgb((byte)(data.Argb >> 24), (byte)(data.Argb >> 16), (byte)(data.Argb >> 8), (byte)data.Argb),
        Width = data.Width, Points = data.Points.Select(FromData).ToList(), Text = data.Text,
        FontSize = data.FontSize, TextWidth = data.TextWidth, TextBoxHeight = data.TextBoxHeight
    };
    private static Point FromData(PointData point) => new(point.X, point.Y);
    private static string MosaicFileName(int index) => $"mosaic-{index:D4}.png";
    private static InvalidDataException InvalidCheckpoint() => new("The SnipFlow update checkpoint is invalid or unsupported.");

    private sealed class SessionManifest
    {
        public required int SchemaVersion { get; init; }
        public required string Kind { get; init; }
        public required string SessionId { get; init; }
        public required int PixelWidth { get; init; }
        public required int PixelHeight { get; init; }
        public required bool HasEdits { get; init; }
        public required List<AnnotationData> Annotations { get; init; }
    }

    private sealed class AnnotationData
    {
        public required string Tool { get; init; }
        public required PointData Start { get; init; }
        public required PointData End { get; init; }
        public required uint Argb { get; init; }
        public required double Width { get; init; }
        public required List<PointData> Points { get; init; }
        public required string Text { get; init; }
        public required double FontSize { get; init; }
        public required double TextWidth { get; init; }
        public required double TextBoxHeight { get; init; }
        public required string? MosaicFile { get; set; }
    }

    private sealed class PointData
    {
        public required double X { get; init; }
        public required double Y { get; init; }
    }
}

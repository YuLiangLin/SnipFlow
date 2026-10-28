using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnipFlow.Editor;

namespace SnipFlow.Services;

/// <summary>Stores editable documents and checkpoints used across an update restart.</summary>
internal static class EditorSessionStore
{
    private const string SessionKind = "SnipFlow.UpdateSession";
    private const int SchemaVersion = 4;
    private const long MaxManifestBytes = 64 * 1024 * 1024;
    private const long MaxImageBytes = 512 * 1024 * 1024;
    private const long MaxProjectBytes = 1024L * 1024 * 1024;
    private const int MaxCentralDirectoryBytes = 4 * 1024 * 1024;
    private const long MaxArchiveBytes = MaxProjectBytes + MaxCentralDirectoryBytes + 65_557;
    private const long MaxPixels = 200_000_000;
    private const int MaxAnnotations = 10_000;
    private const int MaxProjectEntries = MaxAnnotations + 2;
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
        editor.VerifyAccess();
        return SaveSnapshot(editor.CaptureSession());
    }

    internal static void SaveProject(EditorView editor, string path)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.VerifyAccess();
        SaveProject(editor.CaptureSession(), path);
    }

    // The caller owns the captured annotation clones. Bitmaps are thread-agile and can be encoded on a worker thread.
    internal static void SaveProject(EditorSessionSnapshot snapshot, string path)
    {
        var destination = NormalizeProjectPath(path);
        ValidateSnapshot(snapshot);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        if (File.Exists(destination)) RejectReparsePoint(destination);
        var temporary = Path.Combine(parent, ".snipflow-" + Guid.NewGuid().ToString("N") + ".tmp");
        string? checkpoint = null;
        var temporaryCreated = false;
        try
        {
            checkpoint = SaveSnapshot(snapshot);
            var manifest = ReadManifest(Path.Combine(checkpoint, "manifest.json"));
            using (var stream = CreateNewFile(temporary))
            {
                temporaryCreated = true;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var name in CheckpointFileNames(manifest))
                    {
                        var entry = archive.CreateEntry(name, name == "manifest.json"
                            ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                        using var source = OpenFile(Path.Combine(checkpoint, name),
                            name == "manifest.json" ? MaxManifestBytes : MaxImageBytes);
                        using var output = entry.Open();
                        source.CopyTo(output);
                    }
                }
                if (stream.Length > MaxArchiveBytes) throw InvalidCheckpoint();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(destination))
            {
                RejectReparsePoint(destination);
                File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
                File.Move(temporary, destination);
            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated) TryDeleteOwnFile(temporary);
            if (checkpoint is not null) TryDeleteOwnCheckpoint(checkpoint);
        }
    }

    internal static void RestoreProject(EditorView editor, string path)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.VerifyAccess();
        using var source = OpenFile(NormalizeProjectPath(path), MaxArchiveBytes);
        var entryCount = ValidateZipDirectory(source);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != entryCount) throw InvalidCheckpoint();
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long declaredBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (!IsCheckpointFileName(entry.FullName) || !entries.TryAdd(entry.FullName, entry)
                || entry.Length <= 0 || entry.Length > (entry.FullName == "manifest.json" ? MaxManifestBytes : MaxImageBytes))
                throw InvalidCheckpoint();
            declaredBytes += entry.Length;
            if (declaredBytes > MaxProjectBytes) throw InvalidCheckpoint();
        }
        if (!entries.TryGetValue("manifest.json", out var manifestEntry)) throw InvalidCheckpoint();
        long extractedBytes = 0;
        SessionManifest manifest;
        using (var manifestStream = new MemoryStream())
        {
            CopyEntry(manifestEntry, manifestStream, MaxManifestBytes, ref extractedBytes);
            manifestStream.Position = 0;
            manifest = JsonSerializer.Deserialize<SessionManifest>(manifestStream, JsonOptions) ?? throw InvalidCheckpoint();
        }
        ValidateManifest(manifest);
        var expected = CheckpointFileNames(manifest).ToHashSet(StringComparer.Ordinal);
        if (expected.Count != entries.Count || entries.Keys.Any(name => !expected.Contains(name))) throw InvalidCheckpoint();

        string? checkpoint = null;
        try
        {
            manifest = new SessionManifest
            {
                SchemaVersion = manifest.SchemaVersion,
                Kind = manifest.Kind,
                SessionId = Guid.NewGuid().ToString("N"),
                PixelWidth = manifest.PixelWidth,
                PixelHeight = manifest.PixelHeight,
                HasEdits = false,
                IsCollage = manifest.IsCollage,
                Annotations = manifest.Annotations
            };
            checkpoint = CreateCheckpointDirectory(manifest.SessionId);
            foreach (var name in expected)
            {
                if (name == "manifest.json") continue;
                using var target = CreateNewFile(Path.Combine(checkpoint, name));
                CopyEntry(entries[name], target, MaxImageBytes, ref extractedBytes);
                target.Flush(flushToDisk: true);
            }
            WriteManifest(manifest, checkpoint);
            // Restore validates every PNG before changing the editor. ZIP paths are never extracted.
            Restore(editor, checkpoint);
        }
        finally
        {
            if (checkpoint is not null) TryDeleteOwnCheckpoint(checkpoint);
        }
    }

    private static string SaveSnapshot(EditorSessionSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        var manifest = new SessionManifest
        {
            SchemaVersion = SchemaVersion,
            Kind = SessionKind,
            SessionId = Guid.NewGuid().ToString("N"),
            PixelWidth = snapshot.Image.PixelSize.Width,
            PixelHeight = snapshot.Image.PixelSize.Height,
            HasEdits = snapshot.HasEdits,
            IsCollage = snapshot.IsCollage,
            Annotations = snapshot.Annotations.Select(ToData).ToList()
        };
        ValidateManifest(manifest);
        string? directory = null;
        try
        {
            directory = CreateCheckpointDirectory(manifest.SessionId);
            var writtenBytes = WritePng(snapshot.Image, Path.Combine(directory, "original.png"));
            for (var index = 0; index < snapshot.Annotations.Count; index++)
            {
                var item = snapshot.Annotations[index];
                var image = item.Tool == AnnotationTool.Mosaic ? item.Mosaic
                    : item.Tool == AnnotationTool.Image ? item.Image : null;
                if (image is null) continue;
                var name = item.Tool == AnnotationTool.Mosaic ? MosaicFileName(index) : ImageFileName(index);
                writtenBytes += WritePng(image, Path.Combine(directory, name));
                if (writtenBytes > MaxProjectBytes) throw InvalidCheckpoint();
            }
            writtenBytes += WriteManifest(manifest, directory);
            if (writtenBytes > MaxProjectBytes) throw InvalidCheckpoint();
            return directory;
        }
        catch
        {
            if (directory is not null) TryDeleteOwnCheckpoint(directory);
            throw;
        }
    }

    internal static void Restore(EditorView editor, string directory)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.VerifyAccess();
        var checkpoint = ValidateDirectory(directory);
        var manifest = ReadManifest(Path.Combine(checkpoint, "manifest.json"));
        if (manifest.SessionId != Path.GetFileName(checkpoint)) throw InvalidCheckpoint();
        ValidatePngFiles(checkpoint, manifest);
        var original = ReadPng(Path.Combine(checkpoint, "original.png"));
        if (original.PixelSize.Width != manifest.PixelWidth || original.PixelSize.Height != manifest.PixelHeight)
            throw InvalidCheckpoint();
        var annotations = new List<AnnotationItem>(manifest.Annotations.Count);
        for (var index = 0; index < manifest.Annotations.Count; index++)
        {
            var data = manifest.Annotations[index];
            var item = FromData(data);
            if (item.Tool == AnnotationTool.Mosaic)
                item.Mosaic = ReadPng(Path.Combine(checkpoint, MosaicFileName(index)));
            else if (item.Tool == AnnotationTool.Image)
                item.Image = ReadPng(Path.Combine(checkpoint, ImageFileName(index)));
            annotations.Add(item);
        }
        editor.RestoreSession(original, annotations, manifest.HasEdits, manifest.IsCollage);
    }

    private static void ValidateSnapshot(EditorSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Image is null || snapshot.Annotations is null || snapshot.Annotations.Count > MaxAnnotations)
            throw InvalidCheckpoint();
        long pixels = ImagePixels(snapshot.Image);
        foreach (var item in snapshot.Annotations)
        {
            if (item is null)
                throw InvalidCheckpoint();
            if (!ImageOrientation.IsValid(item.TextOrientation) || (item.Tool != AnnotationTool.Text && item.TextOrientation != 0))
                throw InvalidCheckpoint();
            if (item.ImageCrop is { } crop && (item.Tool != AnnotationTool.Image || !ImageCropGeometry.IsValid(crop)))
                throw InvalidCheckpoint();
            if (item.Tool == AnnotationTool.Mosaic)
                pixels += ImagePixels(item.Mosaic ?? throw InvalidCheckpoint());
            else if (item.Tool == AnnotationTool.Image)
                pixels += ImagePixels(item.Image ?? throw InvalidCheckpoint());
            if (pixels > MaxPixels) throw InvalidCheckpoint();
        }
    }

    private static long ImagePixels(Bitmap image)
    {
        ValidateImageSize(image.PixelSize.Width, image.PixelSize.Height);
        return (long)image.PixelSize.Width * image.PixelSize.Height;
    }

    private static string CreateCheckpointDirectory(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw InvalidCheckpoint();
        Directory.CreateDirectory(SettingsStore.DataRoot);
        RejectReparsePoint(SettingsStore.DataRoot);
        Directory.CreateDirectory(Root);
        RejectReparsePoint(Root);
        var directory = Path.Combine(Root, sessionId);
        if (Directory.Exists(directory) || File.Exists(directory)) throw InvalidCheckpoint();
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);
        return directory;
    }

    private static SessionManifest ReadManifest(string path)
    {
        using var stream = OpenFile(path, MaxManifestBytes);
        var manifest = JsonSerializer.Deserialize<SessionManifest>(stream, JsonOptions) ?? throw InvalidCheckpoint();
        ValidateManifest(manifest);
        return manifest;
    }

    private static long WriteManifest(SessionManifest manifest, string directory)
    {
        var temporary = Path.Combine(directory, "manifest.json.tmp");
        long bytes;
        using (var stream = CreateNewFile(temporary))
        {
            JsonSerializer.Serialize(stream, manifest, JsonOptions);
            bytes = stream.Length;
            if (bytes > MaxManifestBytes) throw InvalidCheckpoint();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path.Combine(directory, "manifest.json"));
        return bytes;
    }

    private static void ValidateManifest(SessionManifest manifest)
    {
        if (manifest.SchemaVersion is not (1 or 2 or 3 or SchemaVersion) || manifest.Kind != SessionKind
            || !Guid.TryParseExact(manifest.SessionId, "N", out _) || manifest.Annotations is null
            || manifest.Annotations.Count > MaxAnnotations)
            throw InvalidCheckpoint();
        ValidateImageSize(manifest.PixelWidth, manifest.PixelHeight);
        ValidateAnnotations(manifest.Annotations);
        if (manifest.SchemaVersion < 3 && manifest.Annotations.Any(data => data.TextOrientation != 0))
            throw InvalidCheckpoint();
        if (manifest.SchemaVersion < 4 && manifest.Annotations.Any(data => data.ImageCrop is not null))
            throw InvalidCheckpoint();
        if (manifest.SchemaVersion == 1 && (manifest.IsCollage
            || manifest.Annotations.Any(data => data.Tool == AnnotationTool.Image.ToString() || data.ImageFile is not null)))
            throw InvalidCheckpoint();
    }

    private static IEnumerable<string> CheckpointFileNames(SessionManifest manifest)
    {
        yield return "manifest.json";
        yield return "original.png";
        for (var index = 0; index < manifest.Annotations.Count; index++)
        {
            if (manifest.Annotations[index].Tool == AnnotationTool.Mosaic.ToString()) yield return MosaicFileName(index);
            else if (manifest.Annotations[index].Tool == AnnotationTool.Image.ToString()) yield return ImageFileName(index);
        }
    }

    private static string ValidateDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw InvalidCheckpoint();
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(normalized, Path.TrimEndingDirectorySeparator(directory), PlatformInfo.PathComparison)
            || !string.Equals(Path.GetDirectoryName(normalized), Root, PlatformInfo.PathComparison)
            || !Guid.TryParseExact(Path.GetFileName(normalized), "N", out _))
            throw InvalidCheckpoint();
        RejectReparsePoint(SettingsStore.DataRoot);
        RejectReparsePoint(Root);
        RejectReparsePoint(normalized);
        if (!Directory.Exists(normalized)) throw InvalidCheckpoint();
        return normalized;
    }

    private static string NormalizeProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The project path must be absolute.", nameof(path));
        var normalized = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(normalized), ".snipflow", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The editable project must use the .snipflow extension.", nameof(path));
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

    private static long WritePng(Bitmap image, string path)
    {
        ValidateImageSize(image.PixelSize.Width, image.PixelSize.Height);
        using var stream = CreateNewFile(path);
        // Lossless PNG; the WPF version wrote the same format, so projects open in both versions.
        ImageTools.SavePng(image, stream);
        if (stream.Length > MaxImageBytes) throw InvalidCheckpoint();
        stream.Flush(flushToDisk: true);
        return stream.Length;
    }

    private static void ValidatePngFiles(string directory, SessionManifest manifest)
    {
        long pixels = 0;
        long bytes = new FileInfo(Path.Combine(directory, "manifest.json")).Length;
        foreach (var name in CheckpointFileNames(manifest))
        {
            if (name == "manifest.json") continue;
            using var stream = OpenFile(Path.Combine(directory, name), MaxImageBytes);
            bytes += stream.Length;
            var (width, height) = ReadPngHeader(stream);
            pixels += (long)width * height;
            if (bytes > MaxProjectBytes || pixels > MaxPixels) throw InvalidCheckpoint();
            if (name == "original.png" && (width != manifest.PixelWidth || height != manifest.PixelHeight))
                throw InvalidCheckpoint();
        }
    }

    private static (int Width, int Height) ReadPngHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(header[8..12]) != 13
            || !header[12..16].SequenceEqual("IHDR"u8))
            throw InvalidCheckpoint();
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        ValidateImageSize(width, height);
        return (width, height);
    }

    private static Bitmap ReadPng(string path)
    {
        using var stream = OpenFile(path, MaxImageBytes);
        var (width, height) = ReadPngHeader(stream);
        stream.Position = 0;
        // Decoded at 96 DPI so the PNG's pHYs density never changes document coordinates.
        var image = ImageTools.Decode(stream);
        if (image.PixelSize.Width != width || image.PixelSize.Height != height)
        {
            image.Dispose();
            throw InvalidCheckpoint();
        }
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
        for (var index = 0; index < annotations.Count; index++)
        {
            var data = annotations[index];
            if (data is null || !Enum.TryParse<AnnotationTool>(data.Tool, out var tool)
                || !Enum.IsDefined(tool) || tool == AnnotationTool.Select || data.Tool != tool.ToString()
                || data.Start is null || data.End is null || data.Points is null || data.Text is null
                || data.Text.Length > MaxPoints || !double.IsFinite(data.Width) || data.Width is < 1 or > 32
                || !double.IsFinite(data.FontSize) || data.FontSize is < 12 or > 240
                || !ValidExtent(data.TextWidth) || !ValidExtent(data.TextBoxHeight)
                || !ImageOrientation.IsValid(data.TextOrientation) || (tool != AnnotationTool.Text && data.TextOrientation != 0))
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
            if (data.MosaicFile != (tool == AnnotationTool.Mosaic ? MosaicFileName(index) : null)
                || data.ImageFile != (tool == AnnotationTool.Image ? ImageFileName(index) : null))
                throw InvalidCheckpoint();
            if (data.ImageCrop is { } crop && (tool != AnnotationTool.Image
                || !ImageCropGeometry.IsValid(new Rect(crop.X, crop.Y, crop.Width, crop.Height))))
                throw InvalidCheckpoint();
        }
    }

    // Bound the central directory before ZipArchive allocates an entry object for every record.
    // This format needs neither ZIP64 nor split archives (at most 1 GiB and 10,002 entries).
    private static int ValidateZipDirectory(FileStream stream)
    {
        var tail = new byte[(int)Math.Min(stream.Length, 65_557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) != 0x06054b50) continue;
            var record = tail.AsSpan(index);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[20..22]);
            if (index + 22 + commentLength != tail.Length) continue;
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..6]);
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..8]);
            var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[8..10]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..12]);
            var directoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[12..16]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..20]);
            var recordOffset = stream.Length - tail.Length + index;
            if (disk != 0 || directoryDisk != 0 || diskEntries != entries || entries is < 2 or > MaxProjectEntries
                || directoryBytes is < 1 or > MaxCentralDirectoryBytes
                || (long)directoryOffset + directoryBytes != recordOffset)
                throw InvalidCheckpoint();
            stream.Position = 0;
            return entries;
        }
        throw InvalidCheckpoint();
    }

    private static void CopyEntry(ZipArchiveEntry entry, Stream target, long maximumBytes, ref long totalBytes)
    {
        if (entry.Length <= 0 || entry.Length > maximumBytes) throw InvalidCheckpoint();
        using var input = entry.Open();
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            bytes += read;
            totalBytes += read;
            if (bytes > entry.Length || bytes > maximumBytes || totalBytes > MaxProjectBytes) throw InvalidCheckpoint();
            target.Write(buffer, 0, read);
        }
        if (bytes != entry.Length) throw InvalidCheckpoint();
    }

    private static bool IsCheckpointFileName(string name)
    {
        if (name is "manifest.json" or "original.png") return true;
        var prefix = name.StartsWith("mosaic-", StringComparison.Ordinal) ? "mosaic-"
            : name.StartsWith("image-", StringComparison.Ordinal) ? "image-" : null;
        if (prefix is null || name.Length != prefix.Length + 8 || !name.EndsWith(".png", StringComparison.Ordinal)) return false;
        var digits = name.AsSpan(prefix.Length, 4);
        foreach (var digit in digits)
            if (digit is < '0' or > '9') return false;
        return true;
    }

    private static void TryDeleteOwnCheckpoint(string directory)
    {
        try
        {
            var owned = ValidateDirectory(directory);
            var files = Directory.GetFileSystemEntries(owned);
            // This flat, newly generated checkpoint is the only directory this call may remove.
            foreach (var file in files)
            {
                RejectReparsePoint(file);
                if (Directory.Exists(file) || (!IsCheckpointFileName(Path.GetFileName(file))
                    && Path.GetFileName(file) != "manifest.json.tmp")) throw InvalidCheckpoint();
            }
            foreach (var file in files) File.Delete(file);
            Directory.Delete(owned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SettingsStore.Log(ex);
        }
    }

    private static void TryDeleteOwnFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            RejectReparsePoint(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SettingsStore.Log(ex);
        }
    }

    private static bool ValidExtent(double value) => double.IsFinite(value) && value >= 0 && value <= MaxCoordinate;
    private static void ValidatePoint(PointData point)
    {
        if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > MaxCoordinate || Math.Abs(point.Y) > MaxCoordinate)
            throw InvalidCheckpoint();
    }

    private static AnnotationData ToData(AnnotationItem item, int index) => new()
    {
        Tool = item.Tool.ToString(), Start = ToData(item.Start), End = ToData(item.End),
        Argb = ((uint)item.Color.A << 24) | ((uint)item.Color.R << 16) | ((uint)item.Color.G << 8) | item.Color.B,
        Width = item.Width, Points = item.Points.Select(ToData).ToList(), Text = item.Text,
        FontSize = item.FontSize, TextWidth = item.TextWidth, TextBoxHeight = item.TextBoxHeight, TextOrientation = item.TextOrientation,
        MosaicFile = item.Tool == AnnotationTool.Mosaic ? MosaicFileName(index) : null,
        ImageFile = item.Tool == AnnotationTool.Image ? ImageFileName(index) : null,
        ImageCrop = item.ImageCrop is { } crop ? new ImageCropData { X = crop.X, Y = crop.Y, Width = crop.Width, Height = crop.Height } : null
    };
    private static PointData ToData(Point point) => new() { X = point.X, Y = point.Y };
    private static AnnotationItem FromData(AnnotationData data) => new()
    {
        Tool = Enum.Parse<AnnotationTool>(data.Tool), Start = FromData(data.Start), End = FromData(data.End),
        Color = Color.FromArgb((byte)(data.Argb >> 24), (byte)(data.Argb >> 16), (byte)(data.Argb >> 8), (byte)data.Argb),
        Width = data.Width, Points = data.Points.Select(FromData).ToList(), Text = data.Text,
        FontSize = data.FontSize, TextWidth = data.TextWidth, TextBoxHeight = data.TextBoxHeight, TextOrientation = data.TextOrientation,
        ImageCrop = data.ImageCrop is { } crop ? new Rect(crop.X, crop.Y, crop.Width, crop.Height) : null
    };
    private static Point FromData(PointData point) => new(point.X, point.Y);
    private static string MosaicFileName(int index) => $"mosaic-{index:D4}.png";
    private static string ImageFileName(int index) => $"image-{index:D4}.png";
    private static InvalidDataException InvalidCheckpoint() => new("The SnipFlow project or update checkpoint is invalid or unsupported.");

    private sealed class SessionManifest
    {
        public required int SchemaVersion { get; init; }
        public required string Kind { get; init; }
        public required string SessionId { get; init; }
        public required int PixelWidth { get; init; }
        public required int PixelHeight { get; init; }
        public required bool HasEdits { get; init; }
        public bool IsCollage { get; init; }
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int TextOrientation { get; init; }
        public required string? MosaicFile { get; set; }
        public string? ImageFile { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ImageCropData? ImageCrop { get; init; }
    }

    private sealed class ImageCropData
    {
        public required double X { get; init; }
        public required double Y { get; init; }
        public required double Width { get; init; }
        public required double Height { get; init; }
    }

    private sealed class PointData
    {
        public required double X { get; init; }
        public required double Y { get; init; }
    }
}

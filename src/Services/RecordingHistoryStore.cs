using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnipFlow.Video;

namespace SnipFlow.Services;

/// <summary>Recent MP4 references and owned thumbnails. Original videos are never deleted here.</summary>
/// <remarks>
/// Thumbnails come from the Windows shell (StorageFile.GetThumbnailAsync) or, on Linux, from one
/// ffmpeg frame. Either way they are re-encoded as SnipFlow's own PNG in the VideoHistory folder.
/// </remarks>
public static partial class RecordingHistoryStore
{
    private const int MaximumEntries = 200;
    private const int MaximumIndexBytes = 2 * 1024 * 1024;
    private const int MaximumThumbnailBytes = 4 * 1024 * 1024;
    private const int MaximumStringCharacters = 32767;
    private const int MaximumStringTokenBytes = MaximumStringCharacters * 6;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        Converters = { new BoundedReferencesConverter(), new BoundedStringConverter() }
    };
    private static readonly Bitmap Placeholder = CreatePlaceholder();

    public static async Task<IReadOnlyList<HistoryItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run<IReadOnlyList<HistoryItem>>(() =>
            {
                var directory = SafeDirectory(create: false);
                var entries = ReadIndex(directory);
                var items = new List<HistoryItem>();
                foreach (var entry in entries.OrderByDescending(entry => entry.RecordedAtUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(entry.SourcePath)) continue;
                        var thumbnail = ReadThumbnail(directory, entry.Id) ?? Placeholder;
                        var duration = entry.DurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : (TimeSpan?)null;
                        var durationText = duration is { } value ? FormatDuration(value) : I18n.T("時長未取得");
                        var detail = entry.RecordedAtUtc.ToLocalTime().ToString("MM/dd  HH:mm", CultureInfo.CurrentCulture)
                            + " · " + durationText + " · MP4";
                        items.Add(new(entry.SourcePath, Path.GetFileName(entry.SourcePath), detail, thumbnail)
                        {
                            Kind = HistoryKind.Recording, RecordingId = entry.Id,
                            RecordedAtUtc = entry.RecordedAtUtc, Duration = duration
                        });
                    }
                    catch (Exception exception) when (IsFileError(exception)) { SettingsStore.Log(exception); }
                }
                return items;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public static async Task AddAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!SettingsStore.Current.KeepHistory) return;
        var source = NormalizeVideoPath(path);
        await Task.Run(() =>
        {
            if (!File.Exists(source)) throw new FileNotFoundException(I18n.T("錄影檔案已不存在。"), source);
        }, cancellationToken).ConfigureAwait(false);

        TimeSpan? duration = null;
        try
        {
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var info = await Task.Run(() => VideoMetadata.LoadAsync(source, metadataTimeout.Token), metadataTimeout.Token)
                .ConfigureAwait(false);
            source = NormalizeVideoPath(info.SourcePath);
            duration = info.Duration;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { SettingsStore.Log(exception); }
        cancellationToken.ThrowIfCancellationRequested();
        var thumbnail = await CreateThumbnailAsync(source, duration, cancellationToken).ConfigureAwait(false) ?? Placeholder;
        var png = await Task.Run(() => EncodeThumbnail(thumbnail), cancellationToken).ConfigureAwait(false);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SettingsStore.Current.KeepHistory) return;
                var directory = SafeDirectory(create: true);
                var before = ReadIndex(directory);
                var existing = before.FirstOrDefault(entry => entry.SourcePath.Equals(source, PlatformInfo.PathComparison));
                var id = existing?.Id ?? Guid.NewGuid().ToString("N");
                var entry = new VideoReference(id, source, DateTimeOffset.UtcNow, duration?.Ticks);
                var limit = Math.Clamp(SettingsStore.Current.HistoryLimit, 10, MaximumEntries);
                var after = before.Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    .Append(entry).OrderByDescending(item => item.RecordedAtUtc).Take(limit).ToArray();
                Publish(directory, ThumbnailName(id), png);
                WriteIndex(directory, after);
                foreach (var pruned in before.Where(item => !after.Any(kept => kept.Id == item.Id)))
                    TryDeleteThumbnail(directory, pruned.Id);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public static async Task<bool> DeleteAsync(HistoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsRecording || !IsId(item.RecordingId))
            throw new IOException(I18n.T("錄影歷史紀錄無效，已停止移除。"));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var directory = SafeDirectory(create: false);
                var entries = ReadIndex(directory);
                var entry = entries.FirstOrDefault(entry => entry.Id == item.RecordingId
                    && entry.SourcePath.Equals(item.Path, PlatformInfo.PathComparison));
                if (entry is null) return false;
                WriteIndex(directory, entries.Where(value => value.Id != entry.Id).ToArray());
                TryDeleteThumbnail(directory, entry.Id);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public static async Task<HistoryClearResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var directory = SafeDirectory(create: false);
                if (!Directory.Exists(directory)) return new HistoryClearResult(0, 0);
                var entries = ReadIndex(directory);
                WriteIndex(directory, Array.Empty<VideoReference>());
                var failed = 0;
                // Only fixed-schema cache names; never use a SourcePath as a deletion target.
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsThumbnailName(file.Name) && !IsTemporaryName(file.Name)) continue;
                    try { DeleteManagedFile(directory, file.Name); }
                    catch (Exception exception) when (IsFileError(exception)) { failed++; SettingsStore.Log(exception); }
                }
                return new HistoryClearResult(entries.Count, failed);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>A frame of the video scaled to at most 240 pixels, or null when none can be read in time.</summary>
    private static partial Task<Bitmap?> CreateThumbnailAsync(string path, TimeSpan? duration, CancellationToken cancellationToken);

    private static Bitmap DecodeThumbnail(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        Bitmap frame;
        try { frame = new Bitmap(stream); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new InvalidDataException(I18n.T("錄影縮圖無法讀取。"), exception); }
        var size = frame.PixelSize;
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 2048 || size.Height > 2048)
        { frame.Dispose(); throw new InvalidDataException(I18n.T("錄影縮圖無法讀取。")); }
        var scale = Math.Min(1d, 240d / Math.Max(size.Width, size.Height));
        if (scale >= 1) return frame;
        using (frame)
            return ImageTools.Scale(frame, new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)),
                Math.Max(1, (int)Math.Round(size.Height * scale))));
    }

    private static Bitmap? ReadThumbnail(string directory, string id)
    {
        try
        {
            var path = ManagedPath(directory, ThumbnailName(id));
            if (!File.Exists(path)) return null;
            return DecodeThumbnail(ReadBounded(path, MaximumThumbnailBytes));
        }
        catch (Exception exception) when (IsFileError(exception)) { SettingsStore.Log(exception); return null; }
    }

    private static byte[] EncodeThumbnail(Bitmap image)
    {
        var bytes = ImageTools.EncodePng(image);
        if (bytes.Length > MaximumThumbnailBytes) throw new IOException(I18n.T("錄影縮圖過大，已停止儲存。"));
        return bytes;
    }

    private static IReadOnlyList<VideoReference> ReadIndex(string directory)
    {
        var path = ManagedPath(directory, "index.json");
        if (!File.Exists(path)) return Array.Empty<VideoReference>();
        try
        {
            var document = JsonSerializer.Deserialize<VideoIndex>(ReadBounded(path, MaximumIndexBytes), JsonOptions);
            if (document is null || document.Version != 1 || document.Items is null || document.Items.Length > MaximumEntries)
                throw new JsonException("Invalid recording history schema.");
            var result = new List<VideoReference>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Items)
            {
                if (entry is null || !IsId(entry.Id) || entry.RecordedAtUtc == default
                    || entry.DurationTicks is < 0 || !ids.Add(entry.Id)) continue;
                try
                {
                    var source = NormalizeVideoPath(entry.SourcePath);
                    result.Add(entry with { SourcePath = source, RecordedAtUtc = entry.RecordedAtUtc.ToUniversalTime() });
                }
                catch (Exception exception) when (IsFileError(exception)) { SettingsStore.Log(exception); }
            }
            return result;
        }
        catch (JsonException exception) { throw new IOException(I18n.T("錄影歷史索引損壞，已停止操作。"), exception); }
    }

    private static void WriteIndex(string directory, IReadOnlyList<VideoReference> entries)
    {
        if (entries.Count > MaximumEntries)
            throw new IOException(I18n.T("錄影歷史索引過大，已停止儲存。"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new VideoIndex(1, entries.ToArray()), JsonOptions);
        if (bytes.Length > MaximumIndexBytes)
            throw new IOException(I18n.T("錄影歷史索引過大，已停止儲存。"));
        Publish(directory, "index.json", bytes);
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new IOException(I18n.T("錄影歷史資料過大，已停止讀取。"));
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes); return bytes;
    }

    private static void Publish(string directory, string name, byte[] bytes)
    {
        var destination = ManagedPath(directory, name);
        var temporaryName = "video-history-" + Guid.NewGuid().ToString("N") + ".tmp";
        var temporary = ManagedPath(directory, temporaryName);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            destination = ManagedPath(directory, name);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { DeleteManagedFile(directory, temporaryName); }
            catch (Exception exception) when (IsFileError(exception)) { SettingsStore.Log(exception); }
        }
    }

    private static void TryDeleteThumbnail(string directory, string id)
    {
        try { DeleteManagedFile(directory, ThumbnailName(id)); }
        catch (Exception exception) when (IsFileError(exception)) { SettingsStore.Log(exception); }
    }

    private static void DeleteManagedFile(string directory, string name)
    {
        if (!IsThumbnailName(name) && !IsTemporaryName(name))
            throw new IOException(I18n.T("錄影歷史清理僅能移除自己的縮圖與暫存資料。"));
        var path = ManagedPath(directory, name);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string SafeDirectory(bool create)
    {
        var path = Path.GetFullPath(Path.Combine(SettingsStore.DataRoot, "VideoHistory"));
        ValidateDirectoryChain(path);
        if (create) { Directory.CreateDirectory(path); ValidateDirectoryChain(path); }
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string ManagedPath(string directory, string name)
    {
        if (name != "index.json" && !IsThumbnailName(name) && !IsTemporaryName(name))
            throw new IOException(I18n.T("錄影歷史檔案不在允許的資料夾內。"));
        ValidateDirectoryChain(directory);
        var path = Path.GetFullPath(Path.Combine(directory, name));
        if (!string.Equals(Path.GetDirectoryName(path), directory, PlatformInfo.PathComparison))
            throw new IOException(I18n.T("錄影歷史檔案不在允許的資料夾內。"));
        if (TryAttributes(path, out var attributes) && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException(I18n.T("錄影歷史資料不是一般檔案，已停止操作。"));
        return path;
    }

    private static void ValidateDirectoryChain(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if (TryAttributes(current.FullName, out var attributes)
                && ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException(I18n.T("錄影歷史資料夾不是一般資料夾，已停止操作。"));
    }

    private static bool TryAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { attributes = default; return false; }
        catch (DirectoryNotFoundException) { attributes = default; return false; }
    }

    private static string NormalizeVideoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumStringCharacters || !Path.IsPathFullyQualified(path)
            || !Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new IOException(I18n.T("錄影歷史只能參照完整路徑的 MP4 檔案。"));
        var full = Path.GetFullPath(path);
        // A colon selects an NTFS alternate data stream; on Linux it is an ordinary file name character.
        if (PlatformInfo.IsWindows && Path.GetFileName(full).Contains(':')) throw new IOException(I18n.T("錄影歷史檔案路徑無效。"));
        return full;
    }

    private static bool IsId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);
    private static string ThumbnailName(string id) => IsId(id) ? "recording-" + id + ".png" : throw new IOException("Invalid thumbnail ID.");
    private static bool IsThumbnailName(string name) => name.Length == 46 && name.StartsWith("recording-", StringComparison.Ordinal)
        && name.EndsWith(".png", StringComparison.Ordinal) && IsId(name.Substring(10, 32));
    private static bool IsTemporaryName(string name) => name.Length == 50 && name.StartsWith("video-history-", StringComparison.Ordinal)
        && name.EndsWith(".tmp", StringComparison.Ordinal) && IsId(name.Substring(14, 32));
    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidDataException
        or System.Runtime.InteropServices.COMException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + duration.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static Bitmap CreatePlaceholder()
    {
        const int width = 240, height = 135, stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var play = x >= 102 && x <= 140 && Math.Abs(y - 67) <= (140 - x) * 0.65;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = play ? (byte)220 : (byte)32;
                pixels[offset + 3] = 255;
            }
        return ImageTools.FromBgra(pixels, width, height);
    }

    private sealed record VideoReference(string Id, string SourcePath, DateTimeOffset RecordedAtUtc, long? DurationTicks);
    private sealed record VideoIndex(int Version, VideoReference?[]? Items);

    private sealed class BoundedReferencesConverter : JsonConverter<VideoReference?[]>
    {
        public override VideoReference?[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Invalid recording history array.");
            var entries = new List<VideoReference?>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return entries.ToArray();
                if (entries.Count >= MaximumEntries)
                    throw new JsonException("Recording history contains too many entries.");
                entries.Add(JsonSerializer.Deserialize<VideoReference>(ref reader, options));
            }
            throw new JsonException("Incomplete recording history array.");
        }

        public override void Write(Utf8JsonWriter writer, VideoReference?[] value, JsonSerializerOptions options)
        {
            if (value.Length > MaximumEntries)
                throw new JsonException("Recording history contains too many entries.");
            writer.WriteStartArray();
            foreach (var entry in value) JsonSerializer.Serialize(writer, entry, options);
            writer.WriteEndArray();
        }
    }

    private sealed class BoundedStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > MaximumStringTokenBytes)
                throw new JsonException("Recording history string is too large or invalid.");
            var value = reader.GetString() ?? throw new JsonException("Invalid recording history string.");
            if (value.Length > MaximumStringCharacters)
                throw new JsonException("Recording history string is too large.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value.Length > MaximumStringCharacters)
                throw new JsonException("Recording history string is too large.");
            writer.WriteStringValue(value);
        }
    }
}

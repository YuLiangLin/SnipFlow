using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;
using FileAttributes = System.IO.FileAttributes;
using Windows.Storage.FileProperties;
using SnipFlow.Video;

namespace SnipFlow.Services;

/// <summary>Recent MP4 references and owned thumbnails. Original videos are never deleted here.</summary>
public static class RecordingHistoryStore
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
    private static readonly BitmapSource Placeholder = CreatePlaceholder();

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
        var thumbnail = await CreateThumbnailAsync(source, cancellationToken).ConfigureAwait(false) ?? Placeholder;
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
                var existing = before.FirstOrDefault(entry => entry.SourcePath.Equals(source, StringComparison.OrdinalIgnoreCase));
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
                    && entry.SourcePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
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

    private static async Task<BitmapSource?> CreateThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var file = await VideoNativeOperation.AwaitAsync(StorageFile.GetFileFromPathAsync(path), timeout.Token).ConfigureAwait(false);
            using var thumbnail = await VideoNativeOperation.AwaitAsync(file.GetThumbnailAsync(ThumbnailMode.VideosView, 240),
                timeout.Token).ConfigureAwait(false);
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size == 0
                || thumbnail.Size > MaximumThumbnailBytes) return null;
            using var stream = thumbnail.AsStreamForRead();
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                // Keep the WinRT stream alive until this read actually completes.
                read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (read == 0) break;
                if (bytes.Length + read > MaximumThumbnailBytes) return null;
                await bytes.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            var data = bytes.ToArray();
            return await Task.Run(() => DecodeThumbnail(data), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        { SettingsStore.Log(exception); return null; }
    }

    private static BitmapSource DecodeThumbnail(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 2048 || frame.PixelHeight > 2048)
            throw new FileFormatException(I18n.T("錄影縮圖無法讀取。"));
        var scale = Math.Min(1d, 240d / Math.Max(frame.PixelWidth, frame.PixelHeight));
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
        image.DecodePixelWidth = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
        image.DecodePixelHeight = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));
        image.EndInit(); image.Freeze(); return image;
    }

    private static BitmapSource? ReadThumbnail(string directory, string id)
    {
        try
        {
            var path = ManagedPath(directory, ThumbnailName(id));
            if (!File.Exists(path)) return null;
            return DecodeThumbnail(ReadBounded(path, MaximumThumbnailBytes));
        }
        catch (Exception exception) when (IsFileError(exception)) { SettingsStore.Log(exception); return null; }
    }

    private static byte[] EncodeThumbnail(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image, null, null, null)); encoder.Save(stream);
        if (stream.Length > MaximumThumbnailBytes) throw new IOException(I18n.T("錄影縮圖過大，已停止儲存。"));
        return stream.ToArray();
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
        if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
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
        if (Path.GetFileName(full).Contains(':')) throw new IOException(I18n.T("錄影歷史檔案路徑無效。"));
        return full;
    }

    private static bool IsId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);
    private static string ThumbnailName(string id) => IsId(id) ? "recording-" + id + ".png" : throw new IOException("Invalid thumbnail ID.");
    private static bool IsThumbnailName(string name) => name.Length == 46 && name.StartsWith("recording-", StringComparison.Ordinal)
        && name.EndsWith(".png", StringComparison.Ordinal) && IsId(name.Substring(10, 32));
    private static bool IsTemporaryName(string name) => name.Length == 50 && name.StartsWith("video-history-", StringComparison.Ordinal)
        && name.EndsWith(".tmp", StringComparison.Ordinal) && IsId(name.Substring(14, 32));
    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException or FileFormatException
        or System.Runtime.InteropServices.COMException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + duration.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static BitmapSource CreatePlaceholder()
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
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze(); return image;
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

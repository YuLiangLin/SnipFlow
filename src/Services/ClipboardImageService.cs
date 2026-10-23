using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace SnipFlow.Services;

internal sealed record ClipboardImageContent(IReadOnlyList<Bitmap> Images, bool HasText);

/// <summary>Read only when the user requests Paste, on the UI thread.</summary>
internal static class ClipboardImageService
{
    private const int MaximumImages = 24;
    private const int MaximumEdge = 16384;
    private const long MaximumImagePixels = 40_000_000;
    private const long MaximumTotalPixels = 80_000_000;
    private const long MaximumEncodedBytes = 80L * 1024 * 1024;
    // Windows registers "PNG"; X11 and Wayland selections offer the MIME type.
    private static readonly DataFormat<byte[]> PngFormat = DataFormat.CreateBytesPlatformFormat(PlatformInfo.IsWindows ? "PNG" : "image/png");

    internal static async Task<ClipboardImageContent> ReadAsync(IClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        Dispatcher.UIThread.VerifyAccess();
        try
        {
            // Keep all format checks and payload reads on one data snapshot. Never read text here.
            var data = await clipboard.TryGetDataAsync();
            if (data is null) return new(Array.Empty<Bitmap>(), false);
            var formats = data.Formats;
            var hasText = formats.Contains(DataFormat.Text);

            // A file list can contain several images even when a bitmap format is also advertised.
            if (formats.Contains(DataFormat.File))
            {
                var items = await data.TryGetFilesAsync();
                if (items is null) throw InvalidContent("剪貼簿的檔案清單格式不支援。");
                var paths = new string[items.Length];
                for (var index = 0; index < items.Length; index++)
                    paths[index] = items[index].TryGetLocalPath() ?? throw InvalidContent("剪貼簿包含非圖片檔案，請只複製 PNG、JPG、BMP、GIF 或 TIFF 圖片。");
                return new(ReadFiles(paths), hasText);
            }

            long pixels = 0;
            // PNG is a registered raw format, not a converted bitmap. Prefer it for alpha.
            if (formats.Contains(PngFormat))
            {
                var payload = await data.TryGetValueAsync(PngFormat);
                if (payload is null) throw InvalidContent("剪貼簿的 PNG 資料格式不支援。");
                ValidateEncodedLength(payload.LongLength);
                var bytes = payload.ToArray(); // Do not change the provider's buffer.
                using var stream = new MemoryStream(bytes, writable: false);
                return new(new[] { Decode(stream, requirePng: true, ref pixels) }, hasText);
            }

            if (formats.Contains(DataFormat.Bitmap))
            {
                if (await data.TryGetBitmapAsync() is not { } source)
                    throw InvalidContent("剪貼簿的圖片資料格式不支援。");
                var width = source.PixelSize.Width;
                var height = source.PixelSize.Height;
                ValidateSize(width, height, ref pixels);
                // Copy into the shared 96 DPI BGRA layout so later edits never touch the provider's bitmap.
                var image = PixelBuffer.FromBitmap(source).ToBitmap();
                if (image.PixelSize.Width != width || image.PixelSize.Height != height)
                    throw InvalidContent("剪貼簿圖片資料已變更，請重新複製。");
                return new(new[] { image }, hasText);
            }
            return new(Array.Empty<Bitmap>(), hasText);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw InvalidContent("剪貼簿圖片無法讀取。", exception);
        }
    }

    /// <summary>Reads plain text for the code image composer, or null when the clipboard has none.</summary>
    internal static async Task<string?> ReadTextAsync(IClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        Dispatcher.UIThread.VerifyAccess();
        var data = await clipboard.TryGetDataAsync();
        if (data is null || !data.Formats.Contains(DataFormat.Text)) return null;
        return await data.TryGetTextAsync();
    }

    /// <summary>Copies an image, retrying while another program holds the clipboard.</summary>
    internal static async Task SetImageAsync(IClipboard clipboard, Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(image);
        for (var attempt = 0; ; attempt++)
        {
            try { await clipboard.SetBitmapAsync(image); return; }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(120); }
        }
    }

    private static IReadOnlyList<Bitmap> ReadFiles(string[] paths)
    {
        if (paths.Length > MaximumImages)
            throw InvalidContent("一次最多可貼上 24 張圖片。");
        // Reject a mixed list before opening any image; do not silently omit other files.
        foreach (var path in paths)
            if (!IsImagePath(path))
                throw InvalidContent("剪貼簿包含非圖片檔案，請只複製 PNG、JPG、BMP、GIF 或 TIFF 圖片。");

        var images = new List<Bitmap>(paths.Length);
        long pixels = 0, encodedBytes = 0;
        foreach (var path in paths)
        {
            try
            {
                // A single read-only handle keeps header inspection and decoding on the same file.
                using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
                ValidateEncodedLength(stream.Length);
                encodedBytes += stream.Length;
                if (encodedBytes > MaximumEncodedBytes)
                    throw InvalidContent("貼上的圖片資料合計不可超過 80 MiB。");
                images.Add(Decode(stream, Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase), ref pixels));
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                throw InvalidContent("剪貼簿中的圖片檔案無法讀取。", exception);
            }
        }
        return images.AsReadOnly();
    }

    private static Bitmap Decode(Stream stream, bool requirePng, ref long pixels)
    {
        int width, height;
        if (requirePng)
        {
            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);
            if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || BinaryPrimitives.ReadInt32BigEndian(header[8..12]) != 13
                || !header[12..16].SequenceEqual("IHDR"u8))
                throw InvalidContent("剪貼簿的 PNG 圖片資料無效。");
            width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        }
        else if (!TryReadHeaderSize(stream, out width, out height))
            throw InvalidContent("剪貼簿的圖片資料格式不支援。");
        ValidateSize(width, height, ref pixels);
        stream.Position = 0;
        // The decoder keeps the first frame only, so an animated GIF is not fully decoded.
        Bitmap image;
        try { image = ImageTools.Decode(stream); }
        catch (InvalidDataException exception) { throw InvalidContent("剪貼簿圖片無法讀取。", exception); }
        if (image.PixelSize.Width != width || image.PixelSize.Height != height)
            throw InvalidContent("剪貼簿圖片資料已變更，請重新複製。");
        return image;
    }

    /// <summary>
    /// Reads the pixel size from a PNG, GIF, BMP, JPEG or TIFF header without decoding, so an oversized
    /// image is rejected before its pixels are allocated.
    /// </summary>
    private static bool TryReadHeaderSize(Stream stream, out int width, out int height)
    {
        width = height = 0;
        stream.Position = 0;
        var header = new byte[32];
        var count = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var bytes = header.AsSpan(0, count);
        if (count >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes[12..16].SequenceEqual("IHDR"u8))
        {
            width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
            height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
            return true;
        }
        if (count >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
            return true;
        }
        if (count >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..22]);
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes[22..26]));
            return true;
        }
        if (count >= 8 && (bytes[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) || bytes[..4].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })))
            return TryReadTiffSize(stream, bytes[0] == 0x49, out width, out height);
        if (count >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            return TryReadJpegSize(stream, out width, out height);
        return false;
    }

    private static bool TryReadJpegSize(Stream stream, out int width, out int height)
    {
        width = height = 0;
        stream.Position = 2;
        Span<byte> segment = stackalloc byte[7];
        Span<byte> length = stackalloc byte[2];
        while (stream.Position < stream.Length)
        {
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) return false;
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            if (marker == 0xD9) return false;
            if (stream.ReadAtLeast(length, 2, throwOnEndOfStream: false) < 2) return false;
            var size = BinaryPrimitives.ReadUInt16BigEndian(length);
            if (size < 2) return false;
            // SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (stream.ReadAtLeast(segment[..5], 5, throwOnEndOfStream: false) < 5) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..3]);
                width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..5]);
                return true;
            }
            stream.Seek(size - 2, SeekOrigin.Current);
        }
        return false;
    }

    private static bool TryReadTiffSize(Stream stream, bool littleEndian, out int width, out int height)
    {
        width = height = 0;
        uint Read32(ReadOnlySpan<byte> value) => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(value) : BinaryPrimitives.ReadUInt32BigEndian(value);
        ushort Read16(ReadOnlySpan<byte> value) => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt16BigEndian(value);
        Span<byte> buffer = stackalloc byte[12];
        stream.Position = 4;
        if (stream.ReadAtLeast(buffer[..4], 4, throwOnEndOfStream: false) < 4) return false;
        var offset = Read32(buffer[..4]);
        if (offset < 8 || offset > stream.Length - 2) return false;
        stream.Position = offset;
        if (stream.ReadAtLeast(buffer[..2], 2, throwOnEndOfStream: false) < 2) return false;
        var entries = Read16(buffer[..2]);
        for (var index = 0; index < entries && index < 512; index++)
        {
            if (stream.ReadAtLeast(buffer, 12, throwOnEndOfStream: false) < 12) return false;
            var tag = Read16(buffer[..2]);
            var type = Read16(buffer[2..4]);
            var value = type == 3 ? Read16(buffer[8..10]) : (int)Read32(buffer[8..12]);
            if (tag == 256) width = value;
            else if (tag == 257) height = value;
            if (width > 0 && height > 0) return true;
        }
        return false;
    }

    private static bool IsImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathFullyQualified(path)
            || (PlatformInfo.IsWindows && Path.GetFileName(path).Contains(':'))) return false;
        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";
    }

    private static void ValidateEncodedLength(long length)
    {
        if (length <= 0) throw InvalidContent("剪貼簿圖片資料無效。");
        if (length > MaximumEncodedBytes)
            throw InvalidContent("貼上的圖片資料合計不可超過 80 MiB。");
    }

    private static void ValidateSize(int width, int height, ref long totalPixels)
    {
        var pixels = (long)width * height;
        if (width < 1 || height < 1 || width > MaximumEdge || height > MaximumEdge || pixels > MaximumImagePixels)
            throw InvalidContent("單張圖片不可超過 4,000 萬像素，且任一邊不可超過 16,384 像素。");
        if (totalPixels + pixels > MaximumTotalPixels)
            throw InvalidContent("貼上的圖片合計不可超過 8,000 萬像素。");
        totalPixels += pixels;
    }

    // Clipboard ExternalException (OLE busy on Windows) must reach the caller unchanged for its asynchronous retry.
    private static bool IsReadFailure(Exception exception) => exception is not ClipboardImageException && exception is not ExternalException
        && (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or InvalidDataException or InvalidOperationException or System.Security.SecurityException);
    private static ClipboardImageException InvalidContent(string key, Exception? inner = null) => new(I18n.T(key), inner);
    private sealed class ClipboardImageException(string message, Exception? inner) : IOException(message, inner) { }
}

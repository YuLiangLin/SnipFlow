using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SnipFlow.Services;

internal sealed record ClipboardImageContent(IReadOnlyList<BitmapSource> Images, bool HasText);

/// <summary>Read only when the user requests Paste, on the UI's STA thread.</summary>
internal static class ClipboardImageService
{
    private const int MaximumImages = 24;
    private const int MaximumEdge = 16384;
    private const long MaximumImagePixels = 40_000_000;
    private const long MaximumTotalPixels = 80_000_000;
    private const long MaximumEncodedBytes = 80L * 1024 * 1024;
    private const string PngFormat = "PNG";

    internal static ClipboardImageContent Read()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw InvalidContent("貼上圖片必須在 STA 執行緒執行。");
        try
        {
            // Keep all format checks and payload reads on one data object. Never read text here.
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null) return new(Array.Empty<BitmapSource>(), false);
            var hasText = data.GetDataPresent(DataFormats.UnicodeText, autoConvert: false)
                || data.GetDataPresent(DataFormats.Text, autoConvert: false);

            // A file drop can contain several images even when a bitmap format is also advertised.
            if (data.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
            {
                if (data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] paths)
                    throw InvalidContent("剪貼簿的檔案清單格式不支援。");
                return new(ReadFiles(paths), hasText);
            }

            long pixels = 0;
            // PNG is a registered raw format, not a WPF-converted Bitmap. Prefer it for alpha.
            if (data.GetDataPresent(PngFormat, autoConvert: false))
            {
                var payload = data.GetData(PngFormat, autoConvert: false);
                byte[] bytes;
                if (payload is MemoryStream memory)
                {
                    ValidateEncodedLength(memory.Length);
                    bytes = memory.ToArray(); // Do not change or dispose the provider's stream.
                }
                else if (payload is byte[] buffer)
                {
                    ValidateEncodedLength(buffer.LongLength);
                    bytes = buffer.ToArray();
                }
                else throw InvalidContent("剪貼簿的 PNG 資料格式不支援。");
                ValidateEncodedLength(bytes.LongLength);
                using var stream = new MemoryStream(bytes, writable: false);
                return new(new[] { Decode(stream, requirePng: true, ref pixels) }, hasText);
            }

            if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true))
            {
                if (data.GetData(DataFormats.Bitmap, autoConvert: true) is not BitmapSource source)
                    throw InvalidContent("剪貼簿的圖片資料格式不支援。");
                if (source.IsDownloading) throw InvalidContent("剪貼簿圖片尚未載入完成。");
                var width = source.PixelWidth;
                var height = source.PixelHeight;
                ValidateSize(width, height, ref pixels);
                var image = source.CloneCurrentValue();
                if (!image.CanFreeze) throw InvalidContent("剪貼簿圖片尚未載入完成。");
                image.Freeze();
                if (image.PixelWidth != width || image.PixelHeight != height)
                    throw InvalidContent("剪貼簿圖片資料已變更，請重新複製。");
                return new(new[] { image }, hasText);
            }
            return new(Array.Empty<BitmapSource>(), hasText);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw InvalidContent("剪貼簿圖片無法讀取。", exception);
        }
    }

    private static IReadOnlyList<BitmapSource> ReadFiles(string[] paths)
    {
        if (paths.Length > MaximumImages)
            throw InvalidContent("一次最多可貼上 24 張圖片。");
        // Reject a mixed drop before opening any image; do not silently omit other files.
        foreach (var path in paths)
            if (!IsImagePath(path))
                throw InvalidContent("剪貼簿包含非圖片檔案，請只複製 PNG、JPG、BMP、GIF 或 TIFF 圖片。");

        var images = new List<BitmapSource>(paths.Length);
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

    private static BitmapSource Decode(Stream stream, bool requirePng, ref long pixels)
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
        else
        {
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);
            if (header is not (PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder or GifBitmapDecoder or TiffBitmapDecoder)
                || header.Frames.Count == 0)
                throw InvalidContent("剪貼簿的圖片資料格式不支援。");
            width = header.Frames[0].PixelWidth;
            height = header.Frames[0].PixelHeight;
        }
        ValidateSize(width, height, ref pixels);
        stream.Position = 0;
        // BitmapImage caches the first frame only, so an animated GIF/TIFF is not fully decoded.
        var image = new BitmapImage();
        Exception? decodeFailure = null;
        EventHandler<ExceptionEventArgs> failed = (_, args) => decodeFailure = args.ErrorException;
        image.DecodeFailed += failed;
        try
        {
            image.BeginInit();
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        finally { image.DecodeFailed -= failed; }
        if (decodeFailure is not null) throw InvalidContent("剪貼簿圖片無法讀取。", decodeFailure);
        if (image.PixelWidth != width || image.PixelHeight != height)
            throw InvalidContent("剪貼簿圖片資料已變更，請重新複製。");
        image.Freeze();
        return image;
    }

    private static bool IsImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathFullyQualified(path)
            || Path.GetFileName(path).Contains(':')) return false;
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

    // Clipboard/OLE ExternalException must reach the caller unchanged for its asynchronous retry.
    private static bool IsReadFailure(Exception exception) => exception is not ClipboardImageException && exception is not ExternalException
        && (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or FileFormatException or InvalidOperationException or System.Security.SecurityException);
    private static ClipboardImageException InvalidContent(string key, Exception? inner = null) => new(I18n.T(key), inner);
    private sealed class ClipboardImageException(string message, Exception? inner) : IOException(message, inner) { }
}

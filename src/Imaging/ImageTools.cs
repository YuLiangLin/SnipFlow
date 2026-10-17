using Avalonia.Platform;

namespace SnipFlow.Imaging;

/// <summary>
/// Image helpers replacing the WPF imaging calls (BitmapImage, PngBitmapEncoder, CroppedBitmap,
/// RenderTargetBitmap). All bitmaps use 96 DPI so one pixel equals one device-independent unit,
/// which keeps document coordinates in pixels exactly as before. Avalonia bitmaps are not bound
/// to a thread, so these helpers are safe to call from background tasks.
/// </summary>
public static class ImageTools
{
    public static readonly Vector Dpi = new(96, 96);

    /// <summary>Decodes an image file (PNG, JPEG, BMP, GIF, WebP, ...). Throws <see cref="InvalidDataException"/> for unreadable data.</summary>
    public static Bitmap Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Decode(stream);
    }

    public static Bitmap Decode(Stream stream)
    {
        try { return Normalize(new Bitmap(stream)); }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OutOfMemoryException))
        {
            throw new InvalidDataException(I18n.T("圖片格式無法讀取。"), ex);
        }
    }

    public static Bitmap Decode(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        return Decode(stream);
    }

    /// <summary>Decodes a file scaled to <paramref name="width"/> pixels wide, for thumbnails.</summary>
    public static Bitmap LoadThumbnail(string path, int width)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try { return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality); }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OutOfMemoryException))
        {
            throw new InvalidDataException(I18n.T("圖片格式無法讀取。"), ex);
        }
    }

    /// <summary>Encodes a lossless PNG.</summary>
    public static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        SavePng(bitmap, stream);
        return stream.ToArray();
    }

    public static void SavePng(Bitmap bitmap, Stream stream) => bitmap.Save(stream, new PngBitmapEncoderOptions());

    public static void SavePng(Bitmap bitmap, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        SavePng(bitmap, stream);
    }

    /// <summary>Copies a pixel rectangle into a new bitmap.</summary>
    public static Bitmap Crop(Bitmap source, PixelRect rectangle)
    {
        var size = source.PixelSize;
        if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0
            || rectangle.Right > size.Width || rectangle.Bottom > size.Height)
            throw new ArgumentOutOfRangeException(nameof(rectangle));
        if (source.Format is { } format && source.AlphaFormat is { } alpha)
        {
            var bitmap = new WriteableBitmap(rectangle.Size, Dpi, format, alpha);
            using var target = bitmap.Lock();
            source.CopyPixels(rectangle, target.Address, target.RowBytes * rectangle.Height, target.RowBytes);
            return bitmap;
        }
        return PixelBuffer.FromBitmap(source).Crop(rectangle).ToBitmap();
    }

    /// <summary>Renders drawing commands into a new bitmap of exactly <paramref name="size"/> pixels.</summary>
    public static Bitmap Render(PixelSize size, Action<DrawingContext> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var target = new RenderTargetBitmap(size, Dpi);
        using (var context = target.CreateDrawingContext()) draw(context);
        return target;
    }

    /// <summary>Renders a visual (already measured and arranged) at 96 DPI.</summary>
    public static Bitmap Render(Visual visual, PixelSize size)
    {
        var target = new RenderTargetBitmap(size, Dpi);
        target.Render(visual);
        return target;
    }

    /// <summary>Returns a bitmap scaled to <paramref name="size"/>.</summary>
    public static Bitmap Scale(Bitmap source, PixelSize size, BitmapInterpolationMode mode = BitmapInterpolationMode.HighQuality)
        => source.CreateScaledBitmap(size, mode);

    /// <summary>Scales down to fit within the given bounds, keeping the aspect ratio. Smaller images are returned unchanged.</summary>
    public static Bitmap Fit(Bitmap source, int maxWidth, int maxHeight)
    {
        var size = source.PixelSize;
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)size.Width, maxHeight / (double)size.Height));
        if (scale >= 1) return source;
        return Scale(source, new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale))));
    }

    /// <summary>Creates a bitmap from tightly packed BGRA premultiplied pixels.</summary>
    public static Bitmap FromBgra(byte[] pixels, int width, int height) => new PixelBuffer(width, height, pixels).ToBitmap();

    /// <summary>Converts decoded images to the BGRA/premultiplied layout used by capture code, at 96 DPI.</summary>
    static Bitmap Normalize(Bitmap bitmap)
    {
        if (bitmap.Dpi == Dpi && bitmap.Format == PixelFormat.Bgra8888 && bitmap.AlphaFormat is AlphaFormat.Premul or AlphaFormat.Opaque)
            return bitmap;
        using (bitmap)
        {
            var size = bitmap.PixelSize;
            var normalized = new WriteableBitmap(size, Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var target = normalized.Lock();
            if (bitmap.Format == PixelFormat.Bgra8888 && bitmap.AlphaFormat is AlphaFormat.Premul or AlphaFormat.Opaque)
                bitmap.CopyPixels(new PixelRect(size), target.Address, target.RowBytes * size.Height, target.RowBytes);
            else
                bitmap.CopyPixels(target);
            return normalized;
        }
    }

    /// <summary>A solid opaque image, for blank canvases.</summary>
    public static Bitmap Solid(int width, int height, Color color)
    {
        var buffer = new PixelBuffer(width, height);
        buffer.Fill(color);
        return buffer.ToBitmap();
    }
}

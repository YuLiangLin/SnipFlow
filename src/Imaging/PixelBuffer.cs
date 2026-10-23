using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace SnipFlow.Imaging;

/// <summary>
/// A managed copy of an image in BGRA 8:8:8:8 premultiplied order (stride = Width * 4), the layout
/// that screen capture backends produce and that pixel algorithms (stitching, mosaic, overlap
/// matching) read. Convert with <see cref="FromBitmap"/> and <see cref="ToBitmap"/>.
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;
    public PixelSize Size => new(Width, Height);

    public PixelBuffer(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
        var length = checked(width * height * 4);
        if (pixels is not null && pixels.Length < length) throw new ArgumentException("The pixel array is smaller than width * height * 4.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[length];
    }

    /// <summary>Copies a bitmap's pixels, converting to BGRA premultiplied when needed.</summary>
    public static PixelBuffer FromBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var size = bitmap.PixelSize;
        var buffer = new PixelBuffer(size.Width, size.Height);
        if (bitmap.Format == PixelFormat.Bgra8888 && bitmap.AlphaFormat is AlphaFormat.Premul or AlphaFormat.Opaque)
        {
            var handle = GCHandle.Alloc(buffer.Pixels, GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Pixels.Length, buffer.Stride); }
            finally { handle.Free(); }
            return buffer;
        }
        // Let Avalonia convert other layouts (RGBA, unpremultiplied, ...) into a BGRA target.
        using var converted = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var target = converted.Lock())
        {
            bitmap.CopyPixels(target);
            CopyRows(target.Address, target.RowBytes, buffer.Pixels, buffer.Stride, size.Height);
        }
        return buffer;
    }

    /// <summary>Creates a new 96-DPI bitmap from these pixels.</summary>
    public WriteableBitmap ToBitmap()
    {
        var bitmap = new WriteableBitmap(Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var target = bitmap.Lock();
        for (var y = 0; y < Height; y++)
            Marshal.Copy(Pixels, y * Stride, target.Address + y * target.RowBytes, Stride);
        return bitmap;
    }

    public PixelBuffer Crop(PixelRect rectangle)
    {
        if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0
            || rectangle.Right > Width || rectangle.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rectangle));
        var result = new PixelBuffer(rectangle.Width, rectangle.Height);
        for (var y = 0; y < rectangle.Height; y++)
            Buffer.BlockCopy(Pixels, (rectangle.Y + y) * Stride + rectangle.X * 4, result.Pixels, y * result.Stride, result.Stride);
        return result;
    }

    /// <summary>Copies <paramref name="source"/> into this buffer at (x, y), clipping at the edges.</summary>
    public void Blit(PixelBuffer source, int x, int y)
    {
        var left = Math.Max(0, x);
        var top = Math.Max(0, y);
        var right = Math.Min(Width, x + source.Width);
        var bottom = Math.Min(Height, y + source.Height);
        if (right <= left || bottom <= top) return;
        var bytes = (right - left) * 4;
        for (var row = top; row < bottom; row++)
            Buffer.BlockCopy(source.Pixels, (row - y) * source.Stride + (left - x) * 4, Pixels, row * Stride + left * 4, bytes);
    }

    /// <summary>Fills every pixel with an opaque color.</summary>
    public void Fill(Color color)
    {
        var a = color.A / 255.0;
        byte b = (byte)Math.Round(color.B * a), g = (byte)Math.Round(color.G * a), r = (byte)Math.Round(color.R * a);
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = b; Pixels[i + 1] = g; Pixels[i + 2] = r; Pixels[i + 3] = color.A;
        }
    }

    /// <summary>Copies rows from native memory with any stride into a tightly packed array.</summary>
    public static void CopyRows(IntPtr source, int sourceStride, byte[] destination, int destinationStride, int rows)
    {
        var bytes = Math.Min(sourceStride, destinationStride);
        for (var y = 0; y < rows; y++)
            Marshal.Copy(source + y * sourceStride, destination, y * destinationStride, bytes);
    }
}

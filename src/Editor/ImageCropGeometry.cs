namespace SnipFlow.Editor;

/// <summary>Crop rectangles refer to the complete source image, independently of its on-canvas size.</summary>
internal static class ImageCropGeometry
{
    internal static readonly Rect Full = new(0, 0, 1, 1);

    internal static bool IsValid(Rect crop) => double.IsFinite(crop.X) && double.IsFinite(crop.Y)
        && double.IsFinite(crop.Width) && double.IsFinite(crop.Height)
        && crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0
        && crop.Right <= 1 && crop.Bottom <= 1;

    /// <summary>Avalonia's bitmap drawing path passes source rectangles to the renderer in pixels.</summary>
    internal static Rect SourceRect(Bitmap image, Rect? crop)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = crop ?? Full;
        if (!IsValid(source)) throw new ArgumentOutOfRangeException(nameof(crop));
        // Bitmap.Draw passes sourceRect unchanged to DrawBitmap; Skia samples physical image pixels.
        // Using Image.Size would select the wrong region for a bitmap whose DPI is not 96.
        var size = image.PixelSize;
        return new Rect(source.X * size.Width, source.Y * size.Height,
            source.Width * size.Width, source.Height * size.Height);
    }

    /// <summary>Keeps the same visible content when the complete source bitmap is rotated or mirrored.</summary>
    internal static Rect? Transform(Rect? crop, int orientation)
    {
        var matrix = ImageOrientation.InFrame(orientation, 1, 1);
        if (crop is not { } source) return null;
        if (!IsValid(source)) throw new ArgumentOutOfRangeException(nameof(crop));
        var transformed = ImageOrientation.TransformBounds(source, matrix);
        // D4 maps the unit square onto itself; clamp only arithmetic round-off at its boundary.
        return new Rect(new Point(Math.Clamp(transformed.Left, 0, 1), Math.Clamp(transformed.Top, 0, 1)),
            new Point(Math.Clamp(transformed.Right, 0, 1), Math.Clamp(transformed.Bottom, 0, 1)));
    }
}

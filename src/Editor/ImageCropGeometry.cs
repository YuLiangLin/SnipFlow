namespace SnipFlow.Editor;

/// <summary>Normalized source viewports describe the image inside its fixed on-canvas frame.</summary>
internal static class ImageCropGeometry
{
    private const double MaximumOffset = 1_000_000;
    internal static readonly Rect Full = new(0, 0, 1, 1);

    internal static bool IsLegacyCrop(Rect crop) => double.IsFinite(crop.X) && double.IsFinite(crop.Y)
        && double.IsFinite(crop.Width) && double.IsFinite(crop.Height)
        && crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0
        && crop.Right <= 1 && crop.Bottom <= 1;

    // This range is symmetric around the unit square's center, so D4 transforms keep it bounded.
    internal static bool IsValid(Rect viewport) => HasFinitePositiveExtents(viewport)
        && viewport.Left >= -MaximumOffset && viewport.Top >= -MaximumOffset
        && viewport.Right <= 1 + MaximumOffset && viewport.Bottom <= 1 + MaximumOffset;

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

    /// <summary>Maps only real source pixels into the frame; the rest of the frame stays transparent.</summary>
    internal static (Rect Source, Rect Destination)? GetDrawRects(Bitmap image, Rect? viewport, Rect frame)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = viewport ?? Full;
        if (!IsValid(source)) throw new ArgumentOutOfRangeException(nameof(viewport));
        if (!HasFinitePositiveExtents(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        var visible = source.Intersect(Full);
        if (visible.Width <= 0 || visible.Height <= 0) return null;
        // Ratios stay in [0,1], even when the viewport extends far beyond the source. Do not draw
        // an enormous full-image destination or pass out-of-bounds source pixels to the renderer.
        var left = Math.Clamp((visible.Left - source.Left) / source.Width, 0, 1);
        var top = Math.Clamp((visible.Top - source.Top) / source.Height, 0, 1);
        var width = Math.Clamp(visible.Width / source.Width, 0, 1);
        var height = Math.Clamp(visible.Height / source.Height, 0, 1);
        var destination = new Rect(frame.X + left * frame.Width, frame.Y + top * frame.Height,
            width * frame.Width, height * frame.Height);
        if (destination.Width <= 0 || destination.Height <= 0) return null;
        return (SourceRect(image, visible), destination);
    }

    /// <summary>Keeps the same visible content when the complete source bitmap is rotated or mirrored.</summary>
    internal static Rect? Transform(Rect? crop, int orientation)
    {
        var matrix = ImageOrientation.InFrame(orientation, 1, 1);
        if (crop is not { } source) return null;
        if (!IsValid(source)) throw new ArgumentOutOfRangeException(nameof(crop));
        var transformed = ImageOrientation.TransformBounds(source, matrix);
        if (!IsValid(transformed)) throw new ArgumentOutOfRangeException(nameof(crop));
        return transformed;
    }

    private static bool HasFinitePositiveExtents(Rect rect) => double.IsFinite(rect.X) && double.IsFinite(rect.Y)
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom)
        && rect.Width > 0 && rect.Height > 0;
}

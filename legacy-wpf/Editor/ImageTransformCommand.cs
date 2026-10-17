using WpfRect = System.Windows.Rect;

namespace SnipFlow.Editor;

internal enum ImageTransformCommand { RotateLeft, RotateRight, FlipHorizontal, FlipVertical }

/// <summary>D4: low two bits are clockwise quarter turns; bit 2 mirrors X before rotation.</summary>
internal static class ImageOrientation
{
    internal static bool IsValid(int orientation) => orientation is >= 0 and <= 7;
    internal static bool SwapsAxes(int orientation) => (orientation & 1) != 0;

    internal static int ForCommand(ImageTransformCommand command) => command switch
    {
        ImageTransformCommand.RotateLeft => 3,
        ImageTransformCommand.RotateRight => 1,
        ImageTransformCommand.FlipHorizontal => 4,
        ImageTransformCommand.FlipVertical => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    internal static Matrix Linear(int orientation) => orientation switch
    {
        0 => Matrix.Identity,
        1 => new(0, 1, -1, 0, 0, 0),
        2 => new(-1, 0, 0, -1, 0, 0),
        3 => new(0, -1, 1, 0, 0, 0),
        4 => new(-1, 0, 0, 1, 0, 0),
        5 => new(0, -1, -1, 0, 0, 0),
        6 => new(1, 0, 0, -1, 0, 0),
        7 => new(0, 1, 1, 0, 0, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(orientation))
    };

    internal static Matrix InFrame(int orientation, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var matrix = Linear(orientation);
        var bounds = TransformBounds(new WpfRect(0, 0, width, height), matrix);
        matrix.OffsetX = -bounds.Left;
        matrix.OffsetY = -bounds.Top;
        return matrix;
    }

    internal static WpfRect TransformBounds(WpfRect bounds, Matrix matrix)
    {
        bounds.Transform(matrix);
        return bounds;
    }

    internal static int Compose(int original, int operation)
    {
        // WPF uses row vectors: the original text orientation is applied first.
        var composed = Matrix.Multiply(Linear(original), Linear(operation));
        for (var orientation = 0; orientation < 8; orientation++)
            if (Linear(orientation) == composed) return orientation;
        throw new InvalidOperationException("Invalid orthogonal image transform.");
    }

    internal static BitmapSource TransformBitmap(BitmapSource source, int orientation)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFrozen || source.PixelWidth < 1 || source.PixelHeight < 1)
            throw new InvalidOperationException("Image transform sources must be frozen.");
        var transform = new MatrixTransform(Linear(orientation));
        transform.Freeze();
        var result = new TransformedBitmap(source, transform);
        result.Freeze();
        var expectedWidth = SwapsAxes(orientation) ? source.PixelHeight : source.PixelWidth;
        var expectedHeight = SwapsAxes(orientation) ? source.PixelWidth : source.PixelHeight;
        if (result.PixelWidth != expectedWidth || result.PixelHeight != expectedHeight)
            throw new InvalidOperationException("Unexpected transformed image dimensions.");
        return result;
    }
}

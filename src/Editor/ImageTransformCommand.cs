using System.Runtime.InteropServices;
using Avalonia.Platform;

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
        var bounds = TransformBounds(new Rect(0, 0, width, height), matrix);
        return new Matrix(matrix.M11, matrix.M12, matrix.M21, matrix.M22, -bounds.Left, -bounds.Top);
    }

    internal static Rect TransformBounds(Rect bounds, Matrix matrix) => bounds.TransformToAABB(matrix);

    internal static int Compose(int original, int operation)
    {
        // Avalonia, like WPF, uses row vectors: the original text orientation is applied first.
        var composed = Linear(original) * Linear(operation);
        for (var orientation = 0; orientation < 8; orientation++)
            if (Linear(orientation) == composed) return orientation;
        throw new InvalidOperationException("Invalid orthogonal image transform.");
    }

    /// <summary>Rotates or mirrors whole pixels; the result is lossless and exactly the expected size.</summary>
    internal static Bitmap TransformBitmap(Bitmap source, int orientation)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = source.PixelSize;
        if (size.Width < 1 || size.Height < 1)
            throw new InvalidOperationException("Image transform sources must not be empty.");
        var input = ReadPixels(source);
        var expectedWidth = SwapsAxes(orientation) ? size.Height : size.Width;
        var expectedHeight = SwapsAxes(orientation) ? size.Width : size.Height;
        var output = new PixelBuffer(expectedWidth, expectedHeight);
        // Map pixel centers through the same matrix used for annotations. With 0/±1 coefficients
        // every destination index is an exact integer, so this is an affine walk over indices.
        var matrix = InFrame(orientation, size.Width, size.Height);
        var origin = matrix.Transform(new Point(0.5, 0.5));
        int originX = (int)Math.Floor(origin.X), originY = (int)Math.Floor(origin.Y);
        int stepXx = (int)matrix.M11, stepXy = (int)matrix.M12, stepYx = (int)matrix.M21, stepYy = (int)matrix.M22;
        var from = MemoryMarshal.Cast<byte, uint>(input.Pixels.AsSpan());
        var to = MemoryMarshal.Cast<byte, uint>(output.Pixels.AsSpan());
        for (var y = 0; y < size.Height; y++)
        {
            int targetX = originX + y * stepYx, targetY = originY + y * stepYy;
            var row = y * size.Width;
            for (var x = 0; x < size.Width; x++)
            {
                to[targetY * expectedWidth + targetX] = from[row + x];
                targetX += stepXx;
                targetY += stepXy;
            }
        }
        var result = output.ToBitmap();
        if (result.PixelSize.Width != expectedWidth || result.PixelSize.Height != expectedHeight)
            throw new InvalidOperationException("Unexpected transformed image dimensions.");
        return result;
    }

    /// <summary>BGRA premultiplied pixels; opaque sources always read back with full alpha.</summary>
    internal static PixelBuffer ReadPixels(Bitmap source)
    {
        var buffer = PixelBuffer.FromBitmap(source);
        if (source.AlphaFormat == AlphaFormat.Opaque) SetOpaque(buffer.Pixels);
        return buffer;
    }

    internal static void SetOpaque(byte[] pixels)
    {
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
    }
}

namespace SnipFlow.Services;

/// <summary>
/// Recognizes text on this computer. Windows uses the installed Windows OCR language packs
/// (<c>OcrService.Windows.cs</c>); Linux runs the Tesseract OCR engine (<c>OcrService.Linux.cs</c>).
/// Images stay on this computer.
/// </summary>
public static partial class OcrService
{
    /// <summary>Language tags the local OCR engine can recognize.</summary>
    public static IReadOnlyList<string> AvailableLanguages => GetAvailableLanguages();

    public static Task<string> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
        => RecognizeAsync(image, null, cancellationToken);

    public static Task<string> RecognizeAsync(
        Bitmap image,
        string? languageTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        return RecognizeCoreAsync(image, languageTag, cancellationToken);
    }

    private static partial IReadOnlyList<string> GetAvailableLanguages();

    private static partial Task<string> RecognizeCoreAsync(Bitmap image, string? languageTag, CancellationToken cancellationToken);

    /// <summary>Scales with high-quality filtering by drawing, which works for every bitmap kind.</summary>
    /// <remarks>Skia's <c>Bitmap.CreateScaledBitmap</c> (used by <c>ImageTools.Scale</c>) rejects writeable and rendered bitmaps.</remarks>
    private static Bitmap Resize(Bitmap source, PixelSize size)
        => ImageTools.Render(size, context =>
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(source, new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height), new Rect(0, 0, size.Width, size.Height));
        });

    /// <summary>
    /// Copies the image as opaque BGRA pixels. Transparent annotations are composited over white
    /// instead of being treated as black text.
    /// </summary>
    private static PixelBuffer FlattenOnWhite(Bitmap image)
    {
        var buffer = PixelBuffer.FromBitmap(image);
        var pixels = buffer.Pixels;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha != 255)
            {
                // Premultiplied color over white: color + white * (1 - alpha).
                var white = 255 - alpha;
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = (byte)Math.Min(255, pixels[offset + channel] + white);
                pixels[offset + 3] = 255;
            }
        }
        return buffer;
    }
}

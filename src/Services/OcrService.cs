using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SnipFlow.Services;

/// <summary>Uses the installed Windows OCR language packs. Images stay on this computer.</summary>
public static class OcrService
{
    public static IReadOnlyList<string> AvailableLanguages => OcrEngine.AvailableRecognizerLanguages
        .Select(language => language.LanguageTag)
        .ToArray();

    public static Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken = default)
        => RecognizeAsync(image, null, cancellationToken);

    public static async Task<string> RecognizeAsync(
        BitmapSource image,
        string? languageTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        var engine = CreateEngine(languageTag);
        var maximumDimension = checked((int)OcrEngine.MaxImageDimension);
        if (maximumDimension <= 0)
            throw new InvalidOperationException("Windows OCR 無法取得可處理的影像大小。");

        BitmapSource source = image;
        var scale = Math.Min(1d, (double)maximumDimension / Math.Max(image.PixelWidth, image.PixelHeight));
        if (scale < 1d)
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        // Composite transparent annotations over white instead of treating them as black text.
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha != 255)
            {
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = (byte)((pixels[offset + channel] * alpha + 255 * (255 - alpha)) / 255);
                pixels[offset + 3] = 255;
            }
        }

        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
            source.PixelWidth, source.PixelHeight, BitmapAlphaMode.Ignore);

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            var requestedLanguage = new Language(languageTag);
            return OcrEngine.TryCreateFromLanguage(requestedLanguage)
                ?? throw new InvalidOperationException(
                    $"Windows 尚未安裝 {languageTag} 的 OCR 語言套件。請到 Windows「設定 → 時間與語言 → 語言與地區」新增該語言並安裝光學字元辨識 (OCR) 功能，再重新開啟 SnipFlow。");
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null)
            return engine;

        var installed = OcrEngine.AvailableRecognizerLanguages;
        var fallback = installed.FirstOrDefault(language =>
            language.LanguageTag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || language.LanguageTag.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(language => language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault();

        if (fallback is not null && OcrEngine.TryCreateFromLanguage(fallback) is { } fallbackEngine)
            return fallbackEngine;

        throw new InvalidOperationException(
            "Windows 尚未安裝可用的 OCR 語言套件。請到「設定 → 時間與語言 → 語言與地區」新增繁體中文或英文，安裝光學字元辨識 (OCR) 功能後再試一次。文字辨識使用本機 Windows OCR，不會上傳截圖。");
    }
}

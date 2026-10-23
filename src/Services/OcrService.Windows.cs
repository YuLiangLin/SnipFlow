using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SnipFlow.Services;

/// <summary>Uses the installed Windows OCR language packs. Images stay on this computer.</summary>
public static partial class OcrService
{
    private static partial IReadOnlyList<string> GetAvailableLanguages() => OcrEngine.AvailableRecognizerLanguages
        .Select(language => language.LanguageTag)
        .ToArray();

    private static async partial Task<string> RecognizeCoreAsync(Bitmap image, string? languageTag, CancellationToken cancellationToken)
    {
        var engine = CreateEngine(languageTag);
        var maximumDimension = checked((int)OcrEngine.MaxImageDimension);
        if (maximumDimension <= 0)
            throw new InvalidOperationException(I18n.T("Windows OCR 無法取得可處理的影像大小。"));

        var size = image.PixelSize;
        Bitmap source = image;
        var scale = Math.Min(1d, (double)maximumDimension / Math.Max(size.Width, size.Height));
        if (scale < 1d)
            source = ImageTools.Scale(image, new PixelSize(
                Math.Clamp((int)Math.Round(size.Width * scale), 1, maximumDimension),
                Math.Clamp((int)Math.Round(size.Height * scale), 1, maximumDimension)));

        // Composite transparent annotations over white instead of treating them as black text.
        var pixels = FlattenOnWhite(source);
        if (!ReferenceEquals(source, image)) source.Dispose();

        using var writer = new DataWriter();
        writer.WriteBytes(pixels.Pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
            pixels.Width, pixels.Height, BitmapAlphaMode.Ignore);

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
                    I18n.F("Windows 尚未安裝 {0} 的 OCR 語言套件。請到 Windows「設定 → 時間與語言 → 語言與地區」新增該語言並安裝光學字元辨識 (OCR) 功能，再重新開啟 SnipFlow。", languageTag));
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
            I18n.T("Windows 尚未安裝可用的 OCR 語言套件。請到「設定 → 時間與語言 → 語言與地區」新增繁體中文或英文，安裝光學字元辨識 (OCR) 功能後再試一次。文字辨識使用本機 Windows OCR，不會上傳截圖。"));
    }
}

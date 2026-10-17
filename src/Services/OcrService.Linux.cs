using System.Globalization;
using System.Text.RegularExpressions;

namespace SnipFlow.Services;

/// <summary>
/// Runs the Tesseract OCR engine (<c>tesseract</c> command) on this computer. The image is passed
/// through standard input and the text read from standard output; nothing is written to disk or uploaded.
/// </summary>
public static partial class OcrService
{
    const string Tesseract = "tesseract";
    static readonly TimeSpan RecognitionTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

    // Screen text is far smaller than the ~300 dpi scans Tesseract is trained on. Doubling the
    // image (up to this many output pixels) usually improves accuracy on screenshots of code and text.
    const long UpscaleLimitPixels = 24_000_000;

    // Tesseract separates CJK characters with spaces; join them again.
    static readonly Regex CjkSpacing = new(
        @"(?<=[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}\p{IsHiragana}\p{IsKatakana}\p{IsBopomofo}])[ \t]+(?=[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}\p{IsHiragana}\p{IsKatakana}\p{IsBopomofo}])",
        RegexOptions.CultureInvariant);

    private static partial IReadOnlyList<string> GetAvailableLanguages()
    {
        if (!ExternalTool.IsAvailable(Tesseract)) return Array.Empty<string>();
        try
        {
            // Run on the thread pool so a call from the UI thread cannot deadlock on the process awaits.
            var models = Task.Run(() => InstalledModelsAsync(CancellationToken.None)).GetAwaiter().GetResult();
            return models.Select(LanguageTagFor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SettingsStore.Log(ex);
            return Array.Empty<string>();
        }
    }

    private static async partial Task<string> RecognizeCoreAsync(Bitmap image, string? languageTag, CancellationToken cancellationToken)
    {
        if (!ExternalTool.IsAvailable(Tesseract))
            throw new InvalidOperationException(I18n.F(
                "找不到 Tesseract OCR 文字辨識引擎。請在終端機執行「{0}」安裝後，重新開啟 SnipFlow 再試一次。文字辨識只在本機進行，不會上傳截圖。",
                InstallCommand(DefaultModels())));

        var installed = await InstalledModelsAsync(cancellationToken);
        var models = ChooseModels(languageTag, installed);
        var (png, dpi) = await Task.Run(() => Prepare(image), cancellationToken);

        var result = await ExternalTool.RunAsync(Tesseract,
            ["stdin", "stdout", "-l", string.Join('+', models), "--psm", "3", "--dpi", dpi.ToString(CultureInfo.InvariantCulture)],
            cancellationToken, png, RecognitionTimeout);
        if (!result.Succeeded)
            throw new InvalidOperationException(I18n.F("Tesseract OCR 無法辨識這張圖片：{0}", Summary(result.StandardError, result.ExitCode)));
        return CleanUp(result.StandardOutput);
    }

    /// <summary>Installed traineddata names (eng, chi_tra, ...), excluding orientation detection.</summary>
    static async Task<HashSet<string>> InstalledModelsAsync(CancellationToken cancellationToken)
    {
        var result = await ExternalTool.RunAsync(Tesseract, ["--list-langs"], cancellationToken, timeout: ListTimeout);
        if (!result.Succeeded)
            throw new InvalidOperationException(I18n.F("無法取得 Tesseract OCR 的語言清單：{0}", Summary(result.StandardError, result.ExitCode)));
        // Tesseract 4+ prints the list on stdout after a header line ("List of available languages in ...").
        var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in (result.StandardOutput + "\n" + result.StandardError).Split('\n'))
        {
            var name = line.Trim();
            if (name.Length == 0 || name.Contains(' ') || name.EndsWith(':') || name == "osd") continue;
            models.Add(name);
        }
        return models;
    }

    /// <summary>
    /// The requested language, or the UI language (zh-TW → chi_tra, zh-CN → chi_sim, en → eng),
    /// plus English whenever it is installed, since code and UI text mix both.
    /// </summary>
    static List<string> ChooseModels(string? languageTag, HashSet<string> installed)
    {
        var primary = string.IsNullOrWhiteSpace(languageTag) ? DefaultModels()[0] : ModelFor(languageTag);
        if (!installed.Contains(primary))
        {
            var missing = new List<string> { primary };
            if (primary != "eng" && !installed.Contains("eng")) missing.Add("eng");
            throw new InvalidOperationException(I18n.F(
                "Tesseract OCR 尚未安裝 {0} 語言資料。請在終端機執行「{1}」安裝後再試一次。", primary, InstallCommand(missing)));
        }
        var models = new List<string> { primary };
        if (primary != "eng" && installed.Contains("eng")) models.Add("eng");
        return models;
    }

    static string[] DefaultModels() => I18n.LanguageTag switch
    {
        "zh-TW" => ["chi_tra", "eng"],
        "zh-CN" => ["chi_sim", "eng"],
        _ => ["eng"],
    };

    /// <summary>Maps a BCP-47 tag to a Tesseract model name; Tesseract names (jpn, chi_tra) pass through.</summary>
    static string ModelFor(string languageTag)
    {
        var tag = languageTag.Trim().Replace('_', '-');
        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && (tag.Length == 2 || tag[2] == '-'))
        {
            bool simplified = tag.Contains("Hans", StringComparison.OrdinalIgnoreCase)
                || tag.EndsWith("-CN", StringComparison.OrdinalIgnoreCase) || tag.EndsWith("-SG", StringComparison.OrdinalIgnoreCase);
            return simplified ? "chi_sim" : "chi_tra";
        }
        if (languageTag.Length >= 3 && languageTag.All(c => char.IsAsciiLetterLower(c) || c == '_')) return languageTag;
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            if (!string.IsNullOrEmpty(culture.ThreeLetterISOLanguageName) && culture.ThreeLetterISOLanguageName != "ivl")
                return culture.ThreeLetterISOLanguageName;
        }
        catch (CultureNotFoundException) { }
        return languageTag;
    }

    static string LanguageTagFor(string model) => model switch
    {
        "chi_tra" or "chi_tra_vert" => "zh-Hant",
        "chi_sim" or "chi_sim_vert" => "zh-Hans",
        "eng" => "en",
        "jpn" or "jpn_vert" => "ja",
        "kor" or "kor_vert" => "ko",
        _ => CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(culture => culture.ThreeLetterISOLanguageName == model)?.Name is { Length: > 0 } name ? name : model,
    };

    static string InstallCommand(IEnumerable<string> models)
        => "sudo apt install tesseract-ocr " + string.Join(' ', models.Select(model => "tesseract-ocr-" + model.Replace('_', '-')));

    static (byte[] Png, int Dpi) Prepare(Bitmap image)
    {
        var size = image.PixelSize;
        bool upscale = (long)size.Width * size.Height * 4 <= UpscaleLimitPixels;
        using var scaled = upscale ? Resize(image, new PixelSize(size.Width * 2, size.Height * 2)) : null;
        using var flattened = FlattenOnWhite(scaled ?? image).ToBitmap();
        return (ImageTools.EncodePng(flattened), upscale ? 192 : 96);
    }

    static string CleanUp(string output)
    {
        var lines = new List<string>();
        foreach (var raw in output.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\f", "").Split('\n'))
        {
            // One line per recognized text line, as Windows OCR returns; Tesseract's paragraph gaps are dropped.
            var line = CjkSpacing.Replace(raw.Trim(), "");
            if (line.Length > 0) lines.Add(line);
        }
        return string.Join(Environment.NewLine, lines);
    }

    static string Summary(string error, int exitCode)
    {
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("Estimating resolution", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var text = lines.Length > 0 ? string.Join(" ", lines.TakeLast(3)) : "exit code " + exitCode.ToString(CultureInfo.InvariantCulture);
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}

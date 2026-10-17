namespace SnipFlow.Services;

// Code images and text recognition (OCR) on Linux. Shared code-image strings live in I18n.CodeCapture.cs;
// Windows OCR diagnostics live in I18n.cs.
public static partial class I18n
{
    private static readonly Dictionary<string, (string English, string Simplified)> CodeOcrTranslations = new(StringComparer.Ordinal)
    {
        ["找不到 Tesseract OCR 文字辨識引擎。請在終端機執行「{0}」安裝後，重新開啟 SnipFlow 再試一次。文字辨識只在本機進行，不會上傳截圖。"] = (
            "The Tesseract OCR engine was not found. Install it by running \"{0}\" in a terminal, then reopen SnipFlow and try again. Text recognition runs on this computer; captures are not uploaded.",
            "找不到 Tesseract OCR 文字识别引擎。请在终端中运行“{0}”安装后，重新打开 SnipFlow 再试一次。文字识别只在本机进行，不会上传截图。"),
        ["Tesseract OCR 尚未安裝 {0} 語言資料。請在終端機執行「{1}」安裝後再試一次。"] = (
            "The Tesseract OCR language data for {0} is not installed. Install it by running \"{1}\" in a terminal, then try again.",
            "Tesseract OCR 尚未安装 {0} 语言数据。请在终端中运行“{1}”安装后再试一次。"),
        ["Tesseract OCR 無法辨識這張圖片：{0}"] = ("Tesseract OCR could not recognize this image: {0}", "Tesseract OCR 无法识别这张图片：{0}"),
        ["無法取得 Tesseract OCR 的語言清單：{0}"] = ("Could not read the Tesseract OCR language list: {0}", "无法获取 Tesseract OCR 的语言列表：{0}"),
    };

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterCodeOcr() => Register(CodeOcrTranslations);
}

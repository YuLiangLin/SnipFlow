namespace SnipFlow.Services;

public static partial class I18n
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterTextFont() => Register(new Dictionary<string, (string English, string Simplified)>(StringComparer.Ordinal)
    {
        ["字型"] = ("Font", "字体"),
        ["預設字型"] = ("Default font", "默认字体"),
        ["使用系統預設字型"] = ("Use the system default font", "使用系统默认字体"),
        ["未安裝，使用替代字型"] = ("Unavailable; using a fallback font", "未安装，使用替代字体"),
        ["文字字型已套用，但偏好設定未儲存。"] = ("The font was applied, but the preference could not be saved.", "文字字体已应用，但偏好设置未保存。"),
        ["工具已切換，但偏好設定未儲存。"] = ("The tool was changed, but the preference could not be saved.", "工具已切换，但偏好设置未保存。")
    });
}

using System.Runtime.CompilerServices;

namespace SnipFlow.Services;

internal static class NavigationTranslations
{
    [ModuleInitializer]
    internal static void Register() => I18n.Register(new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["離開合圖"] = ("Leave collage", "退出拼图"),
        ["返回進入合圖前的原圖；也可雙擊右側最近擷取開啟圖片。"] = ("Return to the original image, or double-click a recent capture on the right to open it.", "返回进入拼图前的原图；也可双击右侧最近截取打开图片。"),
        ["已離開合圖，返回原圖。"] = ("Left the collage and returned to the original image.", "已退出拼图，返回原图。"),
        ["已離開合圖，可從最近擷取開啟圖片。"] = ("Left the collage. Open an image from recent captures.", "已退出拼图，可从最近截取打开图片。"),
        ["無法離開合圖"] = ("Could not leave the collage", "无法退出拼图"),
        ["圖形設定已套用，但偏好設定未儲存。"] = ("Shape settings were applied, but preferences could not be saved.", "图形设置已应用，但偏好设置未保存。"),
    });
}

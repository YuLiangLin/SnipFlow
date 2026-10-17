namespace SnipFlow.Services;

// Scrolling capture on the Avalonia port: toolbar placement without capture exclusion (Linux), the
// Wayland screen stream and region-only observation. The original scrolling strings live in
// I18n.ScrollWorkflow.cs and I18n.cs.
public static partial class I18n
{
    private static readonly Dictionary<string, (string English, string Simplified)> ScrollTranslations = new(StringComparer.Ordinal)
    {
        ["Wayland 無法追蹤目標視窗，只比對框選範圍的畫面；請讓範圍保持可見、不被其他視窗遮住。"] = (
            "Wayland does not let SnipFlow track the target window, so only the pixels of the selected region are compared. Keep the region visible and uncovered.",
            "Wayland 无法追踪目标窗口，只比对框选范围的画面；请让范围保持可见、不被其他窗口遮住。"),
        ["範圍下方找不到視窗，只比對框選範圍的畫面；請讓範圍保持可見、不被其他視窗遮住。"] = (
            "No window was found under the region, so only the pixels of the selected region are compared. Keep the region visible and uncovered.",
            "范围下方找不到窗口，只比对框选范围的画面；请让范围保持可见、不被其他窗口遮住。"),
        ["擷取畫面的大小已改變，已保留現有內容；請完成後重新選取範圍。"] = (
            "The captured image size changed. Captured content is retained; finish and select the region again.",
            "截取画面的大小已改变，已保留现有内容；请完成后重新选择范围。"),
        ["正在準備螢幕擷取；請在系統對話框中選擇要分享的螢幕。"] = (
            "Preparing screen capture. Choose the screen to share in the system dialog.",
            "正在准备屏幕截取；请在系统对话框中选择要共享的屏幕。"),
        ["已取消螢幕分享；已保留現有內容。"] = (
            "Screen sharing was cancelled. Captured content is retained.",
            "已取消屏幕共享；已保留现有内容。"),
        ["螢幕分享已停止，已保留現有內容；按「繼續」重新分享。"] = (
            "Screen sharing stopped. Captured content is retained; choose Resume to share again.",
            "屏幕共享已停止，已保留现有内容；按「继续」重新共享。"),
        ["已暫停：完整操作面板會遮住截圖範圍。按「繼續」收合並繼續收集。"] = (
            "Paused: the full panel would cover the capture region. Choose Resume to collapse it and continue.",
            "已暂停：完整操作面板会遮住截图范围。按「继续」收起并继续收集。"),
        ["更多"] = ("More", "更多"),
        ["收合"] = ("Collapse", "收起"),
        ["展開完整操作面板；畫面不夠時會暫停收集。"] = (
            "Open the full panel. Collection pauses when there is no room for it beside the region.",
            "展开完整操作面板；画面不够时会暂停收集。"),
        ["收合成精簡操作條，避開截圖範圍。"] = (
            "Collapse to the compact bar outside the capture region.",
            "收起为精简操作条，避开截图范围。"),
    };

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterScroll() => Register(ScrollTranslations);
}

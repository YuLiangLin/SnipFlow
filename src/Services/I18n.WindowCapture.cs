namespace SnipFlow.Services;

public static partial class I18n
{
    private static readonly Dictionary<string, (string English, string Simplified)> WindowCaptureTranslations = new(StringComparer.Ordinal)
    {
        ["先選取要捲動的視窗"] = ("Select the window to scroll", "先选择要滚动的窗口"),
        ["在選定視窗內框選對話或程式碼區"] = ("Select chat or code inside this window", "在所选窗口内框选对话或代码区"),
        ["請在選定視窗的內容區內框選。"] = ("Select a region inside this window's content area.", "请在所选窗口的内容区内框选。"),
        ["選定視窗已關閉、隱藏或移動，請重新選取。"] = ("The selected window was closed, hidden or moved. Select it again.", "所选窗口已关闭、隐藏或移动，请重新选择。"),
        ["視窗已改變，請選取其他視窗。"] = ("The window changed. Select another window.", "窗口已改变，请选择其他窗口。"),
        ["選定視窗的位置、大小或縮放已改變，請重新選取。"] = ("The selected window's position, size or scale changed. Select it again.", "所选窗口的位置、大小或缩放已改变，请重新选择。"),
        ["無法啟用選定視窗，請先把它移到前景後重試。"] = ("Could not activate the selected window. Bring it to the foreground and try again.", "无法激活所选窗口，请先将它移到前台后重试。"),
        ["此視窗禁止擷取，請選取其他視窗。"] = ("This window restricts capture. Select another window.", "此窗口禁止截图，请选择其他窗口。"),
        ["顯示器排列已改變，或範圍不在螢幕內，請重新選取。"] = ("The display layout changed or the region is off-screen. Select it again.", "显示器布局已改变，或范围不在屏幕内，请重新选择。"),
        ["視窗狀態正在變更，請稍後重新選取。"] = ("Window state is changing. Select it again shortly.", "窗口状态正在变更，请稍后重新选择。"),
        ["框選範圍被其他視窗遮住，請重新框選。"] = ("Another window covers the region. Select an unobstructed region.", "框选范围被其他窗口遮挡，请重新框选。"),
        ["桌面畫面未能更新，請稍後重新選取。"] = ("The desktop image could not refresh. Select the region again shortly.", "桌面画面未能更新，请稍后重新选择。")
    };
}

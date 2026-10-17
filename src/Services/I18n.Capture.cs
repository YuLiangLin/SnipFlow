namespace SnipFlow.Services;

public static partial class I18n
{
    private static readonly Dictionary<string, (string English, string Simplified)> CaptureTranslations = new(StringComparer.Ordinal)
    {
        ["螢幕框選需要在 SnipFlow 應用程式中執行。"] = ("Region selection requires the SnipFlow application.", "屏幕框选需要在 SnipFlow 应用程序中运行。"),
        ["點一下，用系統工具選取視窗"] = ("Click to pick a window with the system tool", "点一下，用系统工具选择窗口"),
        ["Wayland 不允許程式直接讀取其他視窗，將開啟桌面的截圖選擇器。"] = ("Wayland does not let apps read other windows, so the desktop's screenshot picker opens.", "Wayland 不允许程序直接读取其他窗口，将打开桌面的截图选择器。"),
        ["Wayland 不允許程式直接讀取其他視窗，請改用框選或螢幕擷取。"] = ("Wayland does not let apps read other windows. Capture a region or a display instead.", "Wayland 不允许程序直接读取其他窗口，请改用框选或屏幕截图。"),
        ["Wayland 無法鎖定單一視窗，請直接框選要捲動的內容範圍。"] = ("Wayland cannot lock onto one window. Select the scrolling region directly.", "Wayland 无法锁定单个窗口，请直接框选要滚动的内容范围。"),
        ["已取消系統截圖。"] = ("The system screenshot was cancelled.", "已取消系统截图。"),
        ["找不到系統截圖服務（xdg-desktop-portal）。請安裝：sudo apt install xdg-desktop-portal xdg-desktop-portal-gnome，登出後重試。"] = ("The system screenshot service (xdg-desktop-portal) was not found. Install it: sudo apt install xdg-desktop-portal xdg-desktop-portal-gnome, then sign out and try again.", "找不到系统截图服务（xdg-desktop-portal）。请安装：sudo apt install xdg-desktop-portal xdg-desktop-portal-gnome，注销后重试。"),
        ["找不到系統截圖服務（xdg-desktop-portal）。請安裝：sudo apt install xdg-desktop-portal xdg-desktop-portal-kde，登出後重試。"] = ("The system screenshot service (xdg-desktop-portal) was not found. Install it: sudo apt install xdg-desktop-portal xdg-desktop-portal-kde, then sign out and try again.", "找不到系统截图服务（xdg-desktop-portal）。请安装：sudo apt install xdg-desktop-portal xdg-desktop-portal-kde，注销后重试。"),
        ["找不到系統截圖服務（xdg-desktop-portal）。請安裝 xdg-desktop-portal 與桌面對應的後端（例如 xdg-desktop-portal-gnome、-kde 或 -wlr），登出後重試。"] = ("The system screenshot service (xdg-desktop-portal) was not found. Install xdg-desktop-portal and the backend for your desktop (for example xdg-desktop-portal-gnome, -kde or -wlr), then sign out and try again.", "找不到系统截图服务（xdg-desktop-portal）。请安装 xdg-desktop-portal 与桌面对应的后端（例如 xdg-desktop-portal-gnome、-kde 或 -wlr），注销后重试。"),
        ["找不到系統的螢幕分享服務（xdg-desktop-portal ScreenCast）。請安裝 xdg-desktop-portal 與桌面對應的後端（例如 xdg-desktop-portal-gnome），登出後重試。"] = ("The system screen sharing service (xdg-desktop-portal ScreenCast) was not found. Install xdg-desktop-portal and the backend for your desktop (for example xdg-desktop-portal-gnome), then sign out and try again.", "找不到系统的屏幕共享服务（xdg-desktop-portal ScreenCast）。请安装 xdg-desktop-portal 与桌面对应的后端（例如 xdg-desktop-portal-gnome），注销后重试。"),
        ["系統拒絕了截圖要求。請在系統設定的隱私權（螢幕擷取）中允許 SnipFlow，然後重試。"] = ("The system denied the screenshot. Allow SnipFlow under Privacy (screen capture) in the system settings and try again.", "系统拒绝了截图请求。请在系统设置的隐私（屏幕截图）中允许 SnipFlow，然后重试。"),
        ["桌面的截圖工具無法完成擷取，請改用框選或螢幕擷取。"] = ("The desktop's screenshot tool could not complete the capture. Capture a region or a display instead.", "桌面的截图工具无法完成截图，请改用框选或屏幕截图。"),
        ["系統截圖服務沒有傳回圖片，請重試。"] = ("The system screenshot service returned no image. Try again.", "系统截图服务没有返回图片，请重试。"),
        ["系統拒絕了螢幕分享要求，請重試。"] = ("The system denied screen sharing. Try again.", "系统拒绝了屏幕共享请求，请重试。"),
        ["無法建立螢幕分享工作階段，請重試。"] = ("Could not create a screen sharing session. Try again.", "无法创建屏幕共享会话，请重试。"),
        ["螢幕分享沒有提供任何畫面，請重試。"] = ("Screen sharing provided no screen. Try again.", "屏幕共享没有提供任何画面，请重试。"),
        ["螢幕分享服務回報錯誤："] = ("The screen sharing service reported an error: ", "屏幕共享服务报告错误："),
        ["無法連線到 PipeWire，請確認 pipewire 服務正在執行。"] = ("Could not connect to PipeWire. Make sure the pipewire service is running.", "无法连接到 PipeWire，请确认 pipewire 服务正在运行。"),
        ["無法複製 PipeWire 連線。"] = ("Could not duplicate the PipeWire connection.", "无法复制 PipeWire 连接。"),
        ["無法讀取螢幕分享畫面："] = ("Could not read the shared screen: ", "无法读取屏幕共享画面："),
        ["無法建立螢幕分享的 GStreamer 管線："] = ("Could not create the GStreamer pipeline for screen sharing: ", "无法创建屏幕共享的 GStreamer 管线："),
        ["無法啟動畫面串流。"] = ("Could not start the screen stream.", "无法启动画面串流。"),
        ["畫面串流已結束。"] = ("The screen stream ended.", "画面串流已结束。"),
        ["無法啟動 GStreamer。"] = ("Could not start GStreamer.", "无法启动 GStreamer。"),
        ["需要 GStreamer 才能讀取 Wayland 螢幕畫面。請安裝：sudo apt install gstreamer1.0-pipewire gstreamer1.0-plugins-base"] = ("GStreamer is needed to read the Wayland screen. Install it: sudo apt install gstreamer1.0-pipewire gstreamer1.0-plugins-base", "需要 GStreamer 才能读取 Wayland 屏幕画面。请安装：sudo apt install gstreamer1.0-pipewire gstreamer1.0-plugins-base"),
        ["缺少 GStreamer 的 PipeWire 元件。請安裝：sudo apt install gstreamer1.0-pipewire"] = ("The GStreamer PipeWire plugin is missing. Install it: sudo apt install gstreamer1.0-pipewire", "缺少 GStreamer 的 PipeWire 组件。请安装：sudo apt install gstreamer1.0-pipewire"),
        ["缺少 GStreamer 元件 {0}。請安裝：sudo apt install gstreamer1.0-plugins-base gstreamer1.0-plugins-good"] = ("The GStreamer element {0} is missing. Install it: sudo apt install gstreamer1.0-plugins-base gstreamer1.0-plugins-good", "缺少 GStreamer 组件 {0}。请安装：sudo apt install gstreamer1.0-plugins-base gstreamer1.0-plugins-good"),
        ["無法連線到 X 顯示伺服器（DISPLAY），無法擷取螢幕。"] = ("Could not connect to the X display server (DISPLAY), so the screen cannot be captured.", "无法连接到 X 显示服务器（DISPLAY），无法截取屏幕。"),
        ["無法讀取滑鼠位置，請重新操作。"] = ("Could not read the pointer position. Try again.", "无法读取鼠标位置，请重新操作。"),
        ["無法讀取螢幕畫面，請稍後重試。"] = ("Could not read the screen. Try again shortly.", "无法读取屏幕画面，请稍后重试。"),
        ["沒有執行合成管理器時，只能擷取完整顯示在螢幕上、未被遮住的視窗。請把視窗移到前景並完整顯示，或改用框選。"] = ("Without a compositing manager, only windows that are fully on screen and uncovered can be captured. Bring the window to the front and show it completely, or capture a region.", "没有运行合成管理器时，只能截取完整显示在屏幕上、未被遮挡的窗口。请把窗口移到前台并完整显示，或改用框选。")
    };

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterCapture() => Register(CaptureTranslations);
}

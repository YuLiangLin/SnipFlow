namespace SnipFlow.Services;

/// <summary>Strings added by the cross-platform shell: Linux global shortcuts, desktop keyboard settings and package-managed updates.</summary>
public static partial class I18n
{
    static readonly Dictionary<string, (string English, string Simplified)> ShellTranslations = new(StringComparer.Ordinal)
    {
        ["{0} 無法啟用（錯誤 {1}）。"] = ("{0} could not be enabled (error {1}).", "{0} 无法启用（错误 {1}）。"),
        ["剪貼簿目前無法使用。"] = ("The clipboard is not available right now.", "剪贴板目前无法使用。"),
        ["正在等待桌面環境確認全域快捷鍵…"] = ("Waiting for the desktop to confirm the global shortcut…", "正在等待桌面环境确认全局快捷键…"),
        ["桌面環境未核准全域快捷鍵。"] = ("The desktop did not approve the global shortcut.", "桌面环境未批准全局快捷键。"),
        ["桌面環境未啟用全域快捷鍵。"] = ("The desktop did not enable the global shortcut.", "桌面环境未启用全局快捷键。"),
        ["桌面環境設定的觸發鍵：{0}"] = ("Trigger set by the desktop: {0}", "桌面环境设置的触发键：{0}"),
        ["由 GNOME 鍵盤快捷鍵啟動截圖。"] = ("A GNOME keyboard shortcut starts the capture.", "由 GNOME 键盘快捷键启动截图。"),
        ["GNOME 鍵盤快捷鍵指向其他位置的 SnipFlow，請按下方按鈕更新。"] = ("The GNOME keyboard shortcut starts SnipFlow from another location. Press the button below to update it.", "GNOME 键盘快捷键指向其他位置的 SnipFlow，请按下方按钮更新。"),
        ["此桌面不允許程式直接註冊全域快捷鍵。可按下方按鈕，加入 GNOME 鍵盤快捷鍵。"] = ("This desktop does not let apps register global shortcuts. Press the button below to add a GNOME keyboard shortcut.", "此桌面不允许程序直接注册全局快捷键。可按下方按钮，添加 GNOME 键盘快捷键。"),
        ["此桌面不允許程式直接註冊全域快捷鍵。請在桌面環境的鍵盤設定中，新增執行「{0}」的自訂快捷鍵。"] = ("This desktop does not let apps register global shortcuts. In your desktop's keyboard settings, add a custom shortcut that runs \"{0}\".", "此桌面不允许程序直接注册全局快捷键。请在桌面环境的键盘设置中，添加运行“{0}”的自定义快捷键。"),
        ["加入 GNOME 鍵盤快捷鍵"] = ("Add GNOME keyboard shortcut", "添加 GNOME 键盘快捷键"),
        ["GNOME 鍵盤快捷鍵已更新。"] = ("The GNOME keyboard shortcut was updated.", "GNOME 键盘快捷键已更新。"),
        ["GNOME 鍵盤快捷鍵未更新，請稍後重試。"] = ("The GNOME keyboard shortcut was not updated. Please try again later.", "GNOME 键盘快捷键未更新，请稍后重试。"),
        ["GNOME 鍵盤快捷鍵未更新：{0}"] = ("The GNOME keyboard shortcut was not updated: {0}", "GNOME 键盘快捷键未更新：{0}"),
        ["無法讀取 GNOME 鍵盤快捷鍵設定。"] = ("GNOME keyboard shortcut settings could not be read.", "无法读取 GNOME 键盘快捷键设置。"),
        ["套用桌面環境的淺色／深色設定。"] = ("Uses the desktop's light or dark setting.", "应用桌面环境的浅色／深色设置。"),
        ["登入桌面後在背景執行並常駐系統匣。"] = ("Runs in the background and stays in the system tray after you sign in.", "登录桌面后在后台运行并常驻系统托盘。"),
        ["由套件管理員更新"] = ("Updated by your package manager", "由软件包管理器更新"),
        ["此安裝方式由套件管理員或下載來源提供新版本；只有 AppImage 版可在 SnipFlow 內更新。"] = ("This installation gets new versions from its package manager or download source. Only the AppImage can update from within SnipFlow.", "此安装方式由软件包管理器或下载来源提供新版本；只有 AppImage 版可在 SnipFlow 内更新。"),
    };

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterShell() => Register(ShellTranslations);
}

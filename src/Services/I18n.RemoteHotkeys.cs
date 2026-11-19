namespace SnipFlow.Services;

public static partial class I18n
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterRemoteHotkeys() => Register(new Dictionary<string, (string English, string Simplified)>(StringComparer.Ordinal)
    {
        ["遠端視窗優先使用截圖快捷鍵"] = ("Let remote windows receive the capture shortcut", "远程窗口优先使用截图快捷键"),
        ["Windows 遠端桌面、mRemoteNG 或 RealVNC Viewer 在前景時暫停本機快捷鍵，切回本機後自動恢復。"] = ("Pause the local shortcut while Windows Remote Desktop, mRemoteNG, or RealVNC Viewer has focus. It resumes when you switch away.", "Windows 远程桌面、mRemoteNG 或 RealVNC Viewer 在前台时暂停本机快捷键，切回本机后自动恢复。"),
        ["本機截圖快捷鍵已暫停，遠端視窗優先。"] = ("The local capture shortcut is paused while a remote window has focus.", "本机截图快捷键已暂停，远程窗口优先。"),
        ["遠端視窗優先"] = ("Remote window has priority", "远程窗口优先"),
        ["無法暫停本機快捷鍵（Windows 錯誤 {0}）。"] = ("Could not pause the local shortcut (Windows error {0}).", "无法暂停本机快捷键（Windows 错误 {0}）。"),
        ["無法偵測遠端視窗焦點。請確認本機和遠端使用不同的快捷鍵。"] = ("Could not detect remote window focus. Use different shortcuts on the local and remote computers.", "无法检测远程窗口焦点。请确认本机和远程使用不同的快捷键。"),
    });
}

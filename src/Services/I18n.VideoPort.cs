namespace SnipFlow.Services;

/// <summary>Strings added by the cross-platform video editor (libvlc preview, ffmpeg/ffprobe on Linux).</summary>
public static partial class I18n
{
    static readonly Dictionary<string, (string English, string Simplified)> VideoPortTranslations = new(StringComparer.Ordinal)
    {
        ["影片預覽需要 VLC 媒體元件。請安裝：sudo apt install vlc（或 libvlc5 vlc-plugin-base）。其他編輯與匯出功能仍可使用。"] = (
            "Video preview needs the VLC media components. Install them with: sudo apt install vlc (or libvlc5 vlc-plugin-base). Editing and export still work.",
            "视频预览需要 VLC 媒体组件。请安装：sudo apt install vlc（或 libvlc5 vlc-plugin-base）。其他编辑与导出功能仍可使用。"),
        ["影片預覽無法啟動：{0}"] = ("Video preview could not start: {0}", "视频预览无法启动：{0}"),
        ["無法播放這部影片。"] = ("This video cannot be played.", "无法播放这部视频。"),
        ["請確認已安裝 VLC 外掛：sudo apt install vlc-plugin-base"] = (
            "Make sure the VLC plugins are installed: sudo apt install vlc-plugin-base",
            "请确认已安装 VLC 插件：sudo apt install vlc-plugin-base"),
        ["預覽畫面需要 ffmpeg。請安裝：sudo apt install ffmpeg"] = (
            "Preview frames need ffmpeg. Install it with: sudo apt install ffmpeg",
            "预览画面需要 ffmpeg。请安装：sudo apt install ffmpeg"),
        ["讀取影片需要 ffprobe。請安裝 ffmpeg：sudo apt install ffmpeg"] = (
            "Reading videos needs ffprobe. Install ffmpeg with: sudo apt install ffmpeg",
            "读取视频需要 ffprobe。请安装 ffmpeg：sudo apt install ffmpeg"),
        ["匯出影片需要 ffmpeg。請安裝：sudo apt install ffmpeg"] = (
            "Exporting videos needs ffmpeg. Install it with: sudo apt install ffmpeg",
            "导出视频需要 ffmpeg。请安装：sudo apt install ffmpeg"),
        ["無法讀取影片資訊：{0}"] = ("Could not read the video information: {0}", "无法读取视频信息：{0}"),
        ["影片沒有可用的視訊軌。"] = ("The video has no usable video track.", "视频没有可用的视频轨。"),
        ["無法確認影片檔案：{0}"] = ("Could not verify the video file: {0}", "无法确认视频文件：{0}"),
    };

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterVideoPort() => Register(VideoPortTranslations);
}

namespace SnipFlow.Services;

/// <summary>Strings added by the cross-platform screen recorder (ffmpeg, PulseAudio / PipeWire and the ScreenCast portal on Linux).</summary>
public static partial class I18n
{
    static readonly Dictionary<string, (string English, string Simplified)> RecordingTranslations = new(StringComparer.Ordinal)
    {
        ["螢幕錄影需要 ffmpeg。請安裝：sudo apt install ffmpeg"] = (
            "Screen recording needs ffmpeg. Install it with: sudo apt install ffmpeg",
            "屏幕录制需要 ffmpeg。请安装：sudo apt install ffmpeg"),
        ["無法連線到 X 顯示伺服器（DISPLAY），無法錄影。"] = (
            "Could not connect to the X display server (DISPLAY), so the screen cannot be recorded.",
            "无法连接到 X 显示服务器（DISPLAY），无法录制。"),
        ["錄製聲音需要 pactl。請安裝：sudo apt install pulseaudio-utils，或關閉系統聲音與麥克風。"] = (
            "Recording sound needs pactl. Install it with: sudo apt install pulseaudio-utils, or turn off system audio and the microphone.",
            "录制声音需要 pactl。请安装：sudo apt install pulseaudio-utils，或关闭系统声音和麦克风。"),
        ["找不到 PulseAudio 或 PipeWire 音訊服務，這次錄影不含聲音。"] = (
            "No PulseAudio or PipeWire sound server was found. This recording has no sound.",
            "找不到 PulseAudio 或 PipeWire 音频服务，本次录制不含声音。"),
        ["已取消螢幕分享，未開始錄影。"] = (
            "Screen sharing was cancelled. Recording did not start.",
            "已取消屏幕共享，未开始录制。"),
        ["分享的螢幕沒有包含錄影範圍。請重新開始錄影，並在系統的分享視窗中選擇包含錄影範圍的螢幕。"] = (
            "The shared screen does not contain the recording area. Start recording again and choose the screen that contains the area in the system's sharing dialog.",
            "共享的屏幕不包含录制范围。请重新开始录制，并在系统的共享窗口中选择包含录制范围的屏幕。"),
        ["螢幕分享已結束，已停止錄影；可儲存已錄影片。"] = (
            "Screen sharing ended, so recording stopped. You can save the captured video.",
            "屏幕共享已结束，已停止录制；可以保存已录视频。"),
        ["ffmpeg 無法完成錄影："] = ("ffmpeg could not record: ", "ffmpeg 无法完成录制："),
        ["ffmpeg 意外結束。"] = ("ffmpeg exited unexpectedly.", "ffmpeg 意外退出。"),
        ["這個 ffmpeg 不支援 PulseAudio 錄音。請安裝 Ubuntu 的 ffmpeg 套件，或關閉系統聲音與麥克風。"] = (
            "This ffmpeg cannot record from PulseAudio. Install Ubuntu's ffmpeg package, or turn off system audio and the microphone.",
            "此 ffmpeg 不支持 PulseAudio 录音。请安装 Ubuntu 的 ffmpeg 软件包，或关闭系统声音和麦克风。"),
        ["這個 ffmpeg 不支援 X11 螢幕錄影（x11grab）。請安裝 Ubuntu 的 ffmpeg 套件：sudo apt install ffmpeg"] = (
            "This ffmpeg cannot record the X11 screen (x11grab). Install Ubuntu's ffmpeg package: sudo apt install ffmpeg",
            "此 ffmpeg 不支持 X11 屏幕录制（x11grab）。请安装 Ubuntu 的 ffmpeg 软件包：sudo apt install ffmpeg"),
        ["這個 ffmpeg 不支援 H.264（libx264）編碼。請安裝 Ubuntu 的 ffmpeg 套件：sudo apt install ffmpeg"] = (
            "This ffmpeg cannot encode H.264 (libx264). Install Ubuntu's ffmpeg package: sudo apt install ffmpeg",
            "此 ffmpeg 不支持 H.264（libx264）编码。请安装 Ubuntu 的 ffmpeg 软件包：sudo apt install ffmpeg"),
        ["錄影範圍周圍沒有空間放置工具列，工具列會出現在影片中。"] = (
            "There is no room for the toolbar outside the recording area, so the toolbar will appear in the video.",
            "录制范围周围没有空间放置工具栏，工具栏会出现在视频中。"),
        ["工具列位於錄影範圍內，會被錄進影片。"] = (
            "The toolbar is inside the recording area and will be recorded.",
            "工具栏位于录制范围内，会被录进视频。"),
    };

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterRecording() => Register(RecordingTranslations);
}

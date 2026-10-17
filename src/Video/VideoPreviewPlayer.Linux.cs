using System.Reflection;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace SnipFlow.Video;

internal sealed partial class VideoPreviewPlayer
{
    static bool resolverInstalled;

    /// <summary>
    /// LibVLCSharp imports "libvlc"/"libvlccore", which .NET probes as libvlc.so — a symlink that only
    /// libvlc-dev installs. The runtime packages (libvlc5, libvlccore9) ship the versioned sonames.
    /// </summary>
    static partial void PrepareNativeLibrary()
    {
        if (resolverInstalled) return;
        resolverInstalled = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, Resolve);
        }
        catch (InvalidOperationException) { /* LibVLCSharp installed its own resolver. */ }
    }

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[] candidates = name switch
        {
            "libvlc" => ["libvlc.so.5", "libvlc.so"],
            "libvlccore" => ["libvlccore.so.9", "libvlccore.so"],
            _ => [],
        };
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle)) return handle;
        return IntPtr.Zero;
    }

    // Ubuntu splits libvlc's demuxers and decoders (MP4, avcodec) into vlc-plugin-base.
    static partial void DescribePlaybackFailure(ref string message)
        => message += "\n" + I18n.T("請確認已安裝 VLC 外掛：sudo apt install vlc-plugin-base");

    static partial void DescribeMissingLibrary(Exception exception, ref PreviewError message)
    {
        if (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            || exception.InnerException is DllNotFoundException
            || !NativeLibrary.TryLoad("libvlc.so.5", out _) && !NativeLibrary.TryLoad("libvlc.so", out _))
            message = new("影片預覽需要 VLC 媒體元件。請安裝：sudo apt install vlc（或 libvlc5 vlc-plugin-base）。其他編輯與匯出功能仍可使用。");
    }
}

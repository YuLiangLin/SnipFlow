using SnipFlow.Services;

namespace SnipFlow.Video;

/// <remarks>Windows reads MediaClip encoding properties; Linux reads the streams with ffprobe.</remarks>
internal static partial class VideoMetadata
{
    internal static async Task<VideoInfo> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(I18n.T("請選擇 MP4 影片或 SnipFlow 影片專案。"));
        var fileInfo = new FileInfo(full);
        if (!fileInfo.Exists) throw new FileNotFoundException(I18n.T("找不到原始影片，請確認檔案仍在原位置。"), full);
        if (fileInfo.Length < 128 || fileInfo.Length > 32L * 1024 * 1024 * 1024)
            throw new InvalidOperationException(I18n.T("影片檔案無效或超過 32 GB。"));
        full = VideoFileGuard.ResolveFile(full);
        var source = await LoadNativeAsync(full, cancellationToken);
        new VideoEditDocument(source).Validate();
        return source;
    }

    /// <summary>Reads the encoded size and duration of a resolved local MP4.</summary>
    private static partial Task<VideoInfo> LoadNativeAsync(string full, CancellationToken cancellationToken);
}

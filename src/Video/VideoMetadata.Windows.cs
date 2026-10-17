using global::Windows.Media.Editing;
using global::Windows.Storage;

namespace SnipFlow.Video;

internal static partial class VideoMetadata
{
    private static async partial Task<VideoInfo> LoadNativeAsync(string full, CancellationToken cancellationToken)
    {
        var file = await VideoNativeOperation.AwaitAsync(StorageFile.GetFileFromPathAsync(full), cancellationToken);
        var clip = await VideoNativeOperation.AwaitAsync(MediaClip.CreateFromFileAsync(file), cancellationToken);
        var properties = clip.GetVideoEncodingProperties();
        return new VideoInfo(full, checked((int)properties.Width), checked((int)properties.Height), clip.OriginalDuration);
    }
}

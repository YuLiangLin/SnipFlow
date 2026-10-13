using SnipFlow.Services;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using FileAttributes = System.IO.FileAttributes;
using WinRect = Windows.Foundation.Rect;

namespace SnipFlow.Video;

internal static class VideoExportService
{
    /// <summary>Call on the UI dispatcher; native encoding runs asynchronously.</summary>
    internal static async Task<string> ExportAsync(VideoEditDocument document, string destination, bool overwrite,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var snapshot = document.CreateSnapshot();
        snapshot.Validate();
        var output = VideoFileGuard.ValidateOutput(destination, snapshot.Source.SourcePath, ".mp4", overwrite);
        var composition = new MediaComposition();
        var partial = VideoFileGuard.CreatePartial(output, ".mp4");
        ExportWorkspace? workspace = null;
        var published = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspace = new ExportWorkspace();
            var sourceFile = await VideoNativeOperation.AwaitAsync(StorageFile.GetFileFromPathAsync(snapshot.Source.SourcePath), cancellationToken);
            var clip = await VideoNativeOperation.AwaitAsync(MediaClip.CreateFromFileAsync(sourceFile), cancellationToken);
            var sourceVideo = clip.GetVideoEncodingProperties();
            if (sourceVideo.Width != snapshot.Source.Width || sourceVideo.Height != snapshot.Source.Height
                || Math.Abs((clip.OriginalDuration - snapshot.Source.Duration).TotalMilliseconds) > 100)
                throw new InvalidOperationException(I18n.T("原始影片已變更，無法套用這份編輯專案。"));
            clip.TrimTimeFromStart = snapshot.TrimStart;
            clip.TrimTimeFromEnd = clip.OriginalDuration - snapshot.TrimEnd;
            composition.Clips.Add(clip);

            foreach (var item in snapshot.Overlays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Kind == VideoOverlayKind.Text && string.IsNullOrWhiteSpace(item.Text)) continue;
                var start = item.ShowEntireVideo ? snapshot.TrimStart : Max(item.Start, snapshot.TrimStart);
                var end = item.ShowEntireVideo ? snapshot.TrimEnd : Min(item.End, snapshot.TrimEnd);
                if (end <= start) continue;
                var pngPath = workspace.CreateFile(".png");
                var png = await VideoOverlayRenderer.WriteOverlayAsync(item, pngPath, cancellationToken);
                var imageClip = await VideoNativeOperation.AwaitAsync(MediaClip.CreateFromImageFileAsync(png, end - start), cancellationToken);
                var overlay = new MediaOverlay(imageClip)
                {
                    Delay = start - snapshot.TrimStart,
                    Position = new WinRect(item.X, item.Y, item.Width, item.Height), Opacity = 1,
                    AudioEnabled = false
                };
                var layer = new MediaOverlayLayer();
                layer.Overlays.Add(overlay);
                composition.OverlayLayers.Add(layer);
            }

            var sourceProfile = await VideoNativeOperation.AwaitAsync(MediaEncodingProfile.CreateFromFileAsync(sourceFile), cancellationToken);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)(snapshot.Source.Width & ~1);
            profile.Video.Height = (uint)(snapshot.Source.Height & ~1);
            profile.Video.FrameRate.Numerator = sourceVideo.FrameRate.Numerator == 0 ? 30u : sourceVideo.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = sourceVideo.FrameRate.Denominator == 0 ? 1u : sourceVideo.FrameRate.Denominator;
            profile.Video.Bitrate = (uint)Math.Clamp(sourceVideo.Bitrate > 0 ? (long)sourceVideo.Bitrate
                : (long)snapshot.Source.Width * snapshot.Source.Height * 6, 2_000_000, 40_000_000);
            if (sourceProfile.Audio is null) profile.Audio = null;
            else
            {
                profile.Audio.SampleRate = sourceProfile.Audio.SampleRate == 0 ? 48000u : sourceProfile.Audio.SampleRate;
                profile.Audio.ChannelCount = Math.Clamp(sourceProfile.Audio.ChannelCount, 1u, 2u);
                profile.Audio.Bitrate = 192000;
            }

            var targetFile = await VideoNativeOperation.AwaitAsync(StorageFile.GetFileFromPathAsync(VideoFileGuard.ResolveFile(partial)), cancellationToken);
            var operation = composition.RenderToFileAsync(targetFile, MediaTrimmingPreference.Precise, profile);
            var reason = await VideoNativeOperation.AwaitAsync(operation, cancellationToken, progress);
            if (reason != TranscodeFailureReason.None)
                throw new InvalidOperationException(I18n.F("影片匯出失敗：{0}", reason));
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(partial).Length < 128)
                throw new InvalidOperationException(I18n.T("影片匯出未產生完整檔案。"));
            VideoFileGuard.ValidateOutput(output, snapshot.Source.SourcePath, ".mp4", overwrite);
            File.Move(partial, output, overwrite);
            published = true;
            progress?.Report(100);
            return output;
        }
        finally
        {
            // Release composition references before removing only this job's owned files.
            composition.OverlayLayers.Clear();
            composition.Clips.Clear();
            workspace?.Dispose();
            if (!published) VideoFileGuard.DeleteOwnedFile(partial);
        }
    }

    private static TimeSpan Max(TimeSpan x, TimeSpan y) => x > y ? x : y;
    private static TimeSpan Min(TimeSpan x, TimeSpan y) => x < y ? x : y;

    private sealed class ExportWorkspace : IDisposable
    {
        private readonly string _directory;
        private readonly List<string> _files = [];

        internal ExportWorkspace()
        {
            var root = Path.Combine(SettingsStore.DataRoot, "VideoEdits");
            ValidateChain(root);
            Directory.CreateDirectory(root);
            ValidateChain(root);
            _directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            ValidateChain(_directory);
        }

        internal string CreateFile(string extension)
        {
            ValidateChain(_directory);
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + extension);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            _files.Add(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                ValidateChain(_directory);
                foreach (var path in _files) VideoFileGuard.DeleteOwnedFile(path);
                if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                    Directory.Delete(_directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SettingsStore.Log(error); }
        }

        private static void ValidateChain(string path)
        {
            for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            {
                if (!directory.Exists) continue;
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(I18n.T("影片暫存資料夾不是一般資料夾，已停止操作。"));
            }
        }
    }
}

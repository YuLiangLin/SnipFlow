using SnipFlow.Services;

namespace SnipFlow.Video;

/// <remarks>
/// Windows renders with Windows.Media.Editing (MediaComposition); Linux runs ffmpeg with the overlay
/// filter. Both write a partial file next to the destination and publish it only after a complete encode.
/// </remarks>
internal static partial class VideoExportService
{
    /// <summary>Call on the UI dispatcher; encoding runs asynchronously.</summary>
    internal static partial Task<string> ExportAsync(VideoEditDocument document, string destination, bool overwrite,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);

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

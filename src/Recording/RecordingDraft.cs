namespace SnipFlow.Recording;

/// <summary>Owns a local recording until the user chooses a final destination after stopping.</summary>
internal sealed class RecordingDraft : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Func<string, string> _translate;
    private readonly string _directory;
    private bool _ready;
    private bool _disposed;

    internal string OutputPath { get; }

    internal RecordingDraft(string draftRoot, Func<string, string>? translate = null)
    {
        _translate = translate ?? (text => text);
        _directory = Path.Combine(Path.GetFullPath(draftRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        OutputPath = Path.Combine(_directory, "recording.mp4");
    }

    internal void MarkReady(string completedPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(Path.GetFullPath(completedPath), OutputPath, PlatformInfo.PathComparison)
            || !File.Exists(OutputPath) || new FileInfo(OutputPath).Length < 128)
            throw new InvalidOperationException(_translate("錄影沒有產生有效影片。請等待畫面開始錄製後再停止。"));
        _ready = true;
    }

    internal async Task<string> SaveAsync(string destination, bool overwriteExisting,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? exportPath = null;
        var ownsExport = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_ready) throw new InvalidOperationException(_translate("請先停止錄影，再儲存影片。"));
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            var path = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(_translate("影片副檔名必須是 .mp4。"), nameof(destination));
            if (path.StartsWith(_directory + Path.DirectorySeparatorChar, PlatformInfo.PathComparison))
                throw new ArgumentException(_translate("請選擇其他影片儲存位置。"), nameof(destination));
            if (!overwriteExisting && File.Exists(path))
                throw new IOException(_translate("影片檔案已存在，請改用另一個檔名。"));

            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            exportPath = Path.Combine(directory, $".snipflow-save-{Guid.NewGuid():N}.partial.mp4");
            await using (var source = new FileStream(OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsExport = true;
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Publish a complete copy from the destination's own directory. Keep the
            // recording draft for retry if a copy, cancellation or rename fails.
            File.Move(exportPath, path, overwriteExisting);
            ownsExport = false;
            return path;
        }
        finally
        {
            try { if (ownsExport && exportPath is not null) File.Delete(exportPath); }
            finally { _operations.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            File.Delete(OutputPath);
            // Never recursively remove a directory or delete an unrelated file.
            if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
                Directory.Delete(_directory);
            _disposed = true;
        }
        finally { _operations.Release(); }
    }
}

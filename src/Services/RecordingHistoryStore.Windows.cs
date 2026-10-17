using global::Windows.Storage;
using global::Windows.Storage.FileProperties;
using SnipFlow.Video;

namespace SnipFlow.Services;

public static partial class RecordingHistoryStore
{
    private static async partial Task<Bitmap?> CreateThumbnailAsync(string path, TimeSpan? duration, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var file = await VideoNativeOperation.AwaitAsync(StorageFile.GetFileFromPathAsync(path), timeout.Token).ConfigureAwait(false);
            using var thumbnail = await VideoNativeOperation.AwaitAsync(file.GetThumbnailAsync(ThumbnailMode.VideosView, 240),
                timeout.Token).ConfigureAwait(false);
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size == 0
                || thumbnail.Size > MaximumThumbnailBytes) return null;
            using var stream = thumbnail.AsStreamForRead();
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                // Keep the WinRT stream alive until this read actually completes.
                read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (read == 0) break;
                if (bytes.Length + read > MaximumThumbnailBytes) return null;
                await bytes.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            var data = bytes.ToArray();
            return await Task.Run(() => DecodeThumbnail(data), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        { SettingsStore.Log(exception); return null; }
    }
}

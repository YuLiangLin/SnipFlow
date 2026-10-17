using global::Windows.Graphics.Imaging;
using global::Windows.Storage;
using WinBitmapEncoder = global::Windows.Graphics.Imaging.BitmapEncoder;

namespace SnipFlow.Video;

internal static partial class VideoOverlayRenderer
{
    internal static async Task<StorageFile> WriteOverlayAsync(VideoOverlay overlay, string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Layout in source pixels at 96 DPI, independent of the editor's viewbox or screen DPI.
        PixelBuffer pixels;
        using (var bitmap = Render(overlay)) pixels = PixelBuffer.FromBitmap(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        // The export workspace has already created and registered this owned file.
        var file = await VideoNativeOperation.AwaitAsync(StorageFile.GetFileFromPathAsync(VideoFileGuard.ResolveFile(path)), cancellationToken);
        using var stream = await VideoNativeOperation.AwaitAsync(file.OpenAsync(FileAccessMode.ReadWrite), cancellationToken);
        var encoder = await VideoNativeOperation.AwaitAsync(WinBitmapEncoder.CreateAsync(WinBitmapEncoder.PngEncoderId, stream), cancellationToken);
        // Encode the premultiplied buffer explicitly for MediaClip's image path.
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)pixels.Width, (uint)pixels.Height, 96, 96, pixels.Pixels);
        await VideoNativeOperation.AwaitAsync(encoder.FlushAsync(), cancellationToken);
        return file;
    }
}

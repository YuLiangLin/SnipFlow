using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace SnipFlow.Video;

/// <summary>
/// Shows the current preview frame stretched over the source-pixel surface (WPF MediaElement with
/// Stretch.Fill). Frames come from <see cref="VideoPreviewPlayer"/>; exact stills from ffmpeg cover them
/// after a paused seek, or replace them when libvlc is unavailable. Two bitmaps alternate so a frame
/// being composited is never overwritten.
/// </summary>
internal sealed class VideoFrameView : Control
{
    WriteableBitmap? front, back;
    Bitmap? still;

    public VideoFrameView()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>True when a decoded frame or still is visible.</summary>
    public bool HasImage => still is not null || front is not null;

    /// <summary>Copies one BGRX frame (UI thread) and shows it.</summary>
    public unsafe void Present(IntPtr pixels, int width, int height, int pitch)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (back is null || back.PixelSize.Width != width || back.PixelSize.Height != height)
        {
            // Not disposed: the compositor may still hold the previous frame for one more pass.
            back = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var target = back.Lock())
        {
            if (target.RowBytes == pitch)
                Buffer.MemoryCopy((void*)pixels, (void*)target.Address, (long)target.RowBytes * height, (long)pitch * height);
            else
                for (var row = 0; row < height; row++)
                    Buffer.MemoryCopy((void*)(pixels + row * pitch), (void*)(target.Address + row * target.RowBytes),
                        target.RowBytes, Math.Min(pitch, target.RowBytes));
        }
        (front, back) = (back, front);
        if (still is null) InvalidateVisual();
    }

    /// <summary>Shows a still frame (poster) over the decoded frames until <see cref="ReleaseStill"/>.</summary>
    public void ShowStill(Bitmap? image)
    {
        still = image;
        InvalidateVisual();
    }

    /// <summary>Returns to the decoded frames.</summary>
    public void ReleaseStill()
    {
        if (still is null) return;
        still = null;
        InvalidateVisual();
    }

    public void Clear()
    {
        still = null;
        front = back = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);
        IImage? image = still ?? (IImage?)front;
        if (image is null || bounds.Width <= 0 || bounds.Height <= 0) return;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
            context.DrawImage(image, new Rect(image.Size), bounds);
    }
}

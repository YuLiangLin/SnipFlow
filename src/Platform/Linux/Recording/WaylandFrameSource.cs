using SnipFlow.Platform.Linux.Wayland;

namespace SnipFlow.Platform.Linux.Recording;

/// <summary>
/// Frames of one desktop rectangle in a Wayland session: an xdg-desktop-portal ScreenCast read in-process by
/// <see cref="WaylandScreenStream"/> (GStreamer <c>pipewiresrc</c>), so the portal's PipeWire descriptor never
/// reaches a child process (<see cref="InheritedDescriptors"/> keeps the encoder from inheriting it). The
/// rectangle uses SnipFlow's (XWayland) desktop coordinates; frames keep the stream's resolution, which is
/// larger than the rectangle on scaled desktops.
/// </summary>
internal sealed class WaylandFrameSource : IDisposable
{
    private readonly WaylandScreenStream _stream;
    private readonly PixelRect _region;
    private long _copiedFrame = -1;
    private int _ended;
    private volatile bool _disposing;

    /// <summary>The encoded frame size: the first frame's size rounded down to even numbers for yuv420p.</summary>
    internal PixelSize FrameSize { get; }

    /// <summary>Raised once when the desktop or the user ends the screen share, or the stream fails.</summary>
    internal event EventHandler? Ended;

    internal bool HasEnded => Volatile.Read(ref _ended) != 0;

    private WaylandFrameSource(WaylandScreenStream stream, PixelRect region, PixelSize frameSize)
    {
        _stream = stream;
        _region = region;
        FrameSize = frameSize;
        stream.Stopped += (_, _) => RaiseEnded();
    }

    /// <summary>
    /// Starts the screen share (the desktop may ask which screen to share) and waits for the first frame of
    /// <paramref name="region"/>. Throws <see cref="OperationCanceledException"/> when the user cancels the
    /// desktop's dialog, and <see cref="InvalidOperationException"/> with a translated message on failures.
    /// </summary>
    internal static async Task<WaylandFrameSource> StartAsync(PixelRect region, bool captureCursor,
        Func<string, string> translate, CancellationToken cancellationToken)
    {
        var stream = new WaylandScreenStream(new ScreenCastOptions(ScreenCastSourceTypes.Monitor,
            captureCursor ? ScreenCastCursorMode.Embedded : ScreenCastCursorMode.Hidden));
        try
        {
            if (!await stream.StartAsync(cancellationToken).ConfigureAwait(false))
                throw new OperationCanceledException(translate("已取消螢幕分享，未開始錄影。"));
            PixelBuffer? first = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!stream.TryGetLatestFrame(region, out first))
            {
                if (!stream.IsRunning || DateTime.UtcNow >= deadline)
                    break;
                await stream.WaitForFrameAsync(stream.FrameCount, TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            if (first is null)
                throw new InvalidOperationException(stream.Failure is { } failure
                    ? translate("無法讀取螢幕分享畫面：") + failure
                    : translate("分享的螢幕沒有包含錄影範圍。請重新開始錄影，並在系統的分享視窗中選擇包含錄影範圍的螢幕。"));
            var width = first.Width & ~1;
            var height = first.Height & ~1;
            if (width < 2 || height < 2)
                throw new InvalidOperationException(translate("錄影範圍至少需要 2 × 2 像素。"));
            var source = new WaylandFrameSource(stream, region, new PixelSize(width, height));
            if (!stream.IsRunning)
                source.RaiseEnded();
            return source;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A zeroed (black) BGRA buffer of <see cref="FrameSize"/>.</summary>
    internal byte[] CreateFrameBuffer() => new byte[FrameSize.Width * FrameSize.Height * 4];

    /// <summary>
    /// Copies the newest frame into <paramref name="target"/> (BGRA, <see cref="FrameSize"/>, stride = width × 4)
    /// when the compositor delivered one since the last copy. Otherwise the previous pixels stay, which repeats the
    /// last frame while the screen does not change. Returns true when new pixels were copied.
    /// </summary>
    internal bool CopyLatest(byte[] target)
    {
        if (!_stream.IsRunning)
        {
            RaiseEnded();
            return false;
        }
        var count = _stream.FrameCount;
        if (count == _copiedFrame || !_stream.TryGetLatestFrame(_region, out PixelBuffer? pixels) || pixels is null)
            return false;
        _copiedFrame = count;
        var stride = FrameSize.Width * 4;
        var width = Math.Min(FrameSize.Width, pixels.Width);
        var height = Math.Min(FrameSize.Height, pixels.Height);
        if (width < FrameSize.Width || height < FrameSize.Height)
            Array.Clear(target); // A smaller frame (the stream changed size) leaves black, not stale pixels.
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(pixels.Pixels, row * pixels.Stride, target, row * stride, width * 4);
        return true;
    }

    private void RaiseEnded()
    {
        if (_disposing || Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _disposing = true;
        _stream.Dispose();
    }
}

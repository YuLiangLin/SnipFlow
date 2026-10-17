using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace SnipFlow.Video;

/// <summary>
/// Preview playback through libvlc. Decoded frames arrive through libvlc's video callbacks (vmem) as
/// 32-bit BGRX into a small ring of native buffers, and the newest one is copied into the
/// <see cref="VideoFrameView"/> on the UI thread. No native child window is involved, so the overlay
/// editing canvas stays on top of the video, and the audio is played by libvlc itself.
/// </summary>
/// <remarks>
/// libvlc raises its events on its own threads and must not be called back from them; every event is
/// posted to the UI dispatcher. All public members are UI-thread only.
/// </remarks>
internal sealed partial class VideoPreviewPlayer : IDisposable
{
    /// <summary>A translatable reason: an I18n source key and an optional {0} detail.</summary>
    internal sealed record PreviewError(string Key, string? Detail = null)
    {
        public string Text => Detail is null ? I18n.T(Key) : I18n.F(Key, Detail);
    }

    const int BufferCount = 4;
    const int MaximumPreviewSide = 1920;
    enum BufferState { Free, Locked, Ready, Presenting }

    static readonly object InitializationGate = new();
    static LibVLC? library;
    static PreviewError? initializationError;

    readonly MediaPlayer player;
    readonly VideoFrameView view;
    // libvlc keeps raw pointers to these delegates.
    readonly MediaPlayer.LibVLCVideoLockCb lockCallback;
    readonly MediaPlayer.LibVLCVideoUnlockCb unlockCallback;
    readonly MediaPlayer.LibVLCVideoDisplayCb displayCallback;
    readonly object frameGate = new();
    readonly IntPtr[] buffers = new IntPtr[BufferCount];
    readonly BufferState[] states = new BufferState[BufferCount];
    readonly long[] lockedAt = new long[BufferCount];
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DispatcherTimer loadTimeout = new() { Interval = TimeSpan.FromSeconds(15) };
    int frameWidth, frameHeight, framePitch, readyIndex = -1, presentQueued, nextBuffer;
    long lockSequence;
    Media? media;
    bool disposed, loading, pendingPlay, unmuteOnPause;
    TimeSpan? pendingSeek;
    // Set when a load starts; the first displayed frame finishes the load.
    volatile int generation;
    int awaitingFirstFrame;
    // libvlc reports its time about four times a second; the clock interpolates between reports.
    volatile bool clockRunning;
    long reportedTime, reportedAt, lastClock;

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    /// <summary>True once the current media shows a frame and can seek.</summary>
    public bool IsReady { get; private set; }

    VideoPreviewPlayer(LibVLC vlc, VideoFrameView target)
    {
        view = target;
        player = new MediaPlayer(vlc) { EnableKeyInput = false, EnableMouseInput = false };
        lockCallback = LockFrame;
        unlockCallback = UnlockFrame;
        displayCallback = DisplayFrame;
        player.Paused += (_, _) => Post(() =>
        {
            if (disposed || !unmuteOnPause) return;
            unmuteOnPause = false; player.Mute = false;
        });
        player.EndReached += (_, _) => Post(OnEnded);
        player.EncounteredError += (_, _) => Post(() =>
        {
            if (disposed) return;
            IsReady = false; loading = false; clockRunning = false; loadTimeout.Stop();
            Failed?.Invoke(FailureMessage());
        });
        player.TimeChanged += (_, args) =>
        {
            // Reports queued before a pause or seek would move the clock back to a stale time.
            if (!clockRunning) return;
            Interlocked.Exchange(ref reportedAt, clock.ElapsedMilliseconds);
            Interlocked.Exchange(ref reportedTime, args.Time);
        };
        loadTimeout.Tick += (_, _) =>
        {
            loadTimeout.Stop();
            if (disposed || !loading) return;
            StopPlayer();
            Failed?.Invoke(FailureMessage());
        };
    }

    /// <summary>Creates a player, or returns null with a translated reason when libvlc cannot be loaded.</summary>
    public static VideoPreviewPlayer? TryCreate(VideoFrameView target, out PreviewError? error)
    {
        var vlc = LoadLibrary(out error);
        if (vlc is null) return null;
        try { return new VideoPreviewPlayer(vlc, target); }
        catch (Exception exception) when (exception is VLCException or DllNotFoundException or EntryPointNotFoundException)
        {
            SettingsStore.Log(exception);
            error = new("影片預覽無法啟動：{0}", exception.Message);
            return null;
        }
    }

    static LibVLC? LoadLibrary(out PreviewError? error)
    {
        lock (InitializationGate)
        {
            if (library is null && initializationError is null)
            {
                try
                {
                    PrepareNativeLibrary();
                    Core.Initialize();
                    // Frames are delivered through callbacks, so libvlc never needs its own window or X connection.
                    library = new LibVLC(enableDebugLogs: false, LibraryOptions());
                }
                catch (Exception exception) when (exception is VLCException or DllNotFoundException
                    or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException or InvalidOperationException)
                {
                    SettingsStore.Log(exception);
                    initializationError = MissingLibraryMessage(exception);
                }
            }
            error = initializationError;
            return library;
        }
    }

    static string[] LibraryOptions()
    {
        var options = new List<string> { "--quiet", "--intf=dummy", "--no-video-title-show", "--no-osd",
            "--no-sub-autodetect-file", "--no-spu", "--no-snapshot-preview", "--no-stats" };
        if (PlatformInfo.IsLinux) options.Add("--no-xlib");
        return [.. options];
    }

    static partial void PrepareNativeLibrary();

    /// <summary>The translated, actionable message for a libvlc that is missing or cannot start.</summary>
    static partial void DescribeMissingLibrary(Exception exception, ref PreviewError message);

    static PreviewError MissingLibraryMessage(Exception exception)
    {
        var message = new PreviewError("影片預覽無法啟動：{0}", exception.Message);
        DescribeMissingLibrary(exception, ref message);
        return message;
    }

    /// <summary>Loads <paramref name="path"/> and stops on its first frame without an audible start.</summary>
    public void Open(string path, int sourceWidth, int sourceHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        StopPlayer();
        media?.Dispose(); media = null;
        var (width, height) = PreviewSize(sourceWidth, sourceHeight);
        ConfigureBuffers(width, height);
        player.SetVideoFormat("RV32", (uint)width, (uint)height, (uint)framePitch);
        player.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
        media = new Media(library!, path, FromType.FromPath);
        media.AddOption(":no-sub-autodetect-file");
        StartLoad();
    }

    /// <summary>
    /// Plays muted until the first frame is displayed, then pauses there. (":start-paused" stops the input
    /// before any frame is decoded, and libvlc 3 cannot step or seek a picture into an unopened output.)
    /// </summary>
    void StartLoad()
    {
        if (media is null) return;
        IsReady = false; loading = true; clockRunning = false; unmuteOnPause = false;
        generation++;
        if (player.State is not (VLCState.NothingSpecial or VLCState.Stopped)) player.Stop();
        player.Mute = true;
        Interlocked.Exchange(ref awaitingFirstFrame, 1);
        loadTimeout.Stop(); loadTimeout.Start();
        if (!player.Play(media))
        {
            loading = false; loadTimeout.Stop();
            Failed?.Invoke(FailureMessage());
        }
    }

    void OnFirstFrame(int loadGeneration)
    {
        if (disposed || media is null || !loading || loadGeneration != generation) return;
        loading = false; loadTimeout.Stop();
        IsReady = true;
        if (pendingPlay)
        {
            pendingPlay = false;
            if (pendingSeek is { } start) { pendingSeek = null; SeekCore(start); }
            player.Mute = false; RunClock();
        }
        else
        {
            player.SetPause(true); unmuteOnPause = true;
            if (pendingSeek is { } seek) { pendingSeek = null; SeekCore(seek); }
            else SeekCore(TimeSpan.Zero);
        }
        Opened?.Invoke();
    }

    /// <summary>Stops playback and releases the media; the last frame stays visible.</summary>
    public void Close()
    {
        if (disposed) return;
        StopPlayer();
        media?.Dispose(); media = null;
    }

    public void Play()
    {
        if (disposed || media is null) return;
        if (!IsReady || player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            pendingPlay = true;
            if (!loading) StartLoad();
            return;
        }
        unmuteOnPause = false; player.Mute = false;
        player.SetPause(false);
        RunClock();
    }

    /// <summary>Pauses; with <paramref name="at"/> the picture is moved to that time, which keeps the frame,
    /// the editor's playhead and the overlays in step despite libvlc's coarse clock.</summary>
    public void Pause(TimeSpan? at = null)
    {
        pendingPlay = false;
        if (disposed || !IsReady) return;
        clockRunning = false;
        if (player.State == VLCState.Playing) player.SetPause(true);
        if (at is { } time) SeekCore(time);
    }

    /// <summary>Seeks to a source time. Before the media is loaded the request is applied on load.</summary>
    public void Seek(TimeSpan value)
    {
        if (disposed || media is null) return;
        if (!IsReady || player.State is VLCState.Ended or VLCState.Stopped)
        {
            pendingSeek = value;
            if (!loading) StartLoad();
            return;
        }
        SeekCore(value);
    }

    void SeekCore(TimeSpan value)
    {
        var milliseconds = (long)Math.Max(0, value.TotalMilliseconds);
        Interlocked.Exchange(ref reportedAt, clock.ElapsedMilliseconds);
        Interlocked.Exchange(ref reportedTime, milliseconds);
        Interlocked.Exchange(ref lastClock, milliseconds);
        player.Time = milliseconds;
    }

    void RunClock()
    {
        Interlocked.Exchange(ref reportedAt, clock.ElapsedMilliseconds);
        clockRunning = true;
    }

    /// <summary>The playback clock in source time, interpolated between libvlc updates while playing.</summary>
    public TimeSpan Time
    {
        get
        {
            if (!IsReady) return pendingSeek ?? TimeSpan.Zero;
            var value = Interlocked.Read(ref reportedTime);
            if (clockRunning && player.State == VLCState.Playing)
            {
                value += Math.Clamp(clock.ElapsedMilliseconds - Interlocked.Read(ref reportedAt), 0, 1000);
                // Never run backwards between two reads while playing.
                value = Math.Max(value, Interlocked.Read(ref lastClock));
                Interlocked.Exchange(ref lastClock, value);
            }
            return TimeSpan.FromMilliseconds(value);
        }
    }

    public bool IsPlaying => !disposed && IsReady && player.State == VLCState.Playing;

    void OnEnded()
    {
        if (disposed || media is null) return;
        // An ended libvlc player cannot seek; the next seek or play reloads the media.
        IsReady = false; clockRunning = false;
        Ended?.Invoke();
    }

    void StopPlayer()
    {
        IsReady = false; loading = false; pendingPlay = false; pendingSeek = null; clockRunning = false;
        unmuteOnPause = false; generation++;
        Interlocked.Exchange(ref awaitingFirstFrame, 0);
        loadTimeout.Stop();
        // Stop joins libvlc's decoder and video threads, so no frame callback runs after it returns.
        player.Stop();
        lock (frameGate)
        {
            readyIndex = -1;
            for (var i = 0; i < BufferCount; i++) if (states[i] != BufferState.Presenting) states[i] = BufferState.Free;
        }
    }

    static (int Width, int Height) PreviewSize(int width, int height)
    {
        // Overlays are positioned in source pixels by the canvas; the preview frame is only scaled to fit.
        var scale = Math.Min(1d, MaximumPreviewSide / (double)Math.Max(width, height));
        return (Math.Max(2, (int)Math.Round(width * scale) & ~1), Math.Max(2, (int)Math.Round(height * scale) & ~1));
    }

    unsafe void ConfigureBuffers(int width, int height)
    {
        if (width == frameWidth && height == frameHeight && buffers[0] != IntPtr.Zero) return;
        FreeBuffers();
        frameWidth = width; frameHeight = height; framePitch = width * 4;
        for (var i = 0; i < BufferCount; i++)
            buffers[i] = (IntPtr)NativeMemory.AlignedAlloc((nuint)((long)framePitch * height), 64);
    }

    unsafe void FreeBuffers()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (buffers[i] != IntPtr.Zero) NativeMemory.AlignedFree((void*)buffers[i]);
            buffers[i] = IntPtr.Zero; states[i] = BufferState.Free;
        }
        readyIndex = -1;
    }

    // libvlc video thread: hand out a buffer that is neither waiting for nor being presented.
    IntPtr LockFrame(IntPtr opaque, IntPtr planes)
    {
        int index;
        lock (frameGate)
        {
            index = -1;
            for (var step = 0; step < BufferCount && index < 0; step++)
            {
                var candidate = (nextBuffer + step) % BufferCount;
                if (states[candidate] == BufferState.Free) index = candidate;
            }
            if (index < 0)
            {
                // Only reachable when libvlc dropped locked pictures without displaying them.
                for (var candidate = 0; candidate < BufferCount; candidate++)
                    if (states[candidate] == BufferState.Locked && (index < 0 || lockedAt[candidate] < lockedAt[index])) index = candidate;
                if (index < 0) index = readyIndex >= 0 ? readyIndex : 0;
                if (index == readyIndex) readyIndex = -1;
            }
            states[index] = BufferState.Locked;
            lockedAt[index] = ++lockSequence;
            nextBuffer = (index + 1) % BufferCount;
        }
        Marshal.WriteIntPtr(planes, buffers[index]);
        return index + 1;
    }

    void UnlockFrame(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    // libvlc video thread, at the frame's presentation time. While paused libvlc re-displays the
    // same picture periodically; only newly written pictures are presented.
    void DisplayFrame(IntPtr opaque, IntPtr picture)
    {
        var index = (int)picture - 1;
        if (index is < 0 or >= BufferCount) return;
        lock (frameGate)
        {
            if (states[index] != BufferState.Locked) return;
            if (readyIndex >= 0 && readyIndex != index) states[readyIndex] = BufferState.Free;
            states[index] = BufferState.Ready;
            readyIndex = index;
        }
        if (Interlocked.Exchange(ref awaitingFirstFrame, 0) == 1)
        {
            var loadGeneration = generation;
            Post(() => OnFirstFrame(loadGeneration));
        }
        if (Interlocked.Exchange(ref presentQueued, 1) == 0)
            Dispatcher.UIThread.Post(PresentFrame, DispatcherPriority.Render);
    }

    void PresentFrame()
    {
        Interlocked.Exchange(ref presentQueued, 0);
        int index;
        lock (frameGate)
        {
            if (disposed || readyIndex < 0) return;
            index = readyIndex; readyIndex = -1;
            states[index] = BufferState.Presenting;
        }
        try { view.Present(buffers[index], frameWidth, frameHeight, framePitch); }
        finally { lock (frameGate) if (states[index] == BufferState.Presenting) states[index] = BufferState.Free; }
    }

    static string FailureMessage()
    {
        var message = library?.LastLibVLCError is { Length: > 0 } error ? error : I18n.T("無法播放這部影片。");
        DescribePlaybackFailure(ref message);
        return message;
    }

    /// <summary>Adds a platform hint, such as the packages that provide libvlc's decoders.</summary>
    static partial void DescribePlaybackFailure(ref string message);

    static void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        if (disposed) return;
        Dispatcher.UIThread.VerifyAccess();
        try { StopPlayer(); }
        catch (Exception exception) { SettingsStore.Log(exception); }
        lock (frameGate) disposed = true;
        media?.Dispose(); media = null;
        player.Dispose();
        FreeBuffers();
    }
}

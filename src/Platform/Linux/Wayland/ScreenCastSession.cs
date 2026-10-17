using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SnipFlow.Capture;
using SnipFlow.Platform.Linux.Portal;
using Tmds.DBus.Protocol;

namespace SnipFlow.Platform.Linux.Wayland;

[Flags]
internal enum ScreenCastSourceTypes : uint { Monitor = 1, Window = 2, Virtual = 4 }

internal enum ScreenCastCursorMode : uint { Hidden = 1, Embedded = 2, Metadata = 4 }

/// <summary>What a ScreenCast session asks the desktop for.</summary>
/// <param name="SourceTypes">Monitors by default. Window is added only when the portal offers it.</param>
/// <param name="CursorMode">Hidden for screenshots; recording usually wants Embedded.</param>
/// <param name="Multiple">Allow sharing several monitors at once (needed for regions that span monitors).</param>
/// <param name="Persist">Ask for a restore token (portal version 4+) so later sessions skip the dialog.</param>
internal sealed record ScreenCastOptions(
    ScreenCastSourceTypes SourceTypes = ScreenCastSourceTypes.Monitor,
    ScreenCastCursorMode CursorMode = ScreenCastCursorMode.Hidden,
    bool Multiple = true,
    bool Persist = true);

/// <summary>
/// One PipeWire stream of a ScreenCast session. <see cref="Position"/> and <see cref="Size"/> are the
/// compositor's logical layout coordinates (null when the portal does not report them, e.g. windows).
/// </summary>
internal sealed record ScreenCastStream(uint NodeId, PixelPoint? Position, PixelSize? Size, ScreenCastSourceTypes SourceType)
{
    /// <summary>The value for <c>pipewiresrc path=…</c> (also accepted as <c>target-object</c>).</summary>
    public string PipeWirePath => NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The xdg-desktop-portal ScreenCast handshake: CreateSession → SelectSources → Start (the desktop may
/// ask the user which screen to share) → OpenPipeWireRemote. The session owns the PipeWire remote file
/// descriptor; GStreamer's <c>pipewiresrc fd=<see cref="PipeWireFd"/> path=<see cref="ScreenCastStream.PipeWirePath"/></c>
/// duplicates it, so several pipelines can use one session. Dispose closes the session and the descriptor.
/// </summary>
internal sealed class ScreenCastSession : IDisposable
{
    public const string Interface = "org.freedesktop.portal.ScreenCast";
    private const string SessionInterface = "org.freedesktop.portal.Session";
    private static readonly object TokenGate = new();
    private static Dictionary<string, string>? _restoreTokens;

    private readonly SafeFileHandle _remote;
    private IDisposable? _closedWatch;
    private int _closed;

    /// <summary>The portal session object path.</summary>
    public string Handle { get; }
    public IReadOnlyList<ScreenCastStream> Streams { get; }
    public ScreenCastOptions Options { get; }

    /// <summary>The PipeWire remote descriptor (valid until <see cref="Dispose"/>; do not close it).</summary>
    public int PipeWireFd => (int)_remote.DangerousGetHandle();

    /// <summary>Raised once when the desktop or the user ends the session (for example "Stop sharing").</summary>
    public event EventHandler? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    private ScreenCastSession(string handle, IReadOnlyList<ScreenCastStream> streams, SafeFileHandle remote, ScreenCastOptions options)
    {
        Handle = handle;
        Streams = streams;
        _remote = remote;
        Options = options;
    }

    /// <summary>A duplicate of the PipeWire descriptor for a consumer that must own its own copy.</summary>
    public int DuplicatePipeWireFd()
    {
        var duplicate = fcntl(PipeWireFd, 1030 /* F_DUPFD_CLOEXEC */, 3);
        if (duplicate < 0) throw new IOException(I18n.T("無法複製 PipeWire 連線。"));
        return duplicate;
    }

    /// <summary>True when the ScreenCast portal is reachable on the session bus.</summary>
    public static async Task<bool> IsAvailableAsync() =>
        await DesktopPortal.TryGetPropertyAsync(Interface, "version").ConfigureAwait(false) is not null;

    /// <summary>
    /// Runs the handshake. Returns null when the user cancels the desktop's share dialog. Throws an
    /// <see cref="InvalidOperationException"/> with a translated, actionable message on failures.
    /// </summary>
    public static async Task<ScreenCastSession?> StartAsync(ScreenCastOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ScreenCastOptions();
        var version = await DesktopPortal.TryGetPropertyAsync(Interface, "version").ConfigureAwait(false);
        if (version is not { Type: VariantValueType.UInt32 })
            throw new InvalidOperationException(I18n.T("找不到系統的螢幕分享服務（xdg-desktop-portal ScreenCast）。請安裝 xdg-desktop-portal 與桌面對應的後端（例如 xdg-desktop-portal-gnome），登出後重試。"));
        var portalVersion = version.Value.GetUInt32();
        var available = await ReadUInt32PropertyAsync("AvailableSourceTypes").ConfigureAwait(false) ?? (uint)ScreenCastSourceTypes.Monitor;
        var cursorModes = await ReadUInt32PropertyAsync("AvailableCursorModes").ConfigureAwait(false) ?? 0;
        var types = (uint)options.SourceTypes & available;
        if (types == 0) types = (uint)ScreenCastSourceTypes.Monitor & available;
        if (types == 0) types = available;

        string? session = null;
        try
        {
            var create = await DesktopPortal.RequestAsync(Interface, "CreateSession", "a{sv}", null,
                new Dictionary<string, VariantValue> { ["session_handle_token"] = VariantValue.String(DesktopPortal.NewToken()) },
                cancellationToken).ConfigureAwait(false);
            if (create.Cancelled) return null;
            if (!create.Succeeded || !create.Results.TryGetValue("session_handle", out var handleValue))
                throw new InvalidOperationException(I18n.T("無法建立螢幕分享工作階段，請重試。"));
            session = handleValue.Type == VariantValueType.ObjectPath ? handleValue.GetObjectPathAsString() : handleValue.GetString();
            var sessionPath = session;

            var select = new Dictionary<string, VariantValue>
            {
                ["types"] = VariantValue.UInt32(types),
                ["multiple"] = VariantValue.Bool(options.Multiple)
            };
            if (portalVersion >= 2 && (cursorModes & (uint)options.CursorMode) != 0)
                select["cursor_mode"] = VariantValue.UInt32((uint)options.CursorMode);
            var tokenKey = $"{types}:{(uint)options.CursorMode}:{options.Multiple}";
            if (options.Persist && portalVersion >= 4)
            {
                select["persist_mode"] = VariantValue.UInt32(2); // until revoked
                if (LoadRestoreToken(tokenKey) is { } token)
                    select["restore_token"] = VariantValue.String(token);
            }
            var selected = await DesktopPortal.RequestAsync(Interface, "SelectSources", "oa{sv}",
                (ref MessageWriter writer) => writer.WriteObjectPath(sessionPath), select, cancellationToken).ConfigureAwait(false);
            if (selected.Cancelled) { await CloseAsync(sessionPath).ConfigureAwait(false); return null; }
            if (!selected.Succeeded)
                throw new InvalidOperationException(I18n.T("系統拒絕了螢幕分享要求，請重試。"));

            var started = await DesktopPortal.RequestAsync(Interface, "Start", "osa{sv}",
                (ref MessageWriter writer) => { writer.WriteObjectPath(sessionPath); writer.WriteString(""); },
                null, cancellationToken).ConfigureAwait(false);
            if (started.Cancelled) { await CloseAsync(sessionPath).ConfigureAwait(false); return null; }
            if (!started.Succeeded)
            {
                // A stale restore token can make some portals fail; forget it so the next try asks again.
                SaveRestoreToken(tokenKey, null);
                throw new InvalidOperationException(I18n.T("系統拒絕了螢幕分享要求，請重試。"));
            }
            if (started.Results.TryGetValue("restore_token", out var restore) && restore.Type == VariantValueType.String)
                SaveRestoreToken(tokenKey, restore.GetString());
            var streams = started.Results.TryGetValue("streams", out var streamsValue) ? ParseStreams(streamsValue) : new List<ScreenCastStream>();
            if (streams.Count == 0)
                throw new InvalidOperationException(I18n.T("螢幕分享沒有提供任何畫面，請重試。"));

            var remote = await DesktopPortal.CallAsync(Interface, "OpenPipeWireRemote", "oa{sv}",
                (ref MessageWriter writer) =>
                {
                    writer.WriteObjectPath(sessionPath);
                    writer.WriteDictionary(new Dictionary<string, VariantValue>());
                },
                static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()).ConfigureAwait(false);
            if (remote is null || remote.IsInvalid)
                throw new InvalidOperationException(I18n.T("無法連線到 PipeWire，請確認 pipewire 服務正在執行。"));

            var result = new ScreenCastSession(sessionPath, streams, remote, options);
            await result.WatchClosedAsync().ConfigureAwait(false);
            session = null; // owned by the result now
            return result;
        }
        catch (DBusErrorReplyException exception)
        {
            throw new InvalidOperationException(I18n.T("螢幕分享服務回報錯誤：") + exception.ErrorMessage, exception);
        }
        finally
        {
            if (session is not null)
                await CloseAsync(session).ConfigureAwait(false);
        }
    }

    private static async Task<uint?> ReadUInt32PropertyAsync(string name) =>
        await DesktopPortal.TryGetPropertyAsync(Interface, name).ConfigureAwait(false) is { Type: VariantValueType.UInt32 } value
            ? value.GetUInt32() : null;

    private static List<ScreenCastStream> ParseStreams(VariantValue value)
    {
        var streams = new List<ScreenCastStream>();
        if (value.Type != VariantValueType.Array) return streams;
        for (var index = 0; index < value.Count; index++)
        {
            var item = value.GetItem(index);
            if (item.Type != VariantValueType.Struct || item.Count < 2) continue;
            var node = item.GetItem(0).GetUInt32();
            var properties = item.GetItem(1);
            PixelPoint? position = null;
            PixelSize? size = null;
            var sourceType = ScreenCastSourceTypes.Monitor;
            for (var entry = 0; properties.Type == VariantValueType.Dictionary && entry < properties.Count; entry++)
            {
                var pair = properties.GetDictionaryEntry(entry);
                var key = pair.Key.GetString();
                var data = pair.Value.Type == VariantValueType.Variant ? pair.Value.GetVariantValue() : pair.Value;
                if (key == "position" && data.Type == VariantValueType.Struct && data.Count >= 2)
                    position = new PixelPoint(data.GetItem(0).GetInt32(), data.GetItem(1).GetInt32());
                else if (key == "size" && data.Type == VariantValueType.Struct && data.Count >= 2)
                    size = new PixelSize(data.GetItem(0).GetInt32(), data.GetItem(1).GetInt32());
                else if (key == "source_type" && data.Type == VariantValueType.UInt32)
                    sourceType = (ScreenCastSourceTypes)data.GetUInt32();
            }
            streams.Add(new ScreenCastStream(node, position, size, sourceType));
        }
        return streams;
    }

    private async Task WatchClosedAsync()
    {
        try
        {
            var bus = await DesktopPortal.ConnectAsync().ConfigureAwait(false);
            _closedWatch = await bus.WatchSignalAsync(DesktopPortal.Service, Handle, SessionInterface, "Closed",
                (Notification notification) =>
                {
                    if (notification.Type is NotificationType.Value or NotificationType.ConnectionClosed)
                        MarkClosed();
                }, ObserverFlags.None, false, null).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DBusExceptionBase or InvalidOperationException)
        {
            SettingsStore.Log(exception);
        }
    }

    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task CloseAsync(string session)
    {
        try
        {
            var bus = await DesktopPortal.ConnectAsync().ConfigureAwait(false);
            MessageBuffer message;
            // Dispose the writer that created the message: disposing a copy made before
            // CreateMessage would return the message's buffer to the pool before it is sent.
            var w = bus.GetMessageWriter();
            try
            {
                w.WriteMethodCallHeader(DesktopPortal.Service, session, SessionInterface, "Close", "", MessageFlags.None);
                message = w.CreateMessage();
            }
            finally { w.Dispose(); }
            await bus.CallMethodAsync(message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DBusExceptionBase or InvalidOperationException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        var wasOpen = Interlocked.Exchange(ref _closed, 1) == 0;
        _closedWatch?.Dispose();
        _closedWatch = null;
        if (wasOpen)
        {
            _ = CloseAsync(Handle);
            Closed?.Invoke(this, EventArgs.Empty);
        }
        _remote.Dispose();
    }

    // Restore tokens are single use: every successful Start returns the next one. They are kept in
    // memory and in a small file next to the settings so a later SnipFlow run can skip the dialog.
    private static string TokenPath => Path.Combine(SettingsStore.DataRoot, "screencast-restore.json");

    private static string? LoadRestoreToken(string key)
    {
        lock (TokenGate)
        {
            if (_restoreTokens is null)
            {
                try
                {
                    _restoreTokens = File.Exists(TokenPath)
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokenPath)) ?? new()
                        : new();
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                    _restoreTokens = new();
                }
            }
            return _restoreTokens.TryGetValue(key, out var token) ? token : null;
        }
    }

    private static void SaveRestoreToken(string key, string? token)
    {
        lock (TokenGate)
        {
            _ = LoadRestoreToken(key);
            if (token is null) _restoreTokens!.Remove(key);
            else _restoreTokens![key] = token;
            try
            {
                Directory.CreateDirectory(SettingsStore.DataRoot);
                File.WriteAllText(TokenPath + ".tmp", JsonSerializer.Serialize(_restoreTokens));
                File.Move(TokenPath + ".tmp", TokenPath, true);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SettingsStore.Log(exception);
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int argument);
}

/// <summary>
/// A live screen stream for Wayland sessions: a ScreenCast session whose PipeWire streams are read
/// in-process with GStreamer (<c>pipewiresrc ! videoconvert ! BGRx ! appsink</c>). While running it
/// is <see cref="Active"/>, and <see cref="ScreenshotService.CaptureRectangle(PixelRect)"/> reads it
/// instead of asking the screenshot portal for every frame.
/// </summary>
internal sealed class WaylandScreenStream : IDisposable
{
    private static readonly object ActiveGate = new();
    private static readonly List<WaylandScreenStream> Running = new();
    private readonly ScreenCastOptions _options;
    private readonly List<StreamPipeline> _pipelines = new();
    private TaskCompletionSource<bool> _frameSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _frameCount;
    private bool _disposed;

    /// <summary>The most recently started stream that is still running, or null.</summary>
    public static WaylandScreenStream? Active
    {
        get { lock (ActiveGate) return Running.LastOrDefault(stream => stream.IsRunning); }
    }

    public WaylandScreenStream(ScreenCastOptions? options = null) => _options = options ?? new ScreenCastOptions();

    /// <summary>The portal session; recorders can build their own pipelines on <see cref="ScreenCastSession.PipeWireFd"/>.</summary>
    public ScreenCastSession? Session { get; private set; }

    public bool IsRunning => !_disposed && Session is { IsClosed: false } && _pipelines.Count > 0
        && _pipelines.Any(pipeline => pipeline.Failure is null);

    /// <summary>Frames received so far (all streams). Compare to wait for a newer frame.</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>The last pipeline failure, if any.</summary>
    public string? Failure => _pipelines.Select(pipeline => pipeline.Failure).FirstOrDefault(failure => failure is not null);

    public event EventHandler? Stopped;

    /// <summary>
    /// Starts the portal session and the pipelines, then waits briefly for the first frame.
    /// Returns false when the user cancelled the share dialog.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Session is not null) return IsRunning;
        GStreamer.EnsureAvailable("pipewiresrc", "videoconvert", "appsink");
        var session = await ScreenCastSession.StartAsync(_options, cancellationToken).ConfigureAwait(false);
        if (session is null) return false;
        Session = session;
        try
        {
            var monitors = CaptureNative.GetMonitors();
            var desktopRects = MapStreamsToDesktop(session.Streams, monitors);
            for (var index = 0; index < session.Streams.Count; index++)
                _pipelines.Add(new StreamPipeline(this, session.PipeWireFd, session.Streams[index], desktopRects[index]));
            foreach (var pipeline in _pipelines)
                pipeline.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
        session.Closed += (_, _) => Stop();
        lock (ActiveGate) Running.Add(this);
        // The compositor sends a first frame right after the stream starts and later ones on damage.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_pipelines.Any(pipeline => pipeline.Failure is null && !pipeline.HasFrame) && DateTime.UtcNow < deadline)
            await WaitForFrameAsync(FrameCount, deadline - DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (_pipelines.All(pipeline => pipeline.Failure is not null))
        {
            var failure = Failure;
            Dispose();
            throw new InvalidOperationException(I18n.T("無法讀取螢幕分享畫面：") + failure);
        }
        return true;
    }

    /// <summary>Waits until more than <paramref name="after"/> frames have arrived. False on timeout.</summary>
    public async Task<bool> WaitForFrameAsync(long after, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (FrameCount <= after)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !IsRunning) return FrameCount > after;
            var signal = Volatile.Read(ref _frameSignal).Task;
            try { await signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        return true;
    }

    internal void OnFrame()
    {
        Interlocked.Increment(ref _frameCount);
        var previous = Interlocked.Exchange(ref _frameSignal, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult(true);
    }

    /// <summary>
    /// Copies the newest pixels of a desktop rectangle (XWayland / physical desktop coordinates). The
    /// result keeps the stream's resolution, which can exceed the rectangle on scaled desktops. False
    /// when the rectangle is not completely inside the shared screens or no frame arrived yet.
    /// </summary>
    public bool TryGetLatestFrame(PixelRect region, out PixelBuffer? frame)
    {
        frame = null;
        if (!IsRunning || region.Width <= 0 || region.Height <= 0) return false;
        var live = _pipelines.Where(pipeline => pipeline.Failure is null).ToList();
        // Parts outside every shared screen are acceptable only in gaps between monitors, which
        // stay black like on X11 and Windows; a monitor the user did not share needs another source.
        var uncovered = CaptureGeometry.Subtract(region, live.Select(pipeline => pipeline.DesktopBounds));
        if (uncovered.Count > 0)
        {
            var monitors = CaptureNative.GetMonitors();
            if (uncovered.Any(piece => monitors.Any(monitor => CaptureGeometry.IntersectsWith(monitor.Bounds, piece))))
                return false;
        }
        var frames = new List<(PixelBuffer Pixels, PixelRect Piece)>();
        var scale = 0.0;
        foreach (var pipeline in live)
        {
            var piece = CaptureGeometry.Intersect(pipeline.DesktopBounds, region);
            if (CaptureGeometry.IsEmpty(piece)) continue;
            if (!pipeline.TryCopy(piece, out var pixels)) return false;
            frames.Add((pixels!, piece));
            scale = Math.Max(scale, pixels!.Width / (double)piece.Width);
        }
        if (frames.Count == 0) return false;
        if (frames.Count == 1 && frames[0].Piece == region)
        {
            frame = frames[0].Pixels;
            return true;
        }
        var output = new PixelBuffer(Math.Max(1, (int)Math.Round(region.Width * scale)), Math.Max(1, (int)Math.Round(region.Height * scale)));
        output.Fill(Colors.Black);
        foreach (var (pixels, piece) in frames)
        {
            var target = new PixelRect((int)Math.Round((piece.X - region.X) * scale), (int)Math.Round((piece.Y - region.Y) * scale),
                (int)Math.Round(piece.Width * scale), (int)Math.Round(piece.Height * scale));
            Resample(pixels, output, target);
        }
        frame = output;
        return true;
    }

    /// <summary><see cref="TryGetLatestFrame(PixelRect, out PixelBuffer?)"/> as a bitmap.</summary>
    public bool TryGetLatestFrame(PixelRect region, out Bitmap? frame)
    {
        frame = TryGetLatestFrame(region, out PixelBuffer? pixels) ? pixels!.ToBitmap() : null;
        return frame is not null;
    }

    private static void Resample(PixelBuffer source, PixelBuffer target, PixelRect area)
    {
        if (source.Width == area.Width && source.Height == area.Height)
        {
            target.Blit(source, area.X, area.Y);
            return;
        }
        // Nearest neighbour: build each distinct output row once, then copy it to repeated rows.
        var left = Math.Max(0, area.X);
        var right = Math.Min(target.Width, area.Right);
        if (right <= left) return;
        var columns = new int[right - left];
        for (var x = left; x < right; x++)
            columns[x - left] = Math.Min(source.Width - 1, (int)((x - area.X) * (long)source.Width / area.Width));
        var sourcePixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());
        var targetPixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target.Pixels.AsSpan());
        var previousSourceY = -1;
        var previousY = -1;
        for (var y = Math.Max(0, area.Y); y < Math.Min(target.Height, area.Bottom); y++)
        {
            var sourceY = Math.Min(source.Height - 1, (int)((y - area.Y) * (long)source.Height / area.Height));
            var row = targetPixels.Slice(y * target.Width + left, right - left);
            if (sourceY == previousSourceY)
            {
                targetPixels.Slice(previousY * target.Width + left, right - left).CopyTo(row);
                continue;
            }
            var sourceRow = sourcePixels.Slice(sourceY * source.Width, source.Width);
            for (var index = 0; index < columns.Length; index++)
                row[index] = sourceRow[columns[index]];
            previousSourceY = sourceY;
            previousY = y;
        }
    }

    /// <summary>
    /// Desktop (XWayland) rectangles for the streams. The portal reports compositor layout coordinates,
    /// which usually equal XWayland's; when they do not (for example XWayland in physical pixels on a
    /// scaled desktop), streams are paired with XWayland monitors in layout order.
    /// </summary>
    private static IReadOnlyList<PixelRect> MapStreamsToDesktop(IReadOnlyList<ScreenCastStream> streams, IReadOnlyList<CaptureMonitor> monitors)
    {
        var desktop = CaptureNative.DesktopBounds(monitors);
        PixelRect Logical(ScreenCastStream stream) => stream.Position is { } position && stream.Size is { } size
            ? new PixelRect(position, size) : stream.Size is { } only ? new PixelRect(desktop.Position, only) : desktop;
        var logical = streams.Select(Logical).ToList();
        if (logical.All(rect => monitors.Any(monitor => monitor.Bounds == rect)))
            return logical;
        if (streams.Count == 1 && monitors.Count == 1)
            return new[] { monitors[0].Bounds };
        var orderedMonitors = monitors.Select(monitor => monitor.Bounds).OrderBy(rect => rect.X).ThenBy(rect => rect.Y).ToList();
        if (streams.Count == monitors.Count && streams.All(stream => stream.Position is not null))
        {
            var order = logical.Select((rect, index) => (rect, index)).OrderBy(item => item.rect.X).ThenBy(item => item.rect.Y).ToList();
            var result = new PixelRect[streams.Count];
            for (var index = 0; index < order.Count; index++)
                result[order[index].index] = orderedMonitors[index];
            return result;
        }
        return logical;
    }

    private int _stopped;

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        lock (ActiveGate) Running.Remove(this);
        foreach (var pipeline in _pipelines)
            pipeline.Dispose();
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Session?.Dispose();
        var signal = Volatile.Read(ref _frameSignal);
        signal.TrySetResult(false);
    }

    /// <summary>One GStreamer pipeline per PipeWire stream; a worker thread keeps the newest sample.</summary>
    private sealed class StreamPipeline : IDisposable
    {
        private readonly WaylandScreenStream _owner;
        private readonly object _sampleGate = new();
        private readonly Thread _worker;
        private IntPtr _pipeline;
        private IntPtr _sink;
        private IntPtr _bus;
        private IntPtr _latest;
        private volatile bool _stopping;
        private int _disposed;

        public PixelRect DesktopBounds { get; }
        public string? Failure { get; private set; }
        public bool HasFrame { get; private set; }

        public StreamPipeline(WaylandScreenStream owner, int fd, ScreenCastStream stream, PixelRect desktopBounds)
        {
            _owner = owner;
            DesktopBounds = desktopBounds;
            var description = $"pipewiresrc fd={fd} path={stream.PipeWirePath} do-timestamp=true ! videoconvert ! "
                + "video/x-raw,format=BGRx ! appsink name=sink max-buffers=2 drop=true sync=false";
            _pipeline = GStreamer.gst_parse_launch(description, out var error);
            var message = GStreamer.ErrorMessage(error);
            if (_pipeline == IntPtr.Zero)
                throw new InvalidOperationException(I18n.T("無法建立螢幕分享的 GStreamer 管線：") + message);
            _sink = GStreamer.gst_bin_get_by_name(_pipeline, "sink");
            _bus = GStreamer.gst_element_get_bus(_pipeline);
            _worker = new Thread(Pull) { IsBackground = true, Name = "SnipFlow screen cast " + stream.NodeId };
        }

        public void Start()
        {
            if (GStreamer.gst_element_set_state(_pipeline, GStreamer.StatePlaying) == GStreamer.StateChangeFailure)
                Failure = GStreamer.PollFailure(_bus) ?? I18n.T("無法啟動畫面串流。");
            _worker.Start();
        }

        private void Pull()
        {
            while (!_stopping && Failure is null)
            {
                var sample = GStreamer.gst_app_sink_try_pull_sample(_sink, GStreamer.Second / 10);
                if (sample != IntPtr.Zero)
                {
                    IntPtr previous;
                    lock (_sampleGate)
                    {
                        previous = _latest;
                        _latest = sample;
                    }
                    if (previous != IntPtr.Zero) GStreamer.gst_mini_object_unref(previous);
                    HasFrame = true;
                    _owner.OnFrame();
                }
                if (GStreamer.PollFailure(_bus) is { } failure)
                {
                    Failure = failure;
                    SettingsStore.Log(new InvalidOperationException("Screen cast pipeline stopped: " + failure));
                }
            }
        }

        /// <summary>Copies a desktop rectangle inside <see cref="DesktopBounds"/> from the newest frame.</summary>
        public unsafe bool TryCopy(PixelRect piece, out PixelBuffer? pixels)
        {
            pixels = null;
            IntPtr sample;
            lock (_sampleGate)
            {
                sample = _latest;
                if (sample == IntPtr.Zero) return false;
                GStreamer.gst_mini_object_ref(sample);
            }
            try
            {
                var structure = GStreamer.gst_caps_get_structure(GStreamer.gst_sample_get_caps(sample), 0);
                if (structure == IntPtr.Zero || !GStreamer.gst_structure_get_int(structure, "width", out var width)
                    || !GStreamer.gst_structure_get_int(structure, "height", out var height) || width <= 0 || height <= 0)
                    return false;
                var buffer = GStreamer.gst_sample_get_buffer(sample);
                if (buffer == IntPtr.Zero || !GStreamer.gst_buffer_map(buffer, out var map, GStreamer.MapRead))
                    return false;
                try
                {
                    var stride = GStreamer.VideoMetaStride(buffer);
                    var offset = GStreamer.VideoMetaOffset(buffer);
                    if (stride <= 0) stride = (int)Math.Min(int.MaxValue, (long)map.Size / height);
                    if (stride < width * 4 || offset + (long)stride * (height - 1) + width * 4L > (long)map.Size)
                        return false;
                    var scaleX = width / (double)DesktopBounds.Width;
                    var scaleY = height / (double)DesktopBounds.Height;
                    var left = Math.Clamp((int)Math.Round((piece.X - DesktopBounds.X) * scaleX), 0, width - 1);
                    var top = Math.Clamp((int)Math.Round((piece.Y - DesktopBounds.Y) * scaleY), 0, height - 1);
                    var right = Math.Clamp((int)Math.Round((piece.Right - DesktopBounds.X) * scaleX), left + 1, width);
                    var bottom = Math.Clamp((int)Math.Round((piece.Bottom - DesktopBounds.Y) * scaleY), top + 1, height);
                    var result = new PixelBuffer(right - left, bottom - top);
                    var source = (byte*)map.Data + offset;
                    fixed (byte* destination = result.Pixels)
                    {
                        for (var row = 0; row < result.Height; row++)
                            Buffer.MemoryCopy(source + (long)(top + row) * stride + left * 4L, destination + (long)row * result.Stride,
                                result.Stride, result.Stride);
                    }
                    SnipFlow.Platform.Linux.Capture.X11Desktop.SetOpaque(result.Pixels);
                    pixels = result;
                    return true;
                }
                finally { GStreamer.gst_buffer_unmap(buffer, ref map); }
            }
            finally { GStreamer.gst_mini_object_unref(sample); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stopping = true;
            if (_worker.IsAlive && Thread.CurrentThread != _worker)
                _worker.Join(TimeSpan.FromSeconds(2));
            if (_pipeline != IntPtr.Zero)
                GStreamer.gst_element_set_state(_pipeline, GStreamer.StateNull);
            lock (_sampleGate)
            {
                if (_latest != IntPtr.Zero) GStreamer.gst_mini_object_unref(_latest);
                _latest = IntPtr.Zero;
            }
            if (_bus != IntPtr.Zero) GStreamer.gst_object_unref(_bus);
            if (_sink != IntPtr.Zero) GStreamer.gst_object_unref(_sink);
            if (_pipeline != IntPtr.Zero) GStreamer.gst_object_unref(_pipeline);
            _bus = _sink = _pipeline = IntPtr.Zero;
        }
    }
}

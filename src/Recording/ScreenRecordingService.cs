using System.Diagnostics;

namespace SnipFlow.Recording;

/// <summary>Records the selected physical desktop rectangle; it never sends video to a server.</summary>
/// <remarks>
/// The session logic is shared. The recorder itself is platform specific: Windows uses ScreenRecorderLib
/// (Windows.Graphics.Capture and Media Foundation, <c>ScreenRecordingService.Windows.cs</c>); Linux runs ffmpeg
/// with x11grab on X11 or xdg-desktop-portal ScreenCast frames on Wayland, and PulseAudio / PipeWire audio
/// (<c>ScreenRecordingService.Linux.cs</c>, <c>Platform/Linux/Recording</c>).
/// </remarks>
public sealed partial class ScreenRecordingService : IAsyncDisposable
{
    private static readonly SemaphoreSlim ActiveRecordingGate = new(1, 1);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _sync = new();
    private readonly Func<string, string> _translate;
    private readonly Stopwatch _elapsed = new();
    private ScreenRecorderBackend? _recorder;
    private TaskCompletionSource<bool>? _started;
    private TaskCompletionSource<string>? _nativeCompletion;
    private Task<string?>? _session;
    private volatile RecordingState _state = RecordingState.Ready;
    private volatile bool _hasLease;
    private bool _discardRequested;
    private bool _commitRequested;
    private bool _overwriteExisting;
    private string? _temporaryPath;
    private string? _outputPath;
    private TimeSpan _finalizeTimeout = FinalizeTimeout;
    private long _elapsedOffsetTicks;

    public ScreenRecordingService(Func<string, string>? translate = null)
        => _translate = translate ?? (text => text);

    /// <summary>May be raised on a native worker thread; GUI callers should dispatch it.</summary>
    public event EventHandler<RecordingState>? StateChanged;

    /// <summary>
    /// Raised (possibly on a worker thread) when the captured source ends on its own while recording, for
    /// example when the user stops screen sharing in a Wayland session. The recorder keeps the last frame
    /// until <see cref="StopAsync"/> is called, so the video recorded so far can still be saved.
    /// </summary>
    public event EventHandler<string>? Interrupted;

    public RecordingState State => _state;
    public bool IsActive => _hasLease;
    public TimeSpan Elapsed => _elapsed.Elapsed + TimeSpan.FromTicks(Interlocked.Read(ref _elapsedOffsetTicks));
    public string? LastError { get; private set; }
    /// <summary>A non-fatal note about the running recording (for example "recording without sound"), or null.</summary>
    public string? Notice { get; private set; }
    public string? ResultPath { get; private set; }
    public PixelRect CaptureBounds { get; private set; }
    public Task<string?> Completion => _session ?? Task.FromResult<string?>(null);

    public static PixelRect NormalizeBounds(PixelRect bounds, Func<string, string>? translate = null)
    {
        var text = translate ?? (value => value);
        var width = bounds.Width & ~1;
        var height = bounds.Height & ~1;
        if (width < 2 || height < 2)
            throw new ArgumentOutOfRangeException(nameof(bounds), text("錄影範圍至少需要 2 × 2 像素。"));
        if (width > 8192 || height > 8192 || (long)width * height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(bounds), text("錄影範圍過大，請選取較小的範圍。"));
        // Trim the right/bottom edge by at most one pixel; never capture outside the selection.
        return new PixelRect(bounds.X, bounds.Y, width, height);
    }

    public async Task StartAsync(
        PixelRect targetPhysicalBounds,
        string outputPath,
        RecordingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        var acquiredThisCall = false;
        try
        {
            if (_hasLease || _session is { IsCompleted: false })
                throw new InvalidOperationException(T("已有錄影正在進行。"));
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePlatformSupported();
            if (!await ActiveRecordingGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(T("已有錄影正在進行。"));

            _hasLease = true;
            acquiredThisCall = true;
            options ??= new RecordingOptions();
            ResultPath = null;
            LastError = null;
            Notice = null;
            _elapsed.Reset();
            Interlocked.Exchange(ref _elapsedOffsetTicks, 0);
            _session = null;
            _temporaryPath = null;
            _outputPath = null;
            _finalizeTimeout = FinalizeTimeout;
            _started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _nativeCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _discardRequested = false;
                _commitRequested = false;
                _overwriteExisting = options.OverwriteExisting;
            }
            SetState(RecordingState.Starting);
            CaptureBounds = NormalizeBounds(targetPhysicalBounds, _translate);

            _outputPath = PrepareOutputPath(outputPath, options.OverwriteExisting);
            var directory = Path.GetDirectoryName(_outputPath)!;
            _temporaryPath = Path.Combine(directory, $".snipflow-{Guid.NewGuid():N}.partial.mp4");
            // Check write access with an owned probe. The native file API requires a new path:
            // leaving this empty file in place makes its Media Foundation stream fail with E_FAIL.
            using (new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(_temporaryPath);
            // The platform prepares its sources here (on Wayland the desktop may ask which screen to share).
            var recorder = await CreateRecorderAsync(CaptureBounds, options, cancellationToken).ConfigureAwait(false);
            Notice = recorder.Notice;
            recorder.Recording += NativeRecording;
            recorder.Finishing += NativeFinishing;
            recorder.Completed += NativeCompleted;
            recorder.Failed += NativeFailed;
            recorder.Interrupted += NativeInterrupted;
            lock (_sync) _recorder = recorder;
            _session = CompleteSessionAsync(recorder);
            try
            {
                await Task.Run(() => recorder.Record(_temporaryPath), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _nativeCompletion.TrySetException(exception);
                throw;
            }
            await _started.Task.WaitAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A Start call rejected before acquiring a session must not disturb a running one.
            if (acquiredThisCall)
                await AbortStartAsync(exception).ConfigureAwait(false);
            if (acquiredThisCall && exception is not OperationCanceledException)
                LastError = DescribeError(exception);
            throw exception is OperationCanceledException ? exception : new InvalidOperationException(DescribeError(exception), exception);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
                throw new InvalidOperationException(T("尚未開始錄影。"));
            lock (_sync) _commitRequested = true;
            RequestStop();
            var result = await AwaitSessionAsync(cancellationToken).ConfigureAwait(false);
            return result ?? throw new InvalidOperationException(T("錄影已取消，未儲存影片。"));
        }
        finally { _operations.Release(); }
    }

    public async Task CancelAsync()
    {
        // Set this before waiting for a pending Stop so closing while finalizing discards the file.
        lock (_sync)
        {
            _discardRequested = true;
            _commitRequested = false;
        }
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            RequestStop();
            if (_session is not null)
            {
                if (_session.IsCompleted && !_hasLease && _temporaryPath is not null)
                    DeleteOwnedTemporaryFile();
                try { await AwaitSessionAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) when (!_hasLease && _temporaryPath is null) { }
            }
            else if (_hasLease)
            {
                DeleteOwnedTemporaryFile();
                ReleaseLease();
                SetState(RecordingState.Cancelled);
            }
        }
        finally { _operations.Release(); }
    }

    public async ValueTask DisposeAsync() => await CancelAsync().ConfigureAwait(false);

    /// <summary>Throws <see cref="PlatformNotSupportedException"/> when this desktop cannot record.</summary>
    private partial void EnsurePlatformSupported();

    /// <summary>Creates the platform recorder for <paramref name="target"/>; it does not record until <see cref="ScreenRecorderBackend.Record"/>.</summary>
    private partial Task<ScreenRecorderBackend> CreateRecorderAsync(PixelRect target, RecordingOptions options,
        CancellationToken cancellationToken);

    /// <summary>Platform-specific wording for recorder load failures, or null to use the exception message.</summary>
    private partial string? DescribePlatformError(Exception exception);

    private async Task<string?> CompleteSessionAsync(ScreenRecorderBackend recorder)
    {
        Exception? failure = null;
        string? result = null;
        try { await _nativeCompletion!.Task.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }

        // Native Dispose joins its recording worker. Never call it from a native event callback.
        lock (_sync) _recorder = null;
        try
        {
            recorder.Recording -= NativeRecording;
            recorder.Finishing -= NativeFinishing;
            recorder.Completed -= NativeCompleted;
            recorder.Failed -= NativeFailed;
            recorder.Interrupted -= NativeInterrupted;
            await Task.Run(recorder.Dispose).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LastError = T("錄影元件無法釋放，請重新啟動 SnipFlow。") + " " + exception.Message;
            SetState(RecordingState.Failed);
            // Keep the process-wide lease if native shutdown was not confirmed.
            throw new InvalidOperationException(LastError, exception);
        }

        _elapsed.Stop();
        try
        {
            lock (_sync)
            {
                if (failure is null && _commitRequested && !_discardRequested)
                {
                    if (_temporaryPath is null || !File.Exists(_temporaryPath) || new FileInfo(_temporaryPath).Length < 128)
                        throw new InvalidOperationException(T("錄影沒有產生有效影片。請等待畫面開始錄製後再停止。"));
                    File.Move(_temporaryPath, _outputPath!, _overwriteExisting);
                    _temporaryPath = null;
                    result = ResultPath = _outputPath;
                }
            }
            DeleteOwnedTemporaryFile();
        }
        catch (Exception exception)
        {
            failure ??= exception;
            try { DeleteOwnedTemporaryFile(); }
            catch (Exception cleanupException) { failure = new IOException(T("無法清除未完成影片。") + " " + cleanupException.Message, failure); }
        }
        finally { ReleaseLease(); }

        if (failure is not null && failure is not OperationCanceledException)
        {
            LastError = DescribeError(failure);
            SetState(RecordingState.Failed);
            throw new InvalidOperationException(LastError, failure);
        }
        SetState(result is null ? RecordingState.Cancelled : RecordingState.Completed);
        return result;
    }

    private async Task<string?> AwaitSessionAsync(CancellationToken cancellationToken)
    {
        try { return await _session!.WaitAsync(_finalizeTimeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            lock (_sync) { _discardRequested = true; _commitRequested = false; }
            RequestStop();
            var failure = exception is TimeoutException
                ? new TimeoutException(T("影片收尾逾時，正在停止錄影並清除未完成檔案。"), exception)
                : exception;
            _nativeCompletion?.TrySetException(failure);
            // Do not free the capture lease or remove files until underlying native I/O has joined.
            try { await _session!.ConfigureAwait(false); } catch (Exception) { }
            throw failure;
        }
    }

    private async Task AbortStartAsync(Exception exception)
    {
        lock (_sync) { _discardRequested = true; _commitRequested = false; }
        RequestStop();
        _nativeCompletion?.TrySetException(exception);
        if (_session is not null)
        {
            try { await _session.ConfigureAwait(false); } catch (Exception) { }
        }
        else
        {
            try { DeleteOwnedTemporaryFile(); }
            finally { ReleaseLease(); }
            SetState(exception is OperationCanceledException ? RecordingState.Cancelled : RecordingState.Failed);
        }
    }

    private void RequestStop()
    {
        lock (_sync)
        {
            if (_recorder is null)
                return;
            if (_state is RecordingState.Starting or RecordingState.Recording)
                SetState(RecordingState.Finishing);
            // Linux recorders may need longer to finalize a long recording (moving the MP4 index to the front).
            _finalizeTimeout = _recorder.FinalizeTimeout ?? FinalizeTimeout;
            _recorder.Stop();
        }
    }

    private void NativeRecording(object? sender, TimeSpan alreadyRecorded)
    {
        Interlocked.Exchange(ref _elapsedOffsetTicks, alreadyRecorded.Ticks);
        _elapsed.Start();
        SetState(RecordingState.Recording);
        _started?.TrySetResult(true);
    }

    private void NativeFinishing(object? sender, EventArgs args)
    {
        _elapsed.Stop();
        SetState(RecordingState.Finishing);
    }

    private void NativeCompleted(object? sender, string filePath)
        => _nativeCompletion?.TrySetResult(filePath);

    private void NativeFailed(object? sender, Exception exception)
    {
        _started?.TrySetException(exception);
        _nativeCompletion?.TrySetException(exception);
    }

    private void NativeInterrupted(object? sender, string message)
    {
        if (_state == RecordingState.Recording)
            Interrupted?.Invoke(this, message);
    }

    private string PrepareOutputPath(string path, bool overwriteExisting)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException(T("請選擇影片儲存位置。"), nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(T("影片副檔名必須是 .mp4。"), nameof(path));
        if (File.Exists(fullPath) && !overwriteExisting)
            throw new IOException(T("影片檔案已存在，請改用另一個檔名。"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private void DeleteOwnedTemporaryFile()
    {
        if (_temporaryPath is null)
            return;
        File.Delete(_temporaryPath);
        _temporaryPath = null;
    }

    private void ReleaseLease()
    {
        if (!_hasLease)
            return;
        _hasLease = false;
        ActiveRecordingGate.Release();
    }

    private void SetState(RecordingState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private string DescribeError(Exception exception) => DescribePlatformError(exception) ?? exception.Message;

    private string T(string text) => _translate(text);
}

/// <summary>
/// One platform recording behind <see cref="ScreenRecordingService"/>. Like ScreenRecorderLib's native callbacks,
/// its events may be raised on worker threads, and <see cref="Dispose"/> joins the workers, so it must not be
/// called from one of these events.
/// </summary>
internal abstract class ScreenRecorderBackend
{
    /// <summary>Frames are being written. The argument is the media time already recorded when this is raised.</summary>
    internal event EventHandler<TimeSpan>? Recording;
    /// <summary>The recorder stopped collecting and is finalizing the file.</summary>
    internal event EventHandler? Finishing;
    /// <summary>The file is complete. The argument is its path.</summary>
    internal event EventHandler<string>? Completed;
    internal event EventHandler<Exception>? Failed;
    /// <summary>The source ended on its own (translated message); the recorder keeps running until <see cref="Stop"/>.</summary>
    internal event EventHandler<string>? Interrupted;

    /// <summary>A non-fatal, translated note about this recording (for example missing sound), or null.</summary>
    internal string? Notice { get; init; }

    /// <summary>How long a requested stop may take to finalize, or null for the default.</summary>
    internal virtual TimeSpan? FinalizeTimeout => null;

    /// <summary>Starts recording into <paramref name="path"/>; <see cref="Recording"/> follows once frames flow.</summary>
    internal abstract void Record(string path);

    /// <summary>Requests a graceful stop without blocking; <see cref="Completed"/> or <see cref="Failed"/> follows.</summary>
    internal abstract void Stop();

    /// <summary>Releases native resources and joins workers; stops (or kills) anything still running.</summary>
    internal abstract void Dispose();

    protected void OnRecording(TimeSpan alreadyRecorded) => Recording?.Invoke(this, alreadyRecorded);
    protected void OnFinishing() => Finishing?.Invoke(this, EventArgs.Empty);
    protected void OnCompleted(string path) => Completed?.Invoke(this, path);
    protected void OnFailed(Exception exception) => Failed?.Invoke(this, exception);
    protected void OnInterrupted(string message) => Interrupted?.Invoke(this, message);
}

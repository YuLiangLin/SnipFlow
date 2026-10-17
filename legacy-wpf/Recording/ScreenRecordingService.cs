using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorderLib;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Recording;

/// <summary>Records the selected physical desktop rectangle; it never sends video to a server.</summary>
public sealed class ScreenRecordingService : IAsyncDisposable
{
    private static readonly SemaphoreSlim ActiveRecordingGate = new(1, 1);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _sync = new();
    private readonly Func<string, string> _translate;
    private readonly Stopwatch _elapsed = new();
    private Recorder? _recorder;
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

    public ScreenRecordingService(Func<string, string>? translate = null)
        => _translate = translate ?? (text => text);

    /// <summary>May be raised on a native worker thread; GUI callers should dispatch it.</summary>
    public event EventHandler<RecordingState>? StateChanged;
    public RecordingState State => _state;
    public bool IsActive => _hasLease;
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public string? LastError { get; private set; }
    public string? ResultPath { get; private set; }
    public DrawingRectangle CaptureBounds { get; private set; }
    public Task<string?> Completion => _session ?? Task.FromResult<string?>(null);

    public static DrawingRectangle NormalizeBounds(DrawingRectangle bounds, Func<string, string>? translate = null)
    {
        var text = translate ?? (value => value);
        var width = bounds.Width & ~1;
        var height = bounds.Height & ~1;
        if (width < 2 || height < 2)
            throw new ArgumentOutOfRangeException(nameof(bounds), text("錄影範圍至少需要 2 × 2 像素。"));
        if (width > 8192 || height > 8192 || (long)width * height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(bounds), text("錄影範圍過大，請選取較小的範圍。"));
        // Trim the right/bottom edge by at most one pixel; never capture outside the selection.
        return new DrawingRectangle(bounds.X, bounds.Y, width, height);
    }

    public async Task StartAsync(
        DrawingRectangle targetPhysicalBounds,
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
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                throw new PlatformNotSupportedException(T("螢幕錄影需要 Windows 10 2004 或更新版本。"));
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException(T("請使用 SnipFlow 的 Windows x64 版本錄影。"));
            if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
                throw new PlatformNotSupportedException(T("目前 Windows 工作階段不支援螢幕錄影。請確認顯示卡驅動程式及遠端桌面設定。"));
            if (!await ActiveRecordingGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(T("已有錄影正在進行。"));

            _hasLease = true;
            acquiredThisCall = true;
            options ??= new RecordingOptions();
            ResultPath = null;
            LastError = null;
            _elapsed.Reset();
            _session = null;
            _temporaryPath = null;
            _outputPath = null;
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
            var recorderOptions = CreateRecorderOptions(CaptureBounds, options);
            var recorder = await Task.Run(() => Recorder.CreateRecorder(recorderOptions), cancellationToken).ConfigureAwait(false);
            recorder.OnStatusChanged += NativeStatusChanged;
            recorder.OnRecordingComplete += NativeCompleted;
            recorder.OnRecordingFailed += NativeFailed;
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

    private RecorderOptions CreateRecorderOptions(DrawingRectangle target, RecordingOptions options)
    {
        var sources = new List<RecordingSourceBase>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var intersection = DrawingRectangle.Intersect(target, screen.Bounds);
            if (intersection.Width <= 0 || intersection.Height <= 0)
                continue;
            sources.Add(new DisplayRecordingSource(screen.DeviceName)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture,
                IsCursorCaptureEnabled = options.CaptureCursor,
                SourceRect = new ScreenRect(intersection.X - screen.Bounds.X, intersection.Y - screen.Bounds.Y,
                    intersection.Width, intersection.Height),
                OutputSize = new ScreenSize(intersection.Width, intersection.Height),
                Position = new ScreenPoint(intersection.X - target.X, intersection.Y - target.Y),
                AnchorPoint = Anchor.TopLeft,
                Stretch = StretchMode.None
            });
        }
        if (sources.Count == 0)
            throw new InvalidOperationException(T("選取範圍不在任何可錄製的螢幕內，請重新框選。"));

        var audio = new AudioOptions
        {
            IsAudioEnabled = options.SystemAudio || options.Microphone,
            Channels = AudioChannels.Stereo,
            Bitrate = AudioBitrate.bitrate_128kbps
        };
        if (options.SystemAudio)
            audio.AudioSources.Add(LoopbackAudioSource.Default
                ?? throw new InvalidOperationException(T("找不到系統聲音輸出裝置。請連接喇叭或耳機，或關閉系統聲音。")));
        if (options.Microphone)
            audio.AudioSources.Add(CaptureAudioSource.Default
                ?? throw new InvalidOperationException(T("找不到麥克風。請連接麥克風並允許桌面應用程式存取，或關閉麥克風。")));

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = sources },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(target.Width, target.Height),
                SourceRect = new ScreenRect(0, 0, target.Width, target.Height),
                Stretch = StretchMode.None
            },
            AudioOptions = audio,
            LogOptions = new LogOptions
            {
                IsLogEnabled = !string.IsNullOrWhiteSpace(options.DiagnosticLogPath),
                LogFilePath = options.DiagnosticLogPath,
                LogSeverityLevel = LogLevel.Trace
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = options.CaptureCursor, IsMouseClicksDetected = false },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = 30,
                IsFixedFramerate = true,
                IsThrottlingDisabled = false,
                IsHardwareEncodingEnabled = options.HardwareEncoding,
                IsMp4FastStartEnabled = true,
                IsFragmentedMp4Enabled = false,
                Quality = 80,
                Bitrate = (int)Math.Clamp((long)target.Width * target.Height * 5, 3_000_000, 18_000_000),
                Encoder = new H264VideoEncoder { EncoderProfile = H264Profile.Main, BitrateMode = H264BitrateControlMode.UnconstrainedVBR }
            }
        };
    }

    private async Task<string?> CompleteSessionAsync(Recorder recorder)
    {
        Exception? failure = null;
        string? result = null;
        try { await _nativeCompletion!.Task.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }

        // Native Dispose joins its recording worker. Never call it from a native event callback.
        lock (_sync) _recorder = null;
        try
        {
            recorder.OnStatusChanged -= NativeStatusChanged;
            recorder.OnRecordingComplete -= NativeCompleted;
            recorder.OnRecordingFailed -= NativeFailed;
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
        try { return await _session!.WaitAsync(FinalizeTimeout, cancellationToken).ConfigureAwait(false); }
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
            _recorder.Stop();
        }
    }

    private void NativeStatusChanged(object? sender, RecordingStatusEventArgs args)
    {
        if (args.Status == RecorderStatus.Recording)
        {
            _elapsed.Start();
            SetState(RecordingState.Recording);
            _started?.TrySetResult(true);
        }
        else if (args.Status == RecorderStatus.Finishing)
        {
            _elapsed.Stop();
            SetState(RecordingState.Finishing);
        }
    }

    private void NativeCompleted(object? sender, RecordingCompleteEventArgs args)
        => _nativeCompletion?.TrySetResult(args.FilePath);

    private void NativeFailed(object? sender, RecordingFailedEventArgs args)
    {
        var exception = new InvalidOperationException(T("錄影失敗，請檢查顯示卡、音訊裝置及儲存位置。") + " " + args.Error);
        _started?.TrySetException(exception);
        _nativeCompletion?.TrySetException(exception);
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

    private string DescribeError(Exception exception)
    {
        if (exception is DllNotFoundException or BadImageFormatException or FileLoadException)
            return T("錄影元件無法載入。請安裝 Visual C++ 2015–2022 x64 執行階段並重新開啟 SnipFlow。") + " " + exception.Message;
        return exception.Message;
    }

    private string T(string text) => _translate(text);
}

using System.Diagnostics;
using System.Globalization;
using SnipFlow.Recording;

namespace SnipFlow.Platform.Linux.Recording;

/// <summary>
/// Records a desktop rectangle with ffmpeg into an MP4: H.264 (libx264, veryfast, CRF 20, yuv420p) at a constant
/// 30 fps, AAC 192 kb/s, fast start. X11 sessions grab the root window (<c>x11grab</c>); Wayland sessions write
/// raw BGRA frames from a ScreenCast stream to ffmpeg's standard input (<see cref="WaylandFrameSource"/>), so the
/// encoding and audio are identical. Sound comes from PulseAudio / PipeWire-pulse (<see cref="PulseAudioInputs"/>).
/// </summary>
/// <remarks>
/// The audio inputs are opened first and synchronized to the video input with <c>-isync</c>: every input uses
/// the wall clock (x11grab and pulse natively, the frame pipe through <c>-use_wallclock_as_timestamps</c>), so
/// the streams stay aligned even though ffmpeg opens its inputs one after another. A stop writes <c>q</c> to
/// ffmpeg (X11) or ends the frame pipe (Wayland, with <c>-shortest</c>) and waits for the finished file; the
/// process is killed only when <see cref="Dispose"/> runs before it exits (start failure or finalize timeout).
/// </remarks>
internal sealed class FfmpegScreenRecorder : ScreenRecorderBackend
{
    private const int FrameRate = 30;
    private const int ErrorTailLines = 12;
    private readonly string _ffmpeg;
    private readonly PixelRect _target;
    private readonly PixelSize _frameSize;
    private readonly RecordingOptions _options;
    private readonly PulseAudioInputs _audio;
    private readonly WaylandFrameSource? _wayland;
    private readonly string? _display;
    private readonly Func<string, string> _translate;
    private readonly Queue<string> _errorTail = new();
    private Process? _process;
    private Thread? _pump;
    private Task? _outputReader;
    private Task? _errorReader;
    private Task? _exitWatcher;
    private TextWriter? _diagnostics;
    private string? _path;
    private volatile bool _stopRequested;
    private int _recordingRaised;
    private int _finished;

    private FfmpegScreenRecorder(string ffmpeg, PixelRect target, PixelSize frameSize, RecordingOptions options,
        PulseAudioInputs audio, WaylandFrameSource? wayland, string? display, Func<string, string> translate)
    {
        _ffmpeg = ffmpeg;
        _target = target;
        _frameSize = frameSize;
        _options = options;
        _audio = audio;
        _wayland = wayland;
        _display = display;
        _translate = translate;
        if (wayland is not null)
            wayland.Ended += (_, _) => SourceEnded();
    }

    /// <summary>
    /// Checks the tools and audio devices and, on Wayland, starts the screen share. Nothing is recorded until
    /// <see cref="Record"/>.
    /// </summary>
    internal static async Task<FfmpegScreenRecorder> CreateAsync(PixelRect target, RecordingOptions options,
        Func<string, string> translate, CancellationToken cancellationToken)
    {
        var ffmpeg = ExternalTool.Find("ffmpeg")
            ?? throw new InvalidOperationException(translate("螢幕錄影需要 ffmpeg。請安裝：sudo apt install ffmpeg"));
        var audio = await PulseAudioInputs.ResolveAsync(options.SystemAudio, options.Microphone, translate, cancellationToken)
            .ConfigureAwait(false);
        if (PlatformInfo.IsWayland)
        {
            var source = await WaylandFrameSource.StartAsync(target, options.CaptureCursor, translate, cancellationToken)
                .ConfigureAwait(false);
            return new FfmpegScreenRecorder(ffmpeg, target, source.FrameSize, options, audio, source, null, translate)
            {
                Notice = audio.Notice
            };
        }
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrEmpty(display))
            throw new InvalidOperationException(translate("無法連線到 X 顯示伺服器（DISPLAY），無法錄影。"));
        return new FfmpegScreenRecorder(ffmpeg, target, target.Size, options, audio, null, display, translate)
        {
            Notice = audio.Notice
        };
    }

    internal override TimeSpan? FinalizeTimeout
    {
        get
        {
            // Fast start rewrites the whole file once to move the MP4 index to the front; allow for that on slow disks.
            long length = 0;
            try { if (_path is not null && File.Exists(_path)) length = new FileInfo(_path).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return TimeSpan.FromSeconds(20 + length / (20d * 1024 * 1024));
        }
    }

    internal override void Record(string path)
    {
        _path = path;
        if (!string.IsNullOrWhiteSpace(_options.DiagnosticLogPath))
        {
            try { _diagnostics = TextWriter.Synchronized(new StreamWriter(_options.DiagnosticLogPath, append: true) { AutoFlush = true }); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { SettingsStore.Log(exception); }
        }
        var arguments = BuildArguments(path);
        _diagnostics?.WriteLine($"{DateTimeOffset.Now:O} {_ffmpeg} {string.Join(' ', arguments)}");
        var info = ExternalTool.CreateStartInfo(_ffmpeg, arguments, redirectInput: true, redirectOutput: true, redirectError: true);
        info.Environment["LC_ALL"] = "C";
        var process = new Process { StartInfo = info };
        // ffmpeg gets its three pipes only, never descriptors such as the screen cast's PipeWire connection.
        InheritedDescriptors.MarkCloseOnExec();
        process.Start();
        _process = process;
        _outputReader = Task.Run(() => ReadProgressAsync(process));
        _errorReader = Task.Run(() => ReadErrorsAsync(process));
        _exitWatcher = Task.Run(() => WatchExitAsync(process));
        if (_wayland is not null)
        {
            _pump = new Thread(() => PumpFrames(process)) { IsBackground = true, Name = "SnipFlow recording frames" };
            _pump.Start();
        }
    }

    internal override void Stop()
    {
        if (_stopRequested)
            return;
        _stopRequested = true;
        OnFinishing();
        var process = _process;
        if (process is null || _wayland is not null)
            return; // Wayland: the frame pump sees the request and ends ffmpeg's input.
        // X11: ffmpeg's interactive "q" stops reading the inputs, flushes the encoders and writes the MP4 index.
        _ = Task.Run(() =>
        {
            try
            {
                process.StandardInput.Write('q');
                process.StandardInput.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }

    internal override void Dispose()
    {
        _stopRequested = true;
        var process = _process;
        if (process is not null)
        {
            try
            {
                // Reached before ffmpeg exited only when starting failed or finalizing timed out.
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { process.WaitForExit(5000); }
            catch (InvalidOperationException) { }
        }
        _pump?.Join(TimeSpan.FromSeconds(5));
        try { _exitWatcher?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        process?.Dispose();
        _wayland?.Dispose();
        _diagnostics?.Dispose();
        _diagnostics = null;
    }

    internal IReadOnlyList<string> BuildArguments(string path)
    {
        var text = CultureInfo.InvariantCulture;
        var arguments = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "warning", "-progress", "pipe:1", "-stats_period", "0.25"
        };
        var audioSources = new[] { _audio.SystemSource, _audio.MicrophoneSource }.OfType<string>().ToList();
        var videoInput = audioSources.Count;
        foreach (var source in audioSources)
            arguments.AddRange(new[]
            {
                "-thread_queue_size", "1024", "-f", "pulse", "-fragment_size", "3840", "-sample_rate", "48000",
                "-channels", "2", "-isync", videoInput.ToString(text), "-i", source
            });
        // Bound ffmpeg's frame queue to about 256 MB of raw frames.
        var frameBytes = Math.Max(1L, (long)_frameSize.Width * _frameSize.Height * 4);
        var queue = Math.Clamp(256L * 1024 * 1024 / frameBytes, 4, 64).ToString(text);
        var size = $"{_frameSize.Width.ToString(text)}x{_frameSize.Height.ToString(text)}";
        if (_wayland is null)
            arguments.AddRange(new[]
            {
                "-thread_queue_size", queue, "-f", "x11grab", "-framerate", FrameRate.ToString(text), "-video_size", size,
                "-draw_mouse", _options.CaptureCursor ? "1" : "0",
                "-i", $"{_display}+{_target.X.ToString(text)},{_target.Y.ToString(text)}"
            });
        else
            arguments.AddRange(new[]
            {
                "-thread_queue_size", queue, "-use_wallclock_as_timestamps", "1", "-f", "rawvideo", "-pix_fmt", "bgra",
                "-s", size, "-framerate", FrameRate.ToString(text), "-i", "pipe:0"
            });
        arguments.AddRange(new[] { "-map", $"{videoInput.ToString(text)}:v:0" });
        if (audioSources.Count == 1)
            arguments.AddRange(new[] { "-filter_complex", "[0:a]aresample=async=1:first_pts=0[a]", "-map", "[a]" });
        else if (audioSources.Count == 2)
            arguments.AddRange(new[]
            {
                "-filter_complex",
                "[0:a]aresample=async=1:first_pts=0[s];[1:a]aresample=async=1:first_pts=0[m];"
                    + "[s][m]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[a]",
                "-map", "[a]"
            });
        arguments.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-r", FrameRate.ToString(text), "-fps_mode", "cfr"
        });
        if (audioSources.Count > 0)
            arguments.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        if (_wayland is not null)
            arguments.Add("-shortest"); // The end of the frame pipe also ends the live audio inputs.
        arguments.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", "-y", path });
        return arguments;
    }

    private async Task ReadProgressAsync(Process process)
    {
        long frames = 0;
        long outputMicroseconds = 0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("frame=", StringComparison.Ordinal))
                    _ = long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out frames);
                else if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                         && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    outputMicroseconds = Math.Max(0, value);
                else if (line.StartsWith("progress=", StringComparison.Ordinal) && frames > 0
                         && Interlocked.Exchange(ref _recordingRaised, 1) == 0)
                {
                    // Encoded frames reach the file a little after they were captured; count that time too.
                    OnRecording(TimeSpan.FromTicks(outputMicroseconds * 10));
                    if (_wayland?.HasEnded == true)
                        SourceEnded();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;
                lock (_errorTail)
                {
                    _errorTail.Enqueue(line.Trim());
                    while (_errorTail.Count > ErrorTailLines)
                        _errorTail.Dequeue();
                }
                try { _diagnostics?.WriteLine(line); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task WatchExitAsync(Process process)
    {
        int exitCode;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            // Let the readers collect the final error lines before describing a failure.
            await Task.WhenAll(_outputReader ?? Task.CompletedTask, _errorReader ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (InvalidOperationException) { }
        try { exitCode = process.ExitCode; }
        catch (InvalidOperationException) { exitCode = -1; }
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;
        _diagnostics?.WriteLine($"{DateTimeOffset.Now:O} ffmpeg exited with {exitCode.ToString(CultureInfo.InvariantCulture)}");
        if (exitCode == 0 && _path is not null && File.Exists(_path))
        {
            OnCompleted(_path);
            return;
        }
        OnFailed(new InvalidOperationException(DescribeFailure(exitCode)));
    }

    private string DescribeFailure(int exitCode)
    {
        string[] tail;
        lock (_errorTail) tail = _errorTail.ToArray();
        // The last lines say why; earlier ones are usually context such as queue warnings.
        var detail = string.Join(" ", tail.TakeLast(4));
        if (detail.Length > 600)
            detail = "…" + detail[^600..];
        if (detail.Contains("Unknown input format: 'pulse'", StringComparison.Ordinal))
            return _translate("這個 ffmpeg 不支援 PulseAudio 錄音。請安裝 Ubuntu 的 ffmpeg 套件，或關閉系統聲音與麥克風。");
        if (detail.Contains("Unknown input format: 'x11grab'", StringComparison.Ordinal))
            return _translate("這個 ffmpeg 不支援 X11 螢幕錄影（x11grab）。請安裝 Ubuntu 的 ffmpeg 套件：sudo apt install ffmpeg");
        if (detail.Contains("Unknown encoder 'libx264'", StringComparison.Ordinal))
            return _translate("這個 ffmpeg 不支援 H.264（libx264）編碼。請安裝 Ubuntu 的 ffmpeg 套件：sudo apt install ffmpeg");
        if (detail.Length == 0)
            detail = _translate("ffmpeg 意外結束。") + $" ({exitCode.ToString(CultureInfo.InvariantCulture)})";
        return _translate("ffmpeg 無法完成錄影：") + detail;
    }

    /// <summary>
    /// Wayland: writes the newest frame to ffmpeg 30 times a second, repeating the previous one while the screen
    /// does not change (the compositor only sends damaged frames). ffmpeg stamps each frame when it reads it, so a
    /// slow encoder drops frames instead of drifting from the audio. Ends ffmpeg's input when a stop is requested.
    /// </summary>
    private void PumpFrames(Process process)
    {
        var source = _wayland!;
        var frame = source.CreateFrameBuffer();
        source.CopyLatest(frame);
        var interval = TimeSpan.TicksPerSecond / FrameRate;
        var clock = Stopwatch.StartNew();
        long next = 0;
        try
        {
            var input = process.StandardInput.BaseStream;
            while (!_stopRequested)
            {
                source.CopyLatest(frame);
                input.Write(frame, 0, frame.Length);
                next += interval;
                var wait = next - clock.Elapsed.Ticks;
                if (wait > 0)
                    Thread.Sleep(TimeSpan.FromTicks(wait));
                else if (wait < -TimeSpan.TicksPerSecond / 4)
                    next = clock.Elapsed.Ticks; // Fell behind (busy encoder): skip frames rather than send a burst.
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // ffmpeg stopped reading; its exit code and error output describe why.
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private void SourceEnded()
    {
        if (Volatile.Read(ref _recordingRaised) == 0 || _stopRequested)
            return; // Reported again once recording starts; ignored while stopping.
        OnInterrupted(_translate("螢幕分享已結束，已停止錄影；可儲存已錄影片。"));
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using ScreenRecorderLib;
using SnipFlow.Capture;

namespace SnipFlow.Recording;

/// <summary>ScreenRecorderLib (Windows.Graphics.Capture + Media Foundation) recorder for Windows 10 2004 and later.</summary>
public sealed partial class ScreenRecordingService
{
    private partial void EnsurePlatformSupported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException(T("螢幕錄影需要 Windows 10 2004 或更新版本。"));
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(T("請使用 SnipFlow 的 Windows x64 版本錄影。"));
        if (!global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException(T("目前 Windows 工作階段不支援螢幕錄影。請確認顯示卡驅動程式及遠端桌面設定。"));
    }

    private partial async Task<ScreenRecorderBackend> CreateRecorderAsync(PixelRect target, RecordingOptions options,
        CancellationToken cancellationToken)
    {
        var recorderOptions = CreateRecorderOptions(target, options);
        var recorder = await Task.Run(() => Recorder.CreateRecorder(recorderOptions), cancellationToken).ConfigureAwait(false);
        return new NativeScreenRecorder(recorder, _translate);
    }

    private partial string? DescribePlatformError(Exception exception)
    {
        if (exception is DllNotFoundException or BadImageFormatException or FileLoadException)
            return T("錄影元件無法載入。請安裝 Visual C++ 2015–2022 x64 執行階段並重新開啟 SnipFlow。") + " " + exception.Message;
        return null;
    }

    private RecorderOptions CreateRecorderOptions(PixelRect target, RecordingOptions options)
    {
        var sources = new List<RecordingSourceBase>();
        foreach (var screen in DisplayMonitor.GetAll())
        {
            var intersection = CaptureGeometry.Intersect(target, screen.Bounds);
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

    /// <summary>Adapts ScreenRecorderLib's native callbacks to <see cref="ScreenRecorderBackend"/>.</summary>
    private sealed class NativeScreenRecorder : ScreenRecorderBackend
    {
        private readonly Recorder _recorder;
        private readonly Func<string, string> _translate;

        internal NativeScreenRecorder(Recorder recorder, Func<string, string> translate)
        {
            _recorder = recorder;
            _translate = translate;
            recorder.OnStatusChanged += NativeStatusChanged;
            recorder.OnRecordingComplete += NativeCompleted;
            recorder.OnRecordingFailed += NativeFailed;
        }

        internal override void Record(string path) => _recorder.Record(path);

        internal override void Stop() => _recorder.Stop();

        internal override void Dispose()
        {
            _recorder.OnStatusChanged -= NativeStatusChanged;
            _recorder.OnRecordingComplete -= NativeCompleted;
            _recorder.OnRecordingFailed -= NativeFailed;
            _recorder.Dispose();
        }

        private void NativeStatusChanged(object? sender, RecordingStatusEventArgs args)
        {
            if (args.Status == RecorderStatus.Recording)
                OnRecording(TimeSpan.Zero);
            else if (args.Status == RecorderStatus.Finishing)
                OnFinishing();
        }

        private void NativeCompleted(object? sender, RecordingCompleteEventArgs args) => OnCompleted(args.FilePath);

        private void NativeFailed(object? sender, RecordingFailedEventArgs args) =>
            OnFailed(new InvalidOperationException(_translate("錄影失敗，請檢查顯示卡、音訊裝置及儲存位置。") + " " + args.Error));
    }

    /// <summary>A monitor's GDI device name (<c>\\.\DISPLAY1</c>) and physical bounds, as WinForms' Screen reported them.</summary>
    private sealed record DisplayMonitor(string DeviceName, PixelRect Bounds)
    {
        internal static IReadOnlyList<DisplayMonitor> GetAll()
        {
            var monitors = new List<DisplayMonitor>();
            var error = 0;
            MonitorEnumeration callback = (IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data) =>
            {
                var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
                if (!GetMonitorInfo(monitor, ref information))
                {
                    error = Marshal.GetLastWin32Error();
                    if (error == 0) error = 1;
                    return false;
                }
                var bounds = CaptureGeometry.FromLTRB(information.Left, information.Top, information.Right, information.Bottom);
                if (bounds.Width > 0 && bounds.Height > 0)
                    monitors.Add(new DisplayMonitor(information.DeviceName, bounds));
                return true;
            };
            var succeeded = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (error != 0)
                throw new Win32Exception(error);
            if (!succeeded)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return monitors;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool MonitorEnumeration(IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data);

        // MONITORINFOEXW: cbSize, rcMonitor, rcWork, dwFlags, szDevice[32].
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInformation
        {
            public int Size;
            public int Left, Top, Right, Bottom;
            public int WorkLeft, WorkTop, WorkRight, WorkBottom;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumeration callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation information);
    }
}

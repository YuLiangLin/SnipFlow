namespace SnipFlow.Recording;

public sealed record RecordingOptions
{
    public bool SystemAudio { get; init; } = true;
    public bool Microphone { get; init; }
    public bool CaptureCursor { get; init; } = true;
    public bool HardwareEncoding { get; init; } = true;
    public bool OverwriteExisting { get; init; }
    public string? DiagnosticLogPath { get; init; }
}

public enum RecordingState
{
    Ready,
    Starting,
    Recording,
    Finishing,
    Completed,
    Cancelled,
    Failed
}

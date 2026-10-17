using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Capture;

/// <summary>Identity and physical screen geometry of a user-selected top-level window.</summary>
public sealed record WindowCaptureTarget(IntPtr Handle, uint ProcessId,
    DrawingRectangle WindowBounds, DrawingRectangle ClientBounds, uint Dpi = 0);

using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Capture;

public sealed record CaptureResult(BitmapSource Image, DrawingRectangle Bounds);

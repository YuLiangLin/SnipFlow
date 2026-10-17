namespace SnipFlow.Scroll;

public sealed partial class ScrollCaptureWindow
{
    // GDI reads the desktop on every capture; the lease never stops.
    private static partial bool IsLeaseRunning(IDisposable lease) => true;
}

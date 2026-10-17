using SnipFlow.Platform.Linux.Wayland;

namespace SnipFlow.Scroll;

public sealed partial class ScrollCaptureWindow
{
    // The Wayland screen stream ends when the user stops sharing (for example from the desktop's
    // sharing indicator) or the compositor closes the session; X11 leases never stop.
    private static partial bool IsLeaseRunning(IDisposable lease) => lease is not WaylandScreenStream stream || stream.IsRunning;
}

namespace SnipFlow.Capture;

/// <summary>
/// A captured image and the desktop rectangle it shows. <see cref="Bounds"/> uses physical desktop
/// pixels, the coordinate space of <see cref="Window.Position"/> and <see cref="Screens"/>, including
/// negative coordinates. The image normally has exactly <c>Bounds.Size</c> pixels; in a Wayland session,
/// where XWayland can report scaled (logical) coordinates, it keeps the compositor's full resolution
/// and may be larger by the screen scale.
/// </summary>
public sealed record CaptureResult(Bitmap Image, PixelRect Bounds)
{
    public WindowCaptureTarget? Target { get; init; }
}

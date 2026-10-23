using SnipFlow.Capture;
using SnipFlow.Services;

namespace SnipFlow.Scroll;

/// <summary>Reads target identity and visibility. It never sends input or changes target focus.</summary>
/// <remarks>
/// Window identity comes from <see cref="CaptureNative"/> (Win32 on Windows, EWMH/Xlib on X11). In a
/// Wayland session other applications' windows cannot be identified, so the target degrades to
/// region-only observation (<see cref="IsRegionOnly"/>): no foreground, geometry or occlusion checks,
/// and the frame comparison alone decides what is collected.
/// </remarks>
internal sealed class ScrollCaptureTarget
{
    private readonly PixelRect region;
    private nint window;
    private uint processId;
    private PixelRect originalBounds;
    private readonly PixelRect originalClientBounds;
    private readonly uint originalDpi;
    private readonly bool hasSelectedTarget;
    private bool resolved;

    /// <summary>
    /// True when no window identity is available: always in Wayland sessions, and on X11 when the
    /// region does not lie over any window. Only the captured pixels guard the observation then.
    /// </summary>
    internal bool IsRegionOnly { get; private set; }

    internal ScrollCaptureTarget(PixelRect region, WindowCaptureTarget? selectedTarget = null)
    {
        this.region = region;
        if (selectedTarget is not null)
        {
            // X11 clients that do not set _NET_WM_PID report process 0; Windows always knows the owner.
            if (selectedTarget.Handle == 0 || (selectedTarget.ProcessId == 0 && !PlatformInfo.IsLinux)
                || selectedTarget.ProcessId == Environment.ProcessId
                || region.Width <= 0 || region.Height <= 0
                || !CaptureGeometry.Contains(selectedTarget.WindowBounds, region)
                || !CaptureGeometry.Contains(selectedTarget.ClientBounds, region))
                throw new ArgumentException(I18n.T("框選範圍必須位於指定視窗的內容區域內。"), nameof(selectedTarget));

            // Bind the identity and physical geometry selected by the two-step capture.
            // A stale target can still retain its validated initial frame; CanObserve
            // rejects future captures instead of guessing another window at these pixels.
            window = selectedTarget.Handle;
            processId = selectedTarget.ProcessId;
            originalBounds = selectedTarget.WindowBounds;
            originalClientBounds = selectedTarget.ClientBounds;
            originalDpi = selectedTarget.Dpi;
            hasSelectedTarget = true;
            resolved = true;
            return;
        }

        // Compatibility for callers that still select only a desktop region. Windows destroys the
        // selection overlays synchronously; on X11 they may still be mapped for a moment, so the
        // window under the region is looked up on the first observation instead (see CanObserve).
        IsRegionOnly = PlatformInfo.IsWayland;
        if (PlatformInfo.IsWindows) ResolveRegionWindow();
    }

    private void ResolveRegionWindow()
    {
        resolved = true;
        window = CaptureNative.GetWindowAt(new PixelPoint(region.X + region.Width / 2, region.Y + region.Height / 2));
        if (window != 0)
        {
            processId = CaptureNative.GetWindowProcessId(window);
            if (CaptureNative.GetWindowBounds(window) is { } bounds) originalBounds = bounds;
            // One of SnipFlow's own windows (a closing selection overlay) is not the target: look again
            // on the next observation. Windows resolves once, as before.
            if (PlatformInfo.IsLinux && processId == Environment.ProcessId) resolved = false;
        }
        // Wayland never reports other windows; an X11 region can also lie over the bare desktop.
        IsRegionOnly = window == 0 && PlatformInfo.IsLinux;
    }

    internal bool CanObserve(nint toolbar, bool toolbarExcluded, out string reason, bool allowToolbarForeground = false)
    {
        reason = "";
        if (!resolved) ResolveRegionWindow();
        if (IsRegionOnly) return true;
        if (window == 0 || (processId == 0 && !PlatformInfo.IsLinux) || processId == Environment.ProcessId
            || !CaptureNative.IsWindowAlive(window))
        { reason = "目標視窗目前無法擷取，已保留現有內容。"; return false; }
        if (CaptureNative.GetWindowProcessId(window) != processId || CaptureNative.GetWindowBounds(window) is not { } rectangle
            || rectangle != originalBounds || !CaptureGeometry.Contains(originalBounds, region))
        { reason = "目標視窗位置或大小已改變，請完成後重新選取範圍。"; return false; }
        if (hasSelectedTarget && (CaptureNative.GetClientBounds(window) is not { } clientBounds
            || clientBounds != originalClientBounds || !CaptureGeometry.Contains(clientBounds, region)))
        { reason = "目標視窗的內容範圍已改變，請完成後重新選取範圍。"; return false; }
        if (originalDpi != 0 && CaptureNative.GetWindowDpi(window) != originalDpi)
        { reason = "目標視窗的縮放比例已改變，請完成後重新選取範圍。"; return false; }
        if (CaptureNative.IsCaptureProtected(window))
        { reason = "目標視窗已限制螢幕擷取，已保留現有內容。"; return false; }
        var foreground = CaptureNative.GetForegroundWindow();
        if (foreground != window && !(allowToolbarForeground && foreground == toolbar))
        { reason = "切回目標視窗即可繼續；已擷取內容會保留。"; return false; }
        if (CaptureNative.IsPointerButtonDown())
        { reason = "等待拖曳或選取結束…"; return false; }

        // Inspect windows above the target, including small popups that a point grid can miss.
        // The platform walk is bounded and returns null while the desktop changes during enumeration.
        // A toolbar that cannot be excluded from capture (always on Linux) counts as covering.
        var above = CaptureNative.GetWindowsAbove(window, toolbarExcluded && toolbar != 0 ? new[] { toolbar } : null);
        if (above is null)
        { reason = "視窗狀態正在變更，暫停擷取。"; return false; }
        foreach (var covering in above)
        {
            if (!CaptureGeometry.IntersectsWith(covering.Bounds, region)) continue;
            if (covering.Handle != toolbar || !toolbarExcluded)
            {
                reason = covering.Handle == toolbar ? "請把操作條移到選取範圍外，避免出現在截圖中。"
                    : "選取範圍被其他視窗遮住，已暫停擷取。";
                return false;
            }
        }
        return true;
    }

    internal static bool ExcludeToolbar(nint toolbar) => toolbar != 0 && CaptureNative.TryExcludeFromCapture(toolbar);

    /// <summary>The toolbar's outer size in physical pixels, the unit of <see cref="Window.Position"/>.</summary>
    internal static PixelSize ToolbarSize(Window toolbar)
    {
        var scaling = toolbar.RenderScaling > 0 ? toolbar.RenderScaling : 1;
        var size = toolbar.FrameSize ?? toolbar.ClientSize;
        return new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scaling)), Math.Max(1, (int)Math.Ceiling(size.Height * scaling)));
    }

    /// <summary>
    /// The nearest location beside the region (to its top-right first) where a toolbar of
    /// <paramref name="size"/> fits on a screen's working area without covering the region, or null.
    /// </summary>
    internal static PixelRect? FindToolbarLocation(IReadOnlyList<Avalonia.Platform.Screen> screens, PixelRect region, PixelSize size)
    {
        var width = size.Width;
        var height = size.Height;
        var locations = new List<PixelRect>();
        foreach (var screen in screens)
        {
            var area = screen.WorkingArea;
            if (width > area.Width || height > area.Height) continue;
            var xs = new[] { region.Right + 12, region.X - width - 12, area.X + 12, area.Right - width - 12 };
            var ys = new[] { region.Y, region.Bottom + 12, region.Y - height - 12, area.Y + 12, area.Bottom - height - 12 };
            foreach (var x in xs)
                foreach (var y in ys)
                {
                    var candidate = new PixelRect(Math.Clamp(x, area.X, area.Right - width),
                        Math.Clamp(y, area.Y, area.Bottom - height), width, height);
                    if (!CaptureGeometry.IntersectsWith(candidate, region)) locations.Add(candidate);
                }
        }
        if (locations.Count == 0) return null;
        return locations.OrderBy(item => Math.Abs((long)item.X - region.Right)
            + Math.Abs((long)item.Y - region.Y)).First();
    }

    /// <summary>
    /// Moves the toolbar beside the region. Returns false when no screen has room outside the region;
    /// the toolbar then goes to the top-right corner of the region's screen (where a capture-excluded
    /// Windows toolbar may stay over the region).
    /// </summary>
    internal static bool PlaceToolbar(Window toolbar, PixelRect region)
    {
        var size = ToolbarSize(toolbar);
        var screens = toolbar.Screens.All;
        if (FindToolbarLocation(screens, region, size) is { } location)
        {
            Move(toolbar, location.Position);
            return true;
        }
        var fallback = (toolbar.Screens.ScreenFromBounds(region) ?? toolbar.Screens.Primary ?? screens.FirstOrDefault())?.WorkingArea
            ?? region;
        Move(toolbar, new PixelPoint(fallback.Right - size.Width - 12, fallback.Y + 12));
        return false;
    }

    internal static void KeepToolbarVisible(Window toolbar)
    {
        var screen = toolbar.Screens.ScreenFromWindow(toolbar) ?? toolbar.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var size = ToolbarSize(toolbar);
        var position = toolbar.Position;
        var left = Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - size.Width));
        var top = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        if (left == position.X && top == position.Y) return;
        // Growing the preview or expanding controls moves only this toolbar, without activation.
        Move(toolbar, new PixelPoint(left, top));
    }

    /// <summary>The toolbar's current physical rectangle.</summary>
    internal static PixelRect ToolbarBounds(Window toolbar) => new(toolbar.Position, ToolbarSize(toolbar));

    private static void Move(Window toolbar, PixelPoint position)
    {
        if (toolbar.Position != position) toolbar.Position = position;
    }
}

using System.Runtime.InteropServices;
using SnipFlow.Capture;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Scroll;

/// <summary>Reads target identity and visibility. It never sends input or changes target focus.</summary>
internal sealed class ScrollCaptureTarget
{
    private readonly DrawingRectangle region;
    private readonly IntPtr window;
    private readonly uint processId;
    private readonly DrawingRectangle originalBounds;
    private readonly DrawingRectangle originalClientBounds;
    private readonly uint originalDpi;
    private readonly bool hasSelectedTarget;

    internal ScrollCaptureTarget(DrawingRectangle region, WindowCaptureTarget? selectedTarget = null)
    {
        this.region = region;
        if (selectedTarget is not null)
        {
            if (selectedTarget.Handle == IntPtr.Zero || selectedTarget.ProcessId == 0
                || selectedTarget.ProcessId == Environment.ProcessId
                || region.Width <= 0 || region.Height <= 0
                || !selectedTarget.WindowBounds.Contains(region) || !selectedTarget.ClientBounds.Contains(region))
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
            return;
        }

        // Compatibility for callers that still select only a desktop region.
        var point = new NativePoint { X = region.Left + region.Width / 2, Y = region.Top + region.Height / 2 };
        window = GetAncestor(WindowFromPoint(point), 2); // GA_ROOT.
        if (window != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out processId);
            if (GetWindowRect(window, out var rectangle)) originalBounds = rectangle.ToRectangle();
        }
    }

    internal bool CanObserve(IntPtr toolbar, bool toolbarExcluded, out string reason, bool allowToolbarForeground = false)
    {
        reason = "";
        if (window == IntPtr.Zero || processId == 0 || processId == Environment.ProcessId
            || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window) || IsCloaked(window))
        { reason = "目標視窗目前無法擷取，已保留現有內容。"; return false; }
        GetWindowThreadProcessId(window, out var currentProcess);
        if (currentProcess != processId || !GetWindowRect(window, out var rectangle)
            || rectangle.ToRectangle() != originalBounds || !originalBounds.Contains(region))
        { reason = "目標視窗位置或大小已改變，請完成後重新選取範圍。"; return false; }
        if (hasSelectedTarget && (!TryGetClientBounds(window, out var clientBounds)
            || clientBounds != originalClientBounds || !clientBounds.Contains(region)))
        { reason = "目標視窗的內容範圍已改變，請完成後重新選取範圍。"; return false; }
        if (originalDpi != 0 && GetDpiForWindow(window) != originalDpi)
        { reason = "目標視窗的縮放比例已改變，請完成後重新選取範圍。"; return false; }
        if (GetWindowDisplayAffinity(window, out var targetAffinity) && targetAffinity != 0)
        { reason = "目標視窗已限制螢幕擷取，已保留現有內容。"; return false; }
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        if (foreground != window && !(allowToolbarForeground && foreground == toolbar))
        { reason = "切回目標視窗即可繼續；已擷取內容會保留。"; return false; }
        if (IsPointerDown())
        { reason = "等待拖曳或選取結束…"; return false; }

        // Inspect windows above the target, including small popups that a point grid can miss.
        // Bound the walk and remember handles because the desktop can change during enumeration.
        var seen = new HashSet<IntPtr> { window };
        var above = GetWindow(window, 3); // GW_HWNDPREV.
        for (var count = 0; above != IntPtr.Zero; count++)
        {
            if (count >= 4096 || !seen.Add(above))
            { reason = "視窗狀態正在變更，暫停擷取。"; return false; }
            if (IsWindowVisible(above) && !IsIconic(above) && !IsCloaked(above)
                && GetWindowRect(above, out var covering) && covering.ToRectangle().IntersectsWith(region))
            {
                if (above != toolbar || !toolbarExcluded)
                {
                    reason = above == toolbar ? "請把操作條移到選取範圍外，避免出現在截圖中。"
                        : "選取範圍被其他視窗遮住，已暫停擷取。";
                    return false;
                }
            }
            above = GetWindow(above, 3);
        }
        return true;
    }

    internal static bool ExcludeToolbar(IntPtr toolbar) => SetWindowDisplayAffinity(toolbar, 0x11)
        && GetWindowDisplayAffinity(toolbar, out var affinity) && affinity == 0x11;

    internal static void PlaceToolbar(IntPtr toolbar, DrawingRectangle region)
    {
        if (!GetWindowRect(toolbar, out var bounds)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var locations = new List<DrawingRectangle>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            if (width > area.Width || height > area.Height) continue;
            var xs = new[] { region.Right + 12, region.Left - width - 12, area.Left + 12, area.Right - width - 12 };
            var ys = new[] { region.Top, region.Bottom + 12, region.Top - height - 12, area.Top + 12, area.Bottom - height - 12 };
            foreach (var x in xs)
                foreach (var y in ys)
                {
                    var candidate = new DrawingRectangle(Math.Clamp(x, area.Left, area.Right - width),
                        Math.Clamp(y, area.Top, area.Bottom - height), width, height);
                    if (!candidate.IntersectsWith(region)) locations.Add(candidate);
                }
        }
        var fallback = System.Windows.Forms.Screen.FromRectangle(region).WorkingArea;
        var location = locations.OrderBy(item => Math.Abs((long)item.Left - region.Right)
                + Math.Abs((long)item.Top - region.Top)).FirstOrDefault();
        if (location.IsEmpty)
            location = new DrawingRectangle(fallback.Right - width - 12, fallback.Top + 12, width, height);
        SetWindowPos(toolbar, IntPtr.Zero, location.Left, location.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    internal static void KeepToolbarVisible(IntPtr toolbar)
    {
        if (!GetWindowRect(toolbar, out var bounds)) return;
        var area = System.Windows.Forms.Screen.FromHandle(toolbar).WorkingArea;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var left = Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - width));
        var top = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        if (left == bounds.Left && top == bounds.Top) return;
        // Growing the preview or expanding controls moves only this toolbar, without activation.
        SetWindowPos(toolbar, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    private static bool IsPointerDown() => (GetAsyncKeyState(1) & 0x8000) != 0
        || (GetAsyncKeyState(2) & 0x8000) != 0 || (GetAsyncKeyState(4) & 0x8000) != 0;
    private static bool IsCloaked(IntPtr handle) => DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool TryGetClientBounds(IntPtr handle, out DrawingRectangle bounds)
    {
        bounds = default;
        if (!GetClientRect(handle, out var rectangle)) return false;
        var first = new NativePoint { X = rectangle.Left, Y = rectangle.Top };
        var last = new NativePoint { X = rectangle.Right, Y = rectangle.Bottom };
        if (!ClientToScreen(handle, ref first) || !ClientToScreen(handle, ref last)) return false;
        bounds = DrawingRectangle.FromLTRB(first.X, first.Y, last.X, last.Y);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left; public int Top; public int Right; public int Bottom;
        public readonly DrawingRectangle ToRectangle() => DrawingRectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRectangle rectangle);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr handle, out NativeRectangle rectangle);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(IntPtr handle, uint affinity);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(IntPtr handle, out uint affinity);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out int value, int size);
}

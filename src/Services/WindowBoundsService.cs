namespace SnipFlow.Services;

/// <summary>Work-area helpers for SnipFlow's frameless windows.</summary>
public static partial class WindowBoundsService
{
    /// <summary>
    /// Keeps a maximized frameless window inside the monitor work area. Windows needs a
    /// WM_GETMINMAXINFO hook; X11 window managers already maximize to the work area.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        AttachNative(window);
    }

    static partial void AttachNative(Window window);

    /// <summary>The working area (excluding panels and taskbars) of the window's screen, in DIPs.</summary>
    public static Rect GetWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null) return new Rect(0, 0, 1280, 800);
        var area = screen.WorkingArea;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        return new Rect(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale);
    }

    /// <summary>The working area in physical pixels, the unit of <see cref="Window.Position"/>.</summary>
    public static PixelRect GetWorkAreaPixels(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
    }

    /// <summary>The scaling of the window's screen (1.0 = 96 DPI).</summary>
    public static double GetScaling(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        return screen?.Scaling is > 0 and var scaling ? scaling : window.RenderScaling;
    }
}

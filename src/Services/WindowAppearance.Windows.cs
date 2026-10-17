using System.Runtime.InteropServices;

namespace SnipFlow.Services;

public static partial class WindowAppearance
{
    static partial void ApplyNative(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        Win32Properties.SetWindowCornerPreference(window, Win32Properties.WindowCornerPreference.Round);

        // Windows 10 ignores unsupported attributes; custom chrome remains usable.
        var enabled = ThemeService.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) < 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        var border = ColorRef("Line");
        var caption = ColorRef("Panel");
        var text = ColorRef("Ink");
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    // DWM takes COLORREF (0x00BBGGRR).
    static int ColorRef(string key)
    {
        var color = ThemeService.GetColor(key);
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SnipFlow.Services;

public static class WindowAppearance
{
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized -= SourceInitialized;
            window.SourceInitialized += SourceInitialized;
            return;
        }

        // Windows 10 ignores unsupported attributes; custom chrome remains usable.
        var dark = ThemeService.IsDark;
        var enabled = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) < 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));

        var roundedCorners = 2;
        DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));
        var border = ResourceColorRef("Line");
        var caption = ResourceColorRef("Panel");
        var text = ResourceColorRef("Ink");
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    private static int ResourceColorRef(string key)
    {
        // Read the published palette directly; WPF bindings may still be queued
        // when native caption colors are applied during a theme change.
        var color = ThemeService.Colors[key];
        // DWM takes COLORREF (0x00BBGGRR), not WPF's ARGB representation.
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static void SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= SourceInitialized;
        Apply(window);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

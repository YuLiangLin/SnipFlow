using System.Runtime.InteropServices;

namespace SnipFlow.Platform.Linux.X11;

internal static partial class XLib
{
    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8;
    public const nint XK_Num_Lock = 0xFF7F, XK_Scroll_Lock = 0xFF14;

    [DllImport(LibX11)] public static extern byte XKeysymToKeycode(IntPtr display, nint keysym);
    [DllImport(LibX11)] public static extern IntPtr XGetModifierMapping(IntPtr display);
    [DllImport(LibX11)] public static extern int XFreeModifiermap(IntPtr modifierMap);

    /// <summary>The modifier mask (Mod1…Mod5) that a lock key such as Num Lock is mapped to, or 0.</summary>
    public static unsafe uint ModifierMaskForKeysym(IntPtr display, nint keysym)
    {
        var keycode = XKeysymToKeycode(display, keysym);
        if (keycode == 0) return 0;
        var map = XGetModifierMapping(display);
        if (map == IntPtr.Zero) return 0;
        try
        {
            var keymap = (XModifierKeymap*)map;
            for (int modifier = 0; modifier < 8; modifier++)
                for (int index = 0; index < keymap->MaxKeysPerModifier; index++)
                    if (keymap->ModifierMap[modifier * keymap->MaxKeysPerModifier + index] == keycode)
                        return 1u << modifier;
            return 0;
        }
        finally { XFreeModifiermap(map); }
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct XModifierKeymap
    {
        public int MaxKeysPerModifier;
        public byte* ModifierMap;
    }
}

/// <summary>
/// The few libxcb calls used for global shortcuts. XCB "checked" requests return their X error to the
/// caller instead of the process-wide Xlib error handler, which Avalonia owns.
/// </summary>
internal static class Xcb
{
    const string LibXcb = "libxcb.so.1";
    public const byte KeyPress = 2, KeyRelease = 3;
    public const byte BadAccess = 10;
    public const byte GrabModeAsync = 1;

    [StructLayout(LayoutKind.Sequential)] public struct VoidCookie { public uint Sequence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenIterator { public IntPtr Data; public int Remaining; public int Index; }

    [DllImport(LibXcb)] public static extern IntPtr xcb_connect(IntPtr displayName, out int screen);
    [DllImport(LibXcb)] public static extern int xcb_connection_has_error(IntPtr connection);
    [DllImport(LibXcb)] public static extern void xcb_disconnect(IntPtr connection);
    [DllImport(LibXcb)] public static extern IntPtr xcb_get_setup(IntPtr connection);
    [DllImport(LibXcb)] public static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
    [DllImport(LibXcb)] public static extern void xcb_screen_next(ref ScreenIterator iterator);
    [DllImport(LibXcb)] public static extern int xcb_get_file_descriptor(IntPtr connection);
    [DllImport(LibXcb)] public static extern int xcb_flush(IntPtr connection);
    [DllImport(LibXcb)] public static extern IntPtr xcb_poll_for_event(IntPtr connection);
    [DllImport(LibXcb)] public static extern IntPtr xcb_request_check(IntPtr connection, VoidCookie cookie);

    [DllImport(LibXcb)]
    public static extern VoidCookie xcb_grab_key_checked(IntPtr connection, byte ownerEvents, uint grabWindow, ushort modifiers,
        byte key, byte pointerMode, byte keyboardMode);

    [DllImport(LibXcb)]
    public static extern VoidCookie xcb_ungrab_key_checked(IntPtr connection, byte key, uint grabWindow, ushort modifiers);

    /// <summary>Waits for a checked request; returns 0 on success, otherwise the X error code.</summary>
    public static unsafe byte Check(IntPtr connection, VoidCookie cookie)
    {
        var error = xcb_request_check(connection, cookie);
        if (error == IntPtr.Zero) return 0;
        var code = ((byte*)error)[1];
        LibC.free(error);
        return code == 0 ? (byte)255 : code;
    }
}

internal static class LibC
{
    public const short PollIn = 1;

    [StructLayout(LayoutKind.Sequential)] public struct PollFd { public int Fd; public short Events; public short Returned; }

    [DllImport("libc", SetLastError = true)] public static extern unsafe int poll(PollFd* fds, nuint count, int timeout);
    [DllImport("libc", SetLastError = true)] public static extern unsafe int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] public static extern unsafe nint read(int fd, byte* buffer, nuint count);
    [DllImport("libc", SetLastError = true)] public static extern unsafe nint write(int fd, byte* buffer, nuint count);
    [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] public static extern int fcntl(int fd, int command, int argument);
    [DllImport("libc")] public static extern void free(IntPtr pointer);
}

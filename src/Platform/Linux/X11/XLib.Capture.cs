using System.Runtime.InteropServices;

namespace SnipFlow.Platform.Linux.X11;

/// <summary>Xlib, MIT-SHM, XComposite and System V shared memory declarations used by screen capture.</summary>
internal static partial class XLib
{
    public const string LibXext = "libXext.so.6";
    public const string LibXcomposite = "libXcomposite.so.1";
    const string LibC = "libc";

    public const int InputOnly = 2;
    public const int ClientMessage = 33;
    public const int RevertToParent = 2;
    public const nint PointerRoot = 1;
    public const nint SubstructureNotifyMask = 1 << 19;
    public const nint SubstructureRedirectMask = 1 << 20;
    public const uint Button1Mask = 1 << 8, Button2Mask = 1 << 9, Button3Mask = 1 << 10;
    public const int LSBFirst = 0;

    [DllImport(LibX11)]
    public static extern int XSendEvent(IntPtr display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate,
        nint eventMask, IntPtr eventSend);

    [DllImport(LibX11)] public static extern int XRaiseWindow(IntPtr display, nint window);
    [DllImport(LibX11)] public static extern int XSetInputFocus(IntPtr display, nint focus, int revertTo, nint time);
    [DllImport(LibX11)] public static extern int XGetInputFocus(IntPtr display, out nint focus, out int revertTo);
    [DllImport(LibX11)] public static extern nint XGetSelectionOwner(IntPtr display, nint selection);
    [DllImport(LibX11)] public static extern IntPtr XDefaultVisual(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern int XDefaultDepth(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern int XFreePixmap(IntPtr display, nint pixmap);

    [DllImport(LibXext)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XShmQueryExtension(IntPtr display);

    [DllImport(LibXext)]
    public static extern IntPtr XShmCreateImage(IntPtr display, IntPtr visual, uint depth, int format, IntPtr data,
        ref XShmSegmentInfo info, uint width, uint height);

    [DllImport(LibXext)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XShmAttach(IntPtr display, ref XShmSegmentInfo info);

    [DllImport(LibXext)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XShmDetach(IntPtr display, ref XShmSegmentInfo info);

    [DllImport(LibXext)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XShmGetImage(IntPtr display, nint drawable, IntPtr image, int x, int y, nint planeMask);

    [DllImport(LibXcomposite)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XCompositeQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport(LibXcomposite)]
    public static extern nint XCompositeNameWindowPixmap(IntPtr display, nint window);

    [DllImport(LibC, SetLastError = true)] public static extern int shmget(int key, nuint size, int flags);
    [DllImport(LibC, SetLastError = true)] public static extern IntPtr shmat(int id, IntPtr address, int flags);
    [DllImport(LibC, SetLastError = true)] public static extern int shmdt(IntPtr address);
    [DllImport(LibC, SetLastError = true)] public static extern int shmctl(int id, int command, IntPtr buffer);

    public const int IpcPrivate = 0, IpcCreate = 0x200, IpcRemove = 0;

    /// <summary>
    /// Sends an EWMH client message to the root window (32-bit format, five longs), as window managers
    /// expect for _NET_ACTIVE_WINDOW and similar requests.
    /// </summary>
    public static unsafe void SendRootClientMessage(IntPtr display, nint root, nint window, nint messageType,
        nint data0, nint data1 = 0, nint data2 = 0, nint data3 = 0, nint data4 = 0)
    {
        // XClientMessageEvent inside the 24-long XEvent union (LP64 layout).
        var buffer = stackalloc nint[24];
        new Span<nint>(buffer, 24).Clear();
        var bytes = (byte*)buffer;
        *(int*)bytes = ClientMessage;                 // type
        *(int*)(bytes + 16) = 1;                      // send_event
        *(IntPtr*)(bytes + 24) = display;             // display
        *(nint*)(bytes + 32) = window;                // window
        *(nint*)(bytes + 40) = messageType;           // message_type
        *(int*)(bytes + 48) = 32;                     // format
        var data = (nint*)(bytes + 56);
        data[0] = data0; data[1] = data1; data[2] = data2; data[3] = data3; data[4] = data4;
        XSendEvent(display, root, false, SubstructureRedirectMask | SubstructureNotifyMask, (IntPtr)buffer);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct XShmSegmentInfo
{
    public nint ShmSeg;
    public int ShmId;
    public IntPtr ShmAddr;
    public int ReadOnly;
}

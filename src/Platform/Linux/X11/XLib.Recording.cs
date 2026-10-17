using System.Runtime.InteropServices;

namespace SnipFlow.Platform.Linux.X11;

/// <summary>Xlib and XShape declarations for the recording border (plain override-redirect windows).</summary>
internal static partial class XLib
{
    public const uint InputOutput = 1;
    public const nuint CWBackPixel = 1 << 1;
    public const nuint CWBorderPixel = 1 << 3;
    public const nuint CWOverrideRedirect = 1 << 9;
    public const nuint CWSaveUnder = 1 << 10;
    public const nuint CWEventMask = 1 << 11;
    public const int ShapeInput = 2;
    public const int ShapeSet = 0;
    public const int Unsorted = 0;
    public const int PropModeReplace = 0;
    public const nint XA_STRING = 31;

    [DllImport(LibX11)]
    public static extern nint XCreateWindow(IntPtr display, nint parent, int x, int y, uint width, uint height,
        uint borderWidth, int depth, uint windowClass, IntPtr visual, nuint valueMask, ref XSetWindowAttributes attributes);

    [DllImport(LibX11)] public static extern int XMapRaised(IntPtr display, nint window);
    [DllImport(LibX11)] public static extern int XDestroyWindow(IntPtr display, nint window);
    [DllImport(LibX11)] public static extern int XMoveResizeWindow(IntPtr display, nint window, int x, int y, uint width, uint height);
    [DllImport(LibX11)] public static extern int XSetWindowBackground(IntPtr display, nint window, nuint pixel);
    [DllImport(LibX11)] public static extern int XClearWindow(IntPtr display, nint window);
    [DllImport(LibX11)] public static extern nint XDefaultColormap(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern int XAllocColor(IntPtr display, nint colormap, ref XColor color);

    [DllImport(LibX11)]
    public static extern int XChangeProperty(IntPtr display, nint window, nint property, nint type, int format, int mode,
        nint[] data, int elements);

    [DllImport(LibX11)]
    public static extern int XChangeProperty(IntPtr display, nint window, nint property, nint type, int format, int mode,
        byte[] data, int elements);

    [DllImport(LibXext)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport(LibXext)]
    public static extern void XShapeCombineRectangles(IntPtr display, nint destination, int destinationKind, int xOffset,
        int yOffset, IntPtr rectangles, int count, int operation, int ordering);
}

/// <summary>XSetWindowAttributes (LP64 layout).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XSetWindowAttributes
{
    public nint BackgroundPixmap;
    public nuint BackgroundPixel;
    public nint BorderPixmap;
    public nuint BorderPixel;
    public int BitGravity, WinGravity;
    public int BackingStore;
    public nuint BackingPlanes, BackingPixel;
    public int SaveUnder;
    public nint EventMask, DoNotPropagateMask;
    public int OverrideRedirect;
    public nint Colormap;
    public nint Cursor;
}

/// <summary>XColor: pixel, 16-bit red/green/blue, flags.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XColor
{
    public nuint Pixel;
    public ushort Red, Green, Blue;
    public byte Flags, Pad;
}

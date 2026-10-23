using System.Runtime.InteropServices;

namespace SnipFlow.Platform.Linux.X11;

/// <summary>
/// Minimal Xlib / XRandR bindings shared by Linux capture, window tracking and global shortcuts.
/// Modules add the declarations they need in their own <c>XLib.Area.cs</c> partial files.
/// </summary>
/// <remarks>
/// Open a private connection with <see cref="XDisplay"/> for each worker; Avalonia already called
/// XInitThreads and installed a non-fatal X error handler, so do not replace the handler. X window
/// ids and atoms are C <c>unsigned long</c> (64-bit on x64 / arm64 Linux) and are exposed as <see cref="nint"/>.
/// </remarks>
internal static partial class XLib
{
    public const string LibX11 = "libX11.so.6";
    public const string LibXrandr = "libXrandr.so.2";

    public const int ZPixmap = 2;
    public const int Success = 0;
    public const int IsViewable = 2;
    public const nint AnyPropertyType = 0;
    public const nint XA_ATOM = 4;
    public const nint XA_CARDINAL = 6;
    public const nint XA_WINDOW = 33;
    public const nint AllPlanes = -1;

    [DllImport(LibX11)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] public static extern nint XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] public static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] public static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport(LibX11)] public static extern int XFree(IntPtr data);
    [DllImport(LibX11)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(LibX11)] public static extern int XPending(IntPtr display);

    [DllImport(LibX11, CharSet = CharSet.Ansi)]
    public static extern nint XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(IntPtr display, nint window, nint property, nint offset, nint length,
        [MarshalAs(UnmanagedType.Bool)] bool delete, nint requestedType, out nint actualType, out int actualFormat,
        out nint itemCount, out nint bytesAfter, out IntPtr data);

    [DllImport(LibX11)]
    public static extern int XGetWindowAttributes(IntPtr display, nint window, out XWindowAttributes attributes);

    [DllImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XTranslateCoordinates(IntPtr display, nint source, nint destination, int sourceX, int sourceY,
        out int destinationX, out int destinationY, out nint child);

    [DllImport(LibX11)]
    public static extern int XGetGeometry(IntPtr display, nint drawable, out nint root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport(LibX11)]
    public static extern int XQueryTree(IntPtr display, nint window, out nint root, out nint parent, out IntPtr children, out uint childCount);

    [DllImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool XQueryPointer(IntPtr display, nint window, out nint root, out nint child,
        out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);

    [DllImport(LibX11)]
    public static extern IntPtr XGetImage(IntPtr display, nint drawable, int x, int y, uint width, uint height, nint planeMask, int format);

    [DllImport(LibXrandr)]
    public static extern IntPtr XRRGetMonitors(IntPtr display, nint window, [MarshalAs(UnmanagedType.Bool)] bool getActive, out int count);

    [DllImport(LibXrandr)]
    public static extern void XRRFreeMonitors(IntPtr monitors);

    /// <summary>Frees an XImage. XDestroyImage is a macro in Xutil.h, so call the image's own destroy function.</summary>
    public static unsafe void DestroyImage(IntPtr image)
    {
        if (image == IntPtr.Zero) return;
        var destroy = (delegate* unmanaged<IntPtr, int>)((XImage*)image)->DestroyImage;
        if (destroy is not null) destroy(image);
    }

    /// <summary>Reads a 32-bit-format window property (CARDINAL, WINDOW, ATOM). Returns null when absent.</summary>
    public static nint[]? GetLongProperty(IntPtr display, nint window, nint property, nint type)
    {
        if (XGetWindowProperty(display, window, property, 0, 4096, false, type, out var actualType, out var format,
                out var count, out _, out var data) != Success || data == IntPtr.Zero)
            return null;
        try
        {
            if (format != 32 || actualType == 0) return null;
            // Format-32 properties are delivered as C longs.
            var values = new nint[count];
            Marshal.Copy(data, values, 0, (int)count);
            return values;
        }
        finally { XFree(data); }
    }

    /// <summary>Reads an 8-bit string property such as _NET_WM_NAME (UTF8_STRING) or WM_CLASS.</summary>
    public static string? GetStringProperty(IntPtr display, nint window, nint property, nint type = AnyPropertyType)
    {
        if (XGetWindowProperty(display, window, property, 0, 1024, false, type, out _, out var format,
                out var count, out _, out var data) != Success || data == IntPtr.Zero)
            return null;
        try { return format == 8 ? Marshal.PtrToStringUTF8(data, (int)count) : null; }
        finally { XFree(data); }
    }
}

/// <summary>A private Xlib connection, closed on dispose.</summary>
internal sealed class XDisplay : IDisposable
{
    public IntPtr Handle { get; private set; }
    public nint Root { get; }

    XDisplay(IntPtr handle)
    {
        Handle = handle;
        Root = XLib.XDefaultRootWindow(handle);
    }

    /// <summary>Opens $DISPLAY; returns null when no X server (or XWayland) is reachable.</summary>
    public static XDisplay? TryOpen()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        try
        {
            var handle = XLib.XOpenDisplay(IntPtr.Zero);
            return handle == IntPtr.Zero ? null : new XDisplay(handle);
        }
        catch (DllNotFoundException) { return null; }
    }

    public nint Atom(string name) => XLib.XInternAtom(Handle, name, false);

    public void Dispose()
    {
        var handle = Handle;
        Handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) XLib.XCloseDisplay(handle);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct XWindowAttributes
{
    public int X, Y, Width, Height, BorderWidth, Depth;
    public IntPtr Visual;
    public nint Root;
    public int Class, BitGravity, WinGravity, BackingStore;
    public nint BackingPlanes, BackingPixel;
    public int SaveUnder;
    public nint Colormap;
    public int MapInstalled, MapState;
    public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
    public int OverrideRedirect;
    public IntPtr Screen;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XImage
{
    public int Width, Height, XOffset, Format;
    public IntPtr Data;
    public int ByteOrder, BitmapUnit, BitmapBitOrder, BitmapPad, Depth, BytesPerLine, BitsPerPixel;
    public nint RedMask, GreenMask, BlueMask;
    public IntPtr ObData;
    public IntPtr CreateImage, DestroyImage, GetPixel, PutPixel, SubImage, AddPixel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XRRMonitorInfo
{
    public nint Name;
    public int Primary, Automatic, OutputCount;
    public int X, Y, Width, Height, WidthMillimeters, HeightMillimeters;
    public IntPtr Outputs;
}

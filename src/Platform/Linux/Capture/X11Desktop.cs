using BitOperations = System.Numerics.BitOperations;
using System.Runtime.InteropServices;
using SnipFlow.Capture;
using SnipFlow.Platform.Linux.X11;

namespace SnipFlow.Platform.Linux.Capture;

/// <summary>
/// X11 desktop access for capture: XRandR monitors, root-window pixels (MIT-SHM when available),
/// EWMH window lists, stacking, focus and activation. One private Xlib connection serves all calls;
/// Xlib is thread-safe (Avalonia called XInitThreads) and the lock keeps the shared image buffers
/// consistent. In Wayland sessions the same connection reaches XWayland for monitors and the pointer.
/// </summary>
internal static class X11Desktop
{
    private static readonly object Gate = new();
    private static XDisplay? _display;
    private static Atoms? _atoms;
    private static ShmImage? _shmImage;
    private static bool? _shmAvailable;

    private sealed class Atoms(XDisplay display)
    {
        public readonly nint ClientListStacking = display.Atom("_NET_CLIENT_LIST_STACKING");
        public readonly nint ClientList = display.Atom("_NET_CLIENT_LIST");
        public readonly nint FrameExtents = display.Atom("_NET_FRAME_EXTENTS");
        public readonly nint GtkFrameExtents = display.Atom("_GTK_FRAME_EXTENTS");
        public readonly nint State = display.Atom("_NET_WM_STATE");
        public readonly nint StateHidden = display.Atom("_NET_WM_STATE_HIDDEN");
        public readonly nint Pid = display.Atom("_NET_WM_PID");
        public readonly nint Name = display.Atom("_NET_WM_NAME");
        public readonly nint Utf8String = display.Atom("UTF8_STRING");
        public readonly nint WmName = display.Atom("WM_NAME");
        public readonly nint WindowType = display.Atom("_NET_WM_WINDOW_TYPE");
        public readonly nint TypeDesktop = display.Atom("_NET_WM_WINDOW_TYPE_DESKTOP");
        public readonly nint TypeDock = display.Atom("_NET_WM_WINDOW_TYPE_DOCK");
        public readonly nint ActiveWindow = display.Atom("_NET_ACTIVE_WINDOW");
        public readonly nint Desktop = display.Atom("_NET_WM_DESKTOP");
        public readonly nint CurrentDesktop = display.Atom("_NET_CURRENT_DESKTOP");
        public readonly nint SupportingWmCheck = display.Atom("_NET_SUPPORTING_WM_CHECK");
        public readonly nint CompositorSelection =
            display.Atom("_NET_WM_CM_S" + XLib.XDefaultScreen(display.Handle).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A top-level X11 client: identity and physical geometry.</summary>
    internal readonly record struct ClientGeometry(nint Client, nint TopLevel, PixelRect Frame, PixelRect ClientArea,
        uint ProcessId, bool IsShellSurface);

    private static T Use<T>(Func<XDisplay, Atoms, T> action)
    {
        lock (Gate)
        {
            if (_display is null || _display.Handle == IntPtr.Zero)
            {
                _display = XDisplay.TryOpen()
                    ?? throw new InvalidOperationException(I18n.T("無法連線到 X 顯示伺服器（DISPLAY），無法擷取螢幕。"));
                _atoms = new Atoms(_display);
            }
            return action(_display, _atoms!);
        }
    }

    // ---- Monitors and pointer ----------------------------------------------------------------

    internal static IReadOnlyList<CaptureMonitor> GetMonitors() => Use((display, atoms) =>
    {
        var monitors = new List<CaptureMonitor>();
        IntPtr list = IntPtr.Zero;
        var count = 0;
        try { list = XLib.XRRGetMonitors(display.Handle, display.Root, true, out count); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
        if (list != IntPtr.Zero)
        {
            try
            {
                var size = Marshal.SizeOf<XRRMonitorInfo>();
                for (var index = 0; index < count; index++)
                {
                    var info = Marshal.PtrToStructure<XRRMonitorInfo>(list + index * size);
                    if (info.Width > 0 && info.Height > 0)
                        monitors.Add(new CaptureMonitor(new PixelRect(info.X, info.Y, info.Width, info.Height), info.Primary != 0));
                }
            }
            finally { XLib.XRRFreeMonitors(list); }
        }
        if (monitors.Count == 0 && XLib.XGetGeometry(display.Handle, display.Root, out _, out _, out _,
                out var width, out var height, out _, out _) != 0 && width > 0 && height > 0)
            monitors.Add(new CaptureMonitor(new PixelRect(0, 0, (int)width, (int)height), true));
        if (monitors.Count == 0)
            throw new InvalidOperationException(I18n.T("目前找不到可擷取的螢幕。"));
        // Report the primary monitor first, like Windows enumerates it.
        return (IReadOnlyList<CaptureMonitor>)monitors.OrderByDescending(monitor => monitor.IsPrimary).ToList();
    });

    internal static PixelPoint CursorPosition() => Use((display, atoms) =>
        XLib.XQueryPointer(display.Handle, display.Root, out _, out _, out var x, out var y, out _, out _, out _)
            ? new PixelPoint(x, y)
            : throw new InvalidOperationException(I18n.T("無法讀取滑鼠位置，請重新操作。")));

    internal static bool IsPointerButtonDown() => Use((display, atoms) =>
        XLib.XQueryPointer(display.Handle, display.Root, out _, out _, out _, out _, out _, out _, out var mask)
        && (mask & (XLib.Button1Mask | XLib.Button2Mask | XLib.Button3Mask)) != 0);

    /// <summary>True when a compositing manager owns _NET_WM_CM_Sn, so window pixmaps stay valid when covered.</summary>
    internal static bool IsCompositing() => Use((display, atoms) =>
        XLib.XGetSelectionOwner(display.Handle, atoms.CompositorSelection) != 0);

    internal static void Sync() => Use((display, atoms) => XLib.XSync(display.Handle, false));

    // ---- Pixels -------------------------------------------------------------------------------

    /// <summary>Copies root-window pixels; parts outside every monitor stay opaque black.</summary>
    internal static PixelBuffer CopyDesktop(PixelRect bounds, IReadOnlyList<CaptureMonitor> monitors) => Use((display, atoms) =>
    {
        var buffer = new PixelBuffer(bounds.Width, bounds.Height);
        XLib.XGetGeometry(display.Handle, display.Root, out _, out _, out _, out var rootWidth, out var rootHeight, out _, out _);
        var root = new PixelRect(0, 0, (int)rootWidth, (int)rootHeight);
        // One read of the whole rectangle (a single round trip, and one reusable shared-memory
        // segment for repeated captures); then blacken what no monitor shows, like the per-monitor
        // GDI copies on Windows leave virtual-desktop gaps black.
        var area = CaptureGeometry.Intersect(bounds, root);
        if (!CaptureGeometry.IsEmpty(area))
            ReadDrawable(display, display.Root, area, buffer, area.X - bounds.X, area.Y - bounds.Y, opaque: true);
        foreach (var gap in CaptureGeometry.Subtract(bounds, monitors.Select(monitor => monitor.Bounds)))
            FillBlack(buffer, new PixelRect(gap.X - bounds.X, gap.Y - bounds.Y, gap.Width, gap.Height));
        SetOpaque(buffer.Pixels);
        return buffer;
    });

    private static void FillBlack(PixelBuffer buffer, PixelRect area)
    {
        for (var row = area.Y; row < area.Bottom; row++)
            Array.Clear(buffer.Pixels, row * buffer.Stride + area.X * 4, area.Width * 4);
    }

    /// <summary>Reads a rectangle of a drawable into <paramref name="target"/> at (x, y).</summary>
    private static void ReadDrawable(XDisplay display, nint drawable, PixelRect source, PixelBuffer target,
        int x, int y, bool opaque)
    {
        if (drawable == display.Root && TryReadShared(display, source, target, x, y))
            return;
        var image = XLib.XGetImage(display.Handle, drawable, source.X, source.Y, (uint)source.Width, (uint)source.Height,
            XLib.AllPlanes, XLib.ZPixmap);
        if (image == IntPtr.Zero)
            throw new InvalidOperationException(I18n.T("無法讀取螢幕畫面，請稍後重試。"));
        try { CopyImage(image, target, x, y, opaque); }
        finally { XLib.DestroyImage(image); }
    }

    /// <summary>
    /// MIT-SHM read of the root window: the server writes into shared memory instead of sending
    /// the pixels over the socket, which keeps repeated full-screen captures (scrolling capture)
    /// several times faster than XGetImage. The segment is kept for the last size.
    /// </summary>
    private static bool TryReadShared(XDisplay display, PixelRect source, PixelBuffer target, int x, int y)
    {
        if (_shmAvailable == false) return false;
        try
        {
            _shmAvailable ??= XLib.XShmQueryExtension(display.Handle)
                && Environment.GetEnvironmentVariable("SNIPFLOW_NO_XSHM") is null;
            if (_shmAvailable != true) return false;
            if (_shmImage is null || _shmImage.Width != source.Width || _shmImage.Height != source.Height)
            {
                _shmImage?.Dispose();
                _shmImage = null;
                _shmImage = ShmImage.Create(display, source.Width, source.Height);
                if (_shmImage is null) { _shmAvailable = false; return false; }
            }
            if (!XLib.XShmGetImage(display.Handle, display.Root, _shmImage.Image, source.X, source.Y, XLib.AllPlanes))
                return false;
            CopyImage(_shmImage.Image, target, x, y, opaque: true);
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _shmAvailable = false;
            return false;
        }
    }

    private sealed class ShmImage : IDisposable
    {
        private XShmSegmentInfo _info;
        private readonly XDisplay _display;
        public IntPtr Image { get; private set; }
        public int Width { get; }
        public int Height { get; }

        private ShmImage(XDisplay display, int width, int height)
        {
            _display = display;
            Width = width;
            Height = height;
        }

        public static unsafe ShmImage? Create(XDisplay display, int width, int height)
        {
            var screen = XLib.XDefaultScreen(display.Handle);
            var result = new ShmImage(display, width, height);
            result.Image = XLib.XShmCreateImage(display.Handle, XLib.XDefaultVisual(display.Handle, screen),
                (uint)XLib.XDefaultDepth(display.Handle, screen), XLib.ZPixmap, IntPtr.Zero, ref result._info,
                (uint)width, (uint)height);
            if (result.Image == IntPtr.Zero) return null;
            var image = (XImage*)result.Image;
            var size = (nuint)image->BytesPerLine * (nuint)image->Height;
            result._info.ShmId = XLib.shmget(XLib.IpcPrivate, size, XLib.IpcCreate | 0x180);
            if (result._info.ShmId < 0) { result.Free(attached: false); return null; }
            result._info.ShmAddr = XLib.shmat(result._info.ShmId, IntPtr.Zero, 0);
            if (result._info.ShmAddr == new IntPtr(-1))
            {
                XLib.shmctl(result._info.ShmId, XLib.IpcRemove, IntPtr.Zero);
                result._info.ShmAddr = IntPtr.Zero;
                result.Free(attached: false);
                return null;
            }
            image->Data = result._info.ShmAddr;
            result._info.ReadOnly = 0;
            var attached = XLib.XShmAttach(display.Handle, ref result._info);
            // A remote or restricted server reports the attach failure asynchronously.
            XLib.XSync(display.Handle, false);
            // Removal is deferred until both sides detach, so the segment cannot leak on exit.
            XLib.shmctl(result._info.ShmId, XLib.IpcRemove, IntPtr.Zero);
            if (!attached) { result.Free(attached: false); return null; }
            return result;
        }

        private unsafe void Free(bool attached)
        {
            if (attached && _display.Handle != IntPtr.Zero)
            {
                XLib.XShmDetach(_display.Handle, ref _info);
                XLib.XSync(_display.Handle, false);
            }
            if (Image != IntPtr.Zero)
            {
                ((XImage*)Image)->Data = IntPtr.Zero; // the segment is not malloc'ed
                XLib.DestroyImage(Image);
                Image = IntPtr.Zero;
            }
            if (_info.ShmAddr != IntPtr.Zero)
            {
                XLib.shmdt(_info.ShmAddr);
                _info.ShmAddr = IntPtr.Zero;
            }
        }

        public void Dispose() => Free(attached: true);
    }

    /// <summary>Converts an XImage (any 24/32-bit TrueColor layout, or 16-bit) to BGRA premultiplied.</summary>
    private static unsafe void CopyImage(IntPtr handle, PixelBuffer target, int x, int y, bool opaque)
    {
        var image = (XImage*)handle;
        int width = Math.Min(image->Width, target.Width - x), height = Math.Min(image->Height, target.Height - y);
        if (width <= 0 || height <= 0) return;
        var source = (byte*)image->Data;
        // Pixmaps have no visual, so XGetImage reports zero masks for them; their 24/32-bit
        // content uses the TrueColor layout of the windows they were named from.
        var masks = image->RedMask == 0 && image->GreenMask == 0 && image->BlueMask == 0
            || image->RedMask == 0xFF0000 && image->GreenMask == 0xFF00 && image->BlueMask == 0xFF;
        var standard = image->BitsPerPixel == 32 && image->ByteOrder == XLib.LSBFirst && masks;
        fixed (byte* destinationBase = target.Pixels)
        {
            for (var row = 0; row < height; row++)
            {
                var sourceRow = source + (long)row * image->BytesPerLine;
                var destinationRow = destinationBase + ((long)(y + row) * target.Stride + x * 4L);
                if (standard)
                {
                    Buffer.MemoryCopy(sourceRow, destinationRow, width * 4L, width * 4L);
                    // Depth-32 (ARGB) visuals already carry premultiplied alpha; depth 24 has none.
                    if (opaque || image->Depth != 32)
                        SetOpaque(new Span<byte>(destinationRow, width * 4));
                    continue;
                }
                ConvertRow(image, sourceRow, destinationRow, width);
            }
        }
    }

    private static unsafe void ConvertRow(XImage* image, byte* sourceRow, byte* destinationRow, int width)
    {
        var bytesPerPixel = image->BitsPerPixel / 8;
        var (redShift, redBits) = MaskShift((ulong)image->RedMask);
        var (greenShift, greenBits) = MaskShift((ulong)image->GreenMask);
        var (blueShift, blueBits) = MaskShift((ulong)image->BlueMask);
        var bigEndian = image->ByteOrder != XLib.LSBFirst;
        for (var column = 0; column < width; column++)
        {
            var pixel = sourceRow + column * bytesPerPixel;
            ulong value = 0;
            for (var index = 0; index < bytesPerPixel; index++)
                value |= (ulong)pixel[bigEndian ? bytesPerPixel - 1 - index : index] << (8 * index);
            var output = destinationRow + column * 4;
            output[0] = Expand(value, blueShift, blueBits);
            output[1] = Expand(value, greenShift, greenBits);
            output[2] = Expand(value, redShift, redBits);
            output[3] = 255;
        }
    }

    private static (int Shift, int Bits) MaskShift(ulong mask)
    {
        if (mask == 0) return (0, 0);
        var shift = BitOperations.TrailingZeroCount(mask);
        return (shift, BitOperations.PopCount(mask));
    }

    private static byte Expand(ulong value, int shift, int bits)
    {
        if (bits == 0) return 0;
        var component = (value >> shift) & ((1UL << bits) - 1);
        return bits >= 8 ? (byte)(component >> (bits - 8)) : (byte)(component * 255 / ((1UL << bits) - 1));
    }

    internal static void SetOpaque(Span<byte> pixels)
    {
        var values = MemoryMarshal.Cast<byte, uint>(pixels);
        var alpha = new System.Numerics.Vector<uint>(0xFF000000);
        var index = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var vectors = MemoryMarshal.Cast<uint, System.Numerics.Vector<uint>>(values);
            for (var vector = 0; vector < vectors.Length; vector++)
                vectors[vector] |= alpha;
            index = vectors.Length * System.Numerics.Vector<uint>.Count;
        }
        for (; index < values.Length; index++)
            values[index] |= 0xFF000000;
    }

    // ---- Windows ------------------------------------------------------------------------------

    /// <summary>Managed top-level clients, topmost first.</summary>
    internal static IReadOnlyList<CaptureWindow> GetWindows() => Use((display, atoms) =>
    {
        var result = new List<CaptureWindow>();
        var stacking = ClientsBottomToTop(display, atoms);
        for (var index = stacking.Count - 1; index >= 0; index--)
            if (TryGetWindowCore(display, atoms, stacking[index], out var window))
                result.Add(window);
        return (IReadOnlyList<CaptureWindow>)result;
    });

    internal static bool TryGetWindow(nint window, out CaptureWindow item)
    {
        CaptureWindow? found = null;
        var ok = Use((display, atoms) =>
        {
            var success = TryGetWindowCore(display, atoms, window, out var current);
            found = current;
            return success;
        });
        item = found!;
        return ok;
    }

    internal static bool TryGetGeometry(nint window, out ClientGeometry geometry)
    {
        ClientGeometry found = default;
        var ok = Use((display, atoms) => TryGetGeometryCore(display, atoms, window, out found));
        geometry = found;
        return ok;
    }

    private static List<nint> ClientsBottomToTop(XDisplay display, Atoms atoms)
    {
        var clients = XLib.GetLongProperty(display.Handle, display.Root, atoms.ClientListStacking, XLib.XA_WINDOW)
            ?? XLib.GetLongProperty(display.Handle, display.Root, atoms.ClientList, XLib.XA_WINDOW);
        if (clients is not null)
            return clients.Where(window => window != 0).ToList();
        // No EWMH window manager: the root's children are the top-level windows in stacking order.
        var list = new List<nint>();
        foreach (var child in Children(display, display.Root))
            if (XLib.XGetWindowAttributes(display.Handle, child, out var attributes) != 0 && attributes.OverrideRedirect == 0)
                list.Add(child);
        return list;
    }

    private static bool TryGetWindowCore(XDisplay display, Atoms atoms, nint window, out CaptureWindow item)
    {
        item = null!;
        if (!TryGetGeometryCore(display, atoms, window, out var geometry))
            return false;
        var title = XLib.GetStringProperty(display.Handle, window, atoms.Name, atoms.Utf8String)
            ?? XLib.GetStringProperty(display.Handle, window, atoms.WmName) ?? "";
        item = new CaptureWindow(window, geometry.ProcessId, geometry.Frame, title.TrimEnd('\0'),
            geometry.ProcessId != Environment.ProcessId && !geometry.IsShellSurface);
        return true;
    }

    /// <summary>Reads a shown client's frame (WM decorations, without GTK shadow margins) and client area.</summary>
    private static bool TryGetGeometryCore(XDisplay display, Atoms atoms, nint window, out ClientGeometry geometry)
    {
        geometry = default;
        if (window == 0 || XLib.XGetWindowAttributes(display.Handle, window, out var attributes) == 0
            || attributes.MapState != XLib.IsViewable || attributes.Class == XLib.InputOnly
            || attributes.Width <= 0 || attributes.Height <= 0)
            return false;
        var states = XLib.GetLongProperty(display.Handle, window, atoms.State, XLib.XA_ATOM);
        if (states?.Contains(atoms.StateHidden) == true)
            return false;
        var desktop = XLib.GetLongProperty(display.Handle, window, atoms.Desktop, XLib.XA_CARDINAL);
        var current = XLib.GetLongProperty(display.Handle, display.Root, atoms.CurrentDesktop, XLib.XA_CARDINAL);
        if (desktop is { Length: > 0 } && current is { Length: > 0 } && (uint)desktop[0] != 0xFFFFFFFF
            && (uint)desktop[0] != (uint)current[0])
            return false;
        if (!XLib.XTranslateCoordinates(display.Handle, window, display.Root, 0, 0, out var x, out var y, out _))
            return false;

        var gtk = Extents(display, window, atoms.GtkFrameExtents);
        var frame = Extents(display, window, atoms.FrameExtents);
        var client = CaptureGeometry.FromLTRB(x + gtk.Left, y + gtk.Top,
            x + attributes.Width - gtk.Right, y + attributes.Height - gtk.Bottom);
        if (CaptureGeometry.IsEmpty(client))
            return false;
        var bounds = CaptureGeometry.FromLTRB(client.X - frame.Left, client.Y - frame.Top,
            client.Right + frame.Right, client.Bottom + frame.Bottom);

        var pid = XLib.GetLongProperty(display.Handle, window, atoms.Pid, XLib.XA_CARDINAL);
        var types = XLib.GetLongProperty(display.Handle, window, atoms.WindowType, XLib.XA_ATOM);
        var shellSurface = types?.Any(type => type == atoms.TypeDesktop || type == atoms.TypeDock) == true;
        geometry = new ClientGeometry(window, TopLevel(display, window), bounds, client,
            pid is { Length: > 0 } ? (uint)pid[0] : 0, shellSurface);
        return true;
    }

    private readonly record struct Edges(int Left, int Right, int Top, int Bottom);

    private static Edges Extents(XDisplay display, nint window, nint property)
    {
        var values = XLib.GetLongProperty(display.Handle, window, property, XLib.XA_CARDINAL);
        return values is { Length: >= 4 }
            ? new Edges((int)Math.Clamp(values[0], 0, 4096), (int)Math.Clamp(values[1], 0, 4096),
                (int)Math.Clamp(values[2], 0, 4096), (int)Math.Clamp(values[3], 0, 4096))
            : default;
    }

    private static nint[] Children(XDisplay display, nint window)
    {
        if (XLib.XQueryTree(display.Handle, window, out _, out _, out var children, out var count) == 0)
            return Array.Empty<nint>();
        try
        {
            if (children == IntPtr.Zero || count == 0) return Array.Empty<nint>();
            var result = new nint[count];
            Marshal.Copy(children, result, 0, (int)count);
            return result;
        }
        finally
        {
            if (children != IntPtr.Zero) XLib.XFree(children);
        }
    }

    /// <summary>The child of the root window that contains <paramref name="window"/> (its WM frame), or 0.</summary>
    private static nint TopLevel(XDisplay display, nint window)
    {
        for (var depth = 0; window != 0 && depth < 64; depth++)
        {
            if (XLib.XQueryTree(display.Handle, window, out var root, out var parent, out var children, out _) == 0)
                return 0;
            if (children != IntPtr.Zero) XLib.XFree(children);
            if (parent == root || parent == 0) return window;
            window = parent;
        }
        return 0;
    }

    internal static nint GetForegroundWindow() => Use((display, atoms) =>
    {
        var active = XLib.GetLongProperty(display.Handle, display.Root, atoms.ActiveWindow, XLib.XA_WINDOW);
        if (active is not null)
            return active.Length > 0 ? active[0] : 0;
        // No EWMH window manager: the keyboard focus window's top-level window.
        XLib.XGetInputFocus(display.Handle, out var focus, out _);
        return focus is 0 or XLib.PointerRoot ? 0 : TopLevel(display, focus);
    });

    /// <summary>Asks the window manager to activate (raise and focus) a client, as a pager would.</summary>
    internal static void RequestActivation(nint window) => Use((display, atoms) =>
    {
        var hasWindowManager = XLib.GetLongProperty(display.Handle, display.Root, atoms.SupportingWmCheck, XLib.XA_WINDOW) is { Length: > 0 };
        if (hasWindowManager)
            // Source indication 2 (pager): an explicit user choice, not an application stealing focus.
            XLib.SendRootClientMessage(display.Handle, display.Root, window, atoms.ActiveWindow, 2, 0, 0);
        else
        {
            XLib.XRaiseWindow(display.Handle, window);
            XLib.XSetInputFocus(display.Handle, window, XLib.RevertToParent, 0);
        }
        XLib.XFlush(display.Handle);
        return true;
    });

    internal static nint GetWindowAt(PixelPoint point) => Use((display, atoms) =>
    {
        var stacking = ClientsBottomToTop(display, atoms);
        for (var index = stacking.Count - 1; index >= 0; index--)
            if (TryGetGeometryCore(display, atoms, stacking[index], out var geometry)
                && CaptureGeometry.Contains(geometry.Frame, point))
                return geometry.Client;
        return 0;
    });

    /// <summary>
    /// Mapped surfaces stacked above a client, nearest first. The root's children are the true
    /// stacking order, including override-redirect menus and tooltips that EWMH lists omit.
    /// </summary>
    internal static IReadOnlyList<CaptureWindowLayer>? GetWindowsAbove(nint window, IReadOnlyCollection<nint>? excludedWindows) =>
        Use((display, atoms) =>
        {
            var target = TopLevel(display, window);
            if (target == 0) return null;
            var clients = XLib.GetLongProperty(display.Handle, display.Root, atoms.ClientList, XLib.XA_WINDOW)
                ?? XLib.GetLongProperty(display.Handle, display.Root, atoms.ClientListStacking, XLib.XA_WINDOW)
                ?? Array.Empty<nint>();
            var clientOf = new Dictionary<nint, nint>();
            foreach (var client in clients)
            {
                var top = TopLevel(display, client);
                if (top != 0) clientOf[top] = client;
            }
            var excluded = new HashSet<nint>();
            foreach (var handle in excludedWindows ?? Array.Empty<nint>())
            {
                excluded.Add(handle);
                var top = TopLevel(display, handle);
                if (top != 0) excluded.Add(top);
            }

            var children = Children(display, display.Root);
            var position = Array.IndexOf(children, target);
            if (position < 0 || children.Length > 4096) return null;
            var layers = new List<CaptureWindowLayer>();
            for (var index = position + 1; index < children.Length; index++)
            {
                var child = children[index];
                var handle = clientOf.TryGetValue(child, out var client) ? client : child;
                if (excluded.Contains(child) || excluded.Contains(handle))
                    continue;
                if (XLib.XGetWindowAttributes(display.Handle, child, out var attributes) == 0)
                    continue; // destroyed during the walk
                if (attributes.MapState != XLib.IsViewable || attributes.Class == XLib.InputOnly)
                    continue;
                PixelRect bounds;
                if (handle != child)
                {
                    if (!TryGetGeometryCore(display, atoms, handle, out var geometry))
                        continue; // hidden or on another workspace
                    bounds = geometry.Frame;
                }
                else
                {
                    var border = attributes.BorderWidth * 2;
                    bounds = new PixelRect(attributes.X, attributes.Y, attributes.Width + border, attributes.Height + border);
                }
                if (!CaptureGeometry.IsEmpty(bounds))
                    layers.Add(new CaptureWindowLayer(handle, bounds));
            }
            return (IReadOnlyList<CaptureWindowLayer>?)layers;
        });

    // ---- Window pixels ------------------------------------------------------------------------

    /// <summary>
    /// Reads a window's own pixels from the compositor's XComposite pixmap of its top-level (frame)
    /// window, so covered or partly off-screen windows are correct. Returns null when no compositing
    /// manager redirects the window.
    /// </summary>
    internal static PixelBuffer? TryCopyWindowPixmap(nint window, PixelRect frame) => Use((display, atoms) =>
    {
        if (!HasComposite(display)) return null;
        var top = TopLevel(display, window);
        if (top == 0 || XLib.XGetGeometry(display.Handle, top, out _, out var x, out var y, out var width, out var height,
                out var border, out var depth) == 0)
            return null;
        // The pixmap includes the border; its origin is the outer top-left corner.
        var pixmapBounds = new PixelRect(x, y, (int)(width + 2 * border), (int)(height + 2 * border));
        var area = CaptureGeometry.Intersect(frame, pixmapBounds);
        if (area != frame) return null;
        var pixmap = XLib.XCompositeNameWindowPixmap(display.Handle, top);
        if (pixmap == 0) return null;
        try
        {
            XLib.XSync(display.Handle, false);
            var image = XLib.XGetImage(display.Handle, pixmap, frame.X - x, frame.Y - y, (uint)frame.Width,
                (uint)frame.Height, XLib.AllPlanes, XLib.ZPixmap);
            if (image == IntPtr.Zero) return null; // not redirected (BadMatch) or vanished
            try
            {
                var buffer = new PixelBuffer(frame.Width, frame.Height);
                CopyImage(image, buffer, 0, 0, opaque: depth != 32);
                return buffer;
            }
            finally { XLib.DestroyImage(image); }
        }
        finally
        {
            XLib.XFreePixmap(display.Handle, pixmap);
            XLib.XSync(display.Handle, false);
        }
    });

    private static bool? _composite;

    private static bool HasComposite(XDisplay display)
    {
        if (_composite is { } known) return known;
        try { _composite = XLib.XCompositeQueryExtension(display.Handle, out _, out _); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { _composite = false; }
        return _composite.Value;
    }
}

using System.Runtime.InteropServices;

namespace SnipFlow.Platform.Linux.X11;

/// <summary>
/// A passive key grab on the X11 root window, the X11 counterpart of RegisterHotKey.
/// </summary>
/// <remarks>
/// The grab runs on its own XCB connection, read by a dedicated thread that polls the connection's
/// file descriptor together with a wake pipe, so <see cref="Dispose"/> can stop it at once.
/// <para>
/// "Already in use" detection: XGrabKey reports BadAccess asynchronously through the process-wide
/// Xlib error handler, which Avalonia installs and SnipFlow must not replace. XCB's <c>*_checked</c>
/// requests instead return their error to the caller (<c>xcb_request_check</c>) and never reach that
/// handler, so each grab is confirmed synchronously and a BadAccess from another client is reported
/// as <see cref="Services.GlobalHotkeyService.ErrorInUse"/>. A replacement grabs the new key before
/// releasing the old one, so a failure keeps the previous shortcut working. libxcb is always present
/// with libX11; keysyms and lock-modifier masks are looked up with a short-lived Xlib connection.
/// </para>
/// </remarks>
internal sealed class X11HotkeyGrab : IDisposable
{
    readonly IntPtr connection;
    readonly uint root;
    readonly int wakeRead, wakeWrite;
    readonly Thread reader;
    readonly Action pressed;
    readonly object gate = new();
    volatile bool disposed;
    // Active grab: keycode plus the gesture's modifiers; every lock-key variant is grabbed too.
    byte activeKey;
    ushort activeModifiers;
    ushort[] activeVariants = Array.Empty<ushort>();
    uint lastReleaseTime;
    byte lastReleaseKey;

    X11HotkeyGrab(IntPtr connection, uint root, int wakeRead, int wakeWrite, Action pressed)
    {
        this.connection = connection;
        this.root = root;
        this.wakeRead = wakeRead;
        this.wakeWrite = wakeWrite;
        this.pressed = pressed;
        reader = new Thread(ReadEvents) { IsBackground = true, Name = "SnipFlow X11 hotkey" };
        reader.Start();
    }

    /// <summary>Opens the private connection, or returns null when no X server is reachable.</summary>
    public static unsafe X11HotkeyGrab? TryCreate(Action pressed)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        IntPtr connection;
        int screen;
        try { connection = Xcb.xcb_connect(IntPtr.Zero, out screen); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        if (connection == IntPtr.Zero) return null;
        if (Xcb.xcb_connection_has_error(connection) != 0) { Xcb.xcb_disconnect(connection); return null; }
        var iterator = Xcb.xcb_setup_roots_iterator(Xcb.xcb_get_setup(connection));
        for (int index = 0; index < screen && iterator.Remaining > 0; index++) Xcb.xcb_screen_next(ref iterator);
        if (iterator.Data == IntPtr.Zero) { Xcb.xcb_disconnect(connection); return null; }
        var root = *(uint*)iterator.Data; // xcb_screen_t.root is the first field.
        int* fds = stackalloc int[2];
        if (LibC.pipe(fds) != 0) { Xcb.xcb_disconnect(connection); return null; }
        LibC.fcntl(fds[0], 4 /* F_SETFL */, 0x800 /* O_NONBLOCK */);
        return new X11HotkeyGrab(connection, root, fds[0], fds[1], pressed);
    }

    /// <summary>
    /// Grabs <paramref name="keysym"/> with <paramref name="modifiers"/> (X11 masks). Returns 0, or the
    /// X error code (<see cref="Xcb.BadAccess"/> when another client owns the combination).
    /// </summary>
    public int TryGrab(uint keysym, uint modifiers)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryResolve(keysym, out var keycode, out var lockMasks)) return -1;
        var mods = (ushort)modifiers;
        var variants = LockVariants(lockMasks).Select(extra => (ushort)(mods | extra)).Distinct().ToArray();
        lock (gate)
        {
            if (keycode == activeKey && mods == activeModifiers && activeVariants.Length > 0) return 0;
            var cookies = variants.Select(variant => Xcb.xcb_grab_key_checked(connection, 1, root, variant, keycode,
                Xcb.GrabModeAsync, Xcb.GrabModeAsync)).ToArray();
            int failure = 0;
            var granted = new List<ushort>();
            for (int index = 0; index < cookies.Length; index++)
            {
                var error = Xcb.Check(connection, cookies[index]);
                if (error == 0) granted.Add(variants[index]);
                else if (failure == 0 || error == Xcb.BadAccess) failure = error;
            }
            if (failure != 0)
            {
                // A partial grab would work only with some lock keys; release it and keep the old shortcut.
                Ungrab(keycode, granted);
                Wake();
                return failure;
            }
            if (activeVariants.Length > 0) Ungrab(activeKey, activeVariants);
            activeKey = keycode; activeModifiers = mods; activeVariants = variants;
        }
        Wake();
        return 0;
    }

    public void Release()
    {
        if (disposed) return;
        lock (gate)
        {
            if (activeVariants.Length > 0) Ungrab(activeKey, activeVariants);
            activeKey = 0; activeModifiers = 0; activeVariants = Array.Empty<ushort>();
        }
        Wake();
    }

    void Ungrab(byte keycode, IEnumerable<ushort> variants)
    {
        foreach (var cookie in variants.Select(variant => Xcb.xcb_ungrab_key_checked(connection, keycode, root, variant)).ToArray())
            Xcb.Check(connection, cookie);
    }

    static IEnumerable<uint> LockVariants(uint[] lockMasks)
    {
        // Every combination of Caps Lock, Num Lock and Scroll Lock, so the shortcut works whatever lock is on.
        int count = 1 << lockMasks.Length;
        for (int combination = 0; combination < count; combination++)
        {
            uint mask = 0;
            for (int bit = 0; bit < lockMasks.Length; bit++)
                if ((combination & (1 << bit)) != 0) mask |= lockMasks[bit];
            yield return mask;
        }
    }

    static bool TryResolve(uint keysym, out byte keycode, out uint[] lockMasks)
    {
        keycode = 0; lockMasks = Array.Empty<uint>();
        using var display = XDisplay.TryOpen();
        if (display is null) return false;
        keycode = XLib.XKeysymToKeycode(display.Handle, (nint)keysym);
        if (keycode == 0) return false;
        lockMasks = new[] { XLib.LockMask, XLib.ModifierMaskForKeysym(display.Handle, XLib.XK_Num_Lock), XLib.ModifierMaskForKeysym(display.Handle, XLib.XK_Scroll_Lock) }
            .Where(mask => mask != 0).Distinct().ToArray();
        return true;
    }

    unsafe void Wake()
    {
        byte signal = 1;
        LibC.write(wakeWrite, &signal, 1);
    }

    unsafe void ReadEvents()
    {
        var fd = Xcb.xcb_get_file_descriptor(connection);
        var fds = stackalloc LibC.PollFd[2];
        var drain = stackalloc byte[64];
        while (!disposed)
        {
            fds[0] = new LibC.PollFd { Fd = fd, Events = LibC.PollIn };
            fds[1] = new LibC.PollFd { Fd = wakeRead, Events = LibC.PollIn };
            // Another thread waiting for a grab reply can move events into XCB's queue without waking
            // this poll; the wake pipe and the timeout make sure the queue is drained.
            LibC.poll(fds, 2, 1000);
            if (disposed) break;
            if ((fds[1].Returned & LibC.PollIn) != 0) while (LibC.read(wakeRead, drain, 64) > 0) { }
            if (Xcb.xcb_connection_has_error(connection) != 0) break;
            IntPtr item;
            while (!disposed && (item = Xcb.xcb_poll_for_event(connection)) != IntPtr.Zero)
            {
                try { Handle((byte*)item); }
                finally { LibC.free(item); }
            }
        }
    }

    unsafe void Handle(byte* item)
    {
        var type = (byte)(item[0] & 0x7F);
        if (type is not (Xcb.KeyPress or Xcb.KeyRelease)) return;
        var keycode = item[1];
        var time = *(uint*)(item + 4);
        var state = *(ushort*)(item + 28);
        if (type == Xcb.KeyRelease) { lastReleaseKey = keycode; lastReleaseTime = time; return; }
        // Without detectable auto-repeat, a held key repeats as Release+Press pairs with one timestamp.
        if (keycode == lastReleaseKey && time == lastReleaseTime) return;
        byte key; ushort modifiers;
        lock (gate) { key = activeKey; modifiers = activeModifiers; }
        const ushort relevant = (ushort)(XLib.ShiftMask | XLib.ControlMask | XLib.Mod1Mask);
        if (key != 0 && keycode == key && (state & relevant) == modifiers) pressed();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Wake();
        if (Thread.CurrentThread != reader) reader.Join(TimeSpan.FromSeconds(2));
        // Closing the connection also releases every grab it owns.
        Xcb.xcb_disconnect(connection);
        LibC.close(wakeRead); LibC.close(wakeWrite);
    }
}

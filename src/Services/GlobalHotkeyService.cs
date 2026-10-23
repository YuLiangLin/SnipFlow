namespace SnipFlow.Services;

/// <summary>How the global capture shortcut reaches SnipFlow on this desktop.</summary>
public enum HotkeyBackend
{
    /// <summary>Win32 RegisterHotKey on a message-only window.</summary>
    RegisterHotKey,
    /// <summary>XGrabKey on the X11 root window (X11 sessions).</summary>
    X11,
    /// <summary>org.freedesktop.portal.GlobalShortcuts (Wayland sessions whose portal offers it).</summary>
    Portal,
    /// <summary>No in-process shortcut. The desktop's own keyboard settings run <c>SnipFlow --capture</c>.</summary>
    Desktop,
}

/// <summary>
/// The system-wide capture shortcut. Windows registers it with RegisterHotKey; X11 sessions grab the
/// key on the root window; Wayland sessions ask the GlobalShortcuts portal. When none of these is
/// available, <see cref="TryRegister"/> reports <see cref="ErrorUnsupported"/> and the desktop's own
/// shortcut settings launch <c>SnipFlow --capture</c>, which the running instance receives over its pipe.
/// </summary>
public sealed partial class GlobalHotkeyService : IDisposable
{
    /// <summary>The combination is already taken by another program (Win32 error 1409, X11 BadAccess).</summary>
    public const int ErrorInUse = 1409;
    /// <summary>This session has no global shortcut API SnipFlow can use (for example Wayland without the portal).</summary>
    public const int ErrorUnsupported = -1;
    /// <summary>The desktop is still confirming the shortcut; <see cref="StateChanged"/> reports the outcome.</summary>
    public const int ErrorPending = -2;

    bool disposed;
    public HotkeyGesture? ActiveGesture { get; private set; }
    /// <summary>The trigger as the desktop describes it when it may differ from the requested gesture (portal), otherwise null.</summary>
    public string? ActiveDescription { get; private set; }
    /// <summary>The last asynchronous failure reported by the desktop (portal), translated; empty when none.</summary>
    public string PendingError { get; private set; } = "";
    public bool IsPending { get; private set; }
    public event EventHandler? Pressed;
    /// <summary>Raised on the UI thread after an asynchronous registration finished or the desktop changed the shortcut.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The mechanism this session uses. Portal availability is confirmed during registration.</summary>
    public static HotkeyBackend Backend => PlatformInfo.IsWindows ? HotkeyBackend.RegisterHotKey
        : PlatformInfo.IsX11 ? HotkeyBackend.X11
        : PlatformInfo.IsWayland ? HotkeyBackend.Portal : HotkeyBackend.Desktop;

    public GlobalHotkeyService()
    {
        Dispatcher.UIThread.VerifyAccess();
        InitializeNative();
    }

    public bool TryRegister(HotkeyGesture gesture, out int errorCode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Dispatcher.UIThread.VerifyAccess();
        errorCode = 0;
        if (ActiveGesture == gesture && !IsPending) return true;
        return TryRegisterNative(gesture, out errorCode);
    }

    void SetActive(HotkeyGesture? gesture, string? description = null)
    {
        ActiveGesture = gesture;
        ActiveDescription = description;
    }

    void SetPending(bool pending, string error = "")
    {
        IsPending = pending;
        PendingError = error;
    }

    void RaisePressed()
    {
        if (!disposed) Pressed?.Invoke(this, EventArgs.Empty);
    }

    void RaiseStateChanged()
    {
        if (!disposed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (disposed) return;
        Dispatcher.UIThread.VerifyAccess();
        disposed = true;
        DisposeNative();
        ActiveGesture = null; ActiveDescription = null; Pressed = null; StateChanged = null;
    }

    /// <summary>True once this session turned out to have no global shortcut API (see <see cref="ErrorUnsupported"/>).</summary>
    public bool IsUnsupported { get; private set; }

    /// <summary>
    /// Whether SnipFlow can write a desktop keyboard shortcut that runs <c>SnipFlow --capture</c>
    /// (GNOME custom shortcuts). Only done when the user asks for it in Settings.
    /// </summary>
    public static bool CanRegisterDesktopShortcut
    {
        get { var result = false; CanRegisterDesktopShortcutNative(ref result); return result; }
    }

    /// <summary>SnipFlow's desktop keyboard shortcut, or null when the desktop has none SnipFlow can read.</summary>
    public static Task<DesktopShortcut?> ReadDesktopShortcutAsync()
    {
        Task<DesktopShortcut?> result = Task.FromResult<DesktopShortcut?>(null);
        ReadDesktopShortcutNative(ref result);
        return result;
    }

    /// <summary>Creates or updates the desktop keyboard shortcut for <paramref name="gesture"/>.</summary>
    public static Task RegisterDesktopShortcutAsync(HotkeyGesture gesture)
    {
        Task? result = null;
        RegisterDesktopShortcutNative(gesture, ref result);
        return result ?? Task.FromException(new PlatformNotSupportedException());
    }

    partial void InitializeNative();
    private partial bool TryRegisterNative(HotkeyGesture gesture, out int errorCode);
    partial void DisposeNative();
    static partial void CanRegisterDesktopShortcutNative(ref bool result);
    static partial void ReadDesktopShortcutNative(ref Task<DesktopShortcut?> result);
    static partial void RegisterDesktopShortcutNative(HotkeyGesture gesture, ref Task? result);
}

/// <summary>SnipFlow's shortcut in the desktop's keyboard settings. <see cref="IsCurrent"/> is false when it starts another SnipFlow copy.</summary>
public sealed record DesktopShortcut(bool Registered, HotkeyGesture? Gesture, bool IsCurrent);

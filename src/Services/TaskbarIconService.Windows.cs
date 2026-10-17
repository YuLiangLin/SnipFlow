using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace SnipFlow.Services;

/// <summary>Refreshes the window icon after showing the workspace or recreating its taskbar button.</summary>
internal sealed class TaskbarIconService
{
    static readonly HashSet<Window> AttachedWindows = new();
    static bool shuttingDown;
    readonly Window window;
    readonly TaskbarIdentityService identity = new();
    readonly uint taskbarCreatedMessage;
    readonly Win32Properties.CustomWndProcHookCallback hook;
    IntPtr handle;
    bool hooked;
    bool queued;
    bool closed;
    bool shellNotified;
    bool identityApplied;

    TaskbarIconService(Window window)
    {
        this.window = window;
        hook = WindowMessage;
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
        window.Opened += SourceInitialized;
        window.PropertyChanged += VisibleChanged;
        window.Closed += Closed;
        if (window.TryGetPlatformHandle()?.Handle is { } existing && existing != IntPtr.Zero) SourceInitialized(window, EventArgs.Empty);
    }

    internal static void Attach(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!AttachedWindows.Add(window)) return;
        if (AttachedWindows.Count == 1) Dispatcher.UIThread.ShutdownStarted += (_, _) => shuttingDown = true;
        _ = new TaskbarIconService(window);
    }

    void SourceInitialized(object? sender, EventArgs args)
    {
        window.Opened -= SourceInitialized;
        if (hooked) return;
        handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        hooked = true;
        Win32Properties.AddWndProcHookCallback(window, hook);
        identityApplied = identity.TryApply(handle);
        QueueRefresh();
    }

    void VisibleChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.IsVisibleProperty && window.IsVisible) QueueRefresh();
    }

    IntPtr WindowMessage(IntPtr hwnd, uint message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (message == 0x0002 && identityApplied) // WM_DESTROY: clear only properties successfully owned by this service.
        {
            TaskbarIdentityService.Clear(hwnd);
            identityApplied = false;
        }
        if ((taskbarCreatedMessage != 0 && message == taskbarCreatedMessage) || message == 0x02E0)
            QueueRefresh();
        return IntPtr.Zero;
    }

    void QueueRefresh()
    {
        if (closed || queued || shuttingDown) return;
        queued = true;
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.ContextIdle);
    }

    void Refresh()
    {
        queued = false;
        if (closed || !window.IsVisible || handle == IntPtr.Zero) return;
        if (!identityApplied) identityApplied = identity.TryApply(handle);
        try
        {
            // Use a newly decoded ICO so Windows updates both native icon handles.
            using var stream = AssetLoader.Open(new Uri("avares://SnipFlow/Assets/SnipFlow-v3.ico"));
            window.Icon = new WindowIcon(stream);
            if (shellNotified || !identityApplied) return;
            shellNotified = true;
            foreach (var path in identity.NotificationPaths) NotifyIconItem(path);
        }
        catch (Exception exception)
        {
            SettingsStore.Log(exception);
        }
    }

    static void NotifyIconItem(string path)
    {
        if (File.Exists(path))
            SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero); // SHCNE_UPDATEITEM / SHCNF_PATHW.
    }

    void Closed(object? sender, EventArgs args)
    {
        closed = true;
        window.Opened -= SourceInitialized;
        window.PropertyChanged -= VisibleChanged;
        window.Closed -= Closed;
        if (hooked) Win32Properties.RemoveWndProcHookCallback(window, hook);
        hooked = false;
        handle = IntPtr.Zero;
        AttachedWindows.Remove(window);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    static extern uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern void SHChangeNotify(uint changeEvent, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string item, IntPtr other);
}

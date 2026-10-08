using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SnipFlow.Services;

/// <summary>Refreshes the window icon after showing the workspace or recreating its taskbar button.</summary>
internal sealed class TaskbarIconService
{
    static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(TaskbarIconService), new PropertyMetadata(false));
    readonly Window window;
    readonly TaskbarIdentityService identity = new();
    readonly uint taskbarCreatedMessage;
    HwndSource? source;
    bool queued;
    bool closed;
    bool shellNotified;
    bool identityApplied;

    TaskbarIconService(Window window)
    {
        this.window = window;
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
        window.SourceInitialized += SourceInitialized;
        window.IsVisibleChanged += VisibleChanged;
        window.Closed += Closed;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) SourceInitialized(window, EventArgs.Empty);
    }

    internal static void Attach(Window window)
    {
        window.VerifyAccess();
        if ((bool)window.GetValue(AttachedProperty)) return;
        window.SetValue(AttachedProperty, true);
        _ = new TaskbarIconService(window);
    }

    void SourceInitialized(object? sender, EventArgs args)
    {
        window.SourceInitialized -= SourceInitialized;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(WindowMessage);
        if (source is { IsDisposed: false }) identityApplied = identity.TryApply(source.Handle);
        QueueRefresh();
    }

    void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (window.IsVisible) QueueRefresh();
    }

    IntPtr WindowMessage(IntPtr handle, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (message == 0x0002 && identityApplied) // WM_DESTROY: clear only properties successfully owned by this service.
        {
            TaskbarIdentityService.Clear(handle);
            identityApplied = false;
        }
        if ((taskbarCreatedMessage != 0 && message == taskbarCreatedMessage) || message == 0x02E0)
            QueueRefresh();
        return IntPtr.Zero;
    }

    void QueueRefresh()
    {
        if (closed || queued || window.Dispatcher.HasShutdownStarted) return;
        queued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Refresh));
    }

    void Refresh()
    {
        queued = false;
        if (closed || !window.IsVisible || source is null || source.IsDisposed) return;
        if (!identityApplied) identityApplied = identity.TryApply(source.Handle);
        try
        {
            // Use a new decoded ICO frame so WPF updates both native icon handles.
            var icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/SnipFlow-v3.ico"),
                BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            icon.Freeze();
            window.SetCurrentValue(Window.IconProperty, icon);
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
        window.SourceInitialized -= SourceInitialized;
        window.IsVisibleChanged -= VisibleChanged;
        window.Closed -= Closed;
        if (source is { IsDisposed: false }) source.RemoveHook(WindowMessage);
        source = null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    static extern uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern void SHChangeNotify(uint changeEvent, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string item, IntPtr other);
}

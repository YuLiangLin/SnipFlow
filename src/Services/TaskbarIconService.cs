using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SnipFlow.Services;

/// <summary>Refreshes the window icon after showing the workspace or recreating its taskbar button.</summary>
internal sealed class TaskbarIconService
{
    readonly Window window;
    readonly uint taskbarCreatedMessage;
    HwndSource? source;
    bool queued;
    bool closed;
    bool shellNotified;

    TaskbarIconService(Window window)
    {
        this.window = window;
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
        window.SourceInitialized += SourceInitialized;
        window.IsVisibleChanged += VisibleChanged;
        window.Closed += Closed;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) SourceInitialized(window, EventArgs.Empty);
    }

    internal static void Attach(Window window) => _ = new TaskbarIconService(window);

    void SourceInitialized(object? sender, EventArgs args)
    {
        window.SourceInitialized -= SourceInitialized;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(WindowMessage);
        QueueRefresh();
    }

    void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (window.IsVisible) QueueRefresh();
    }

    IntPtr WindowMessage(IntPtr handle, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
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
        try
        {
            // Use a new decoded ICO frame so WPF updates both native icon handles.
            var icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/SnipFlow-v3.ico"),
                BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            icon.Freeze();
            window.SetCurrentValue(Window.IconProperty, icon);
            if (shellNotified) return;
            shellNotified = true;
            NotifyIconItem(Path.Combine(AppContext.BaseDirectory, "Assets", "SnipFlow-v3.ico"));
            if (Environment.ProcessPath is { } executable
                && Path.GetFileName(executable).Equals("SnipFlow.exe", StringComparison.OrdinalIgnoreCase))
                NotifyIconItem(executable);
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

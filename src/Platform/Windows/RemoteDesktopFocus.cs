using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SnipFlow.Capture;

namespace SnipFlow.Platform.Windows;

/// <summary>Listens for foreground window changes on the registering UI thread.</summary>
internal sealed class RemoteDesktopFocus : IDisposable
{
    const uint ForegroundEvent = 0x0003;
    const uint OutOfContext = 0;
    const uint QueryLimitedInformation = 0x1000;
    static readonly Dictionary<nint, Action> Callbacks = new();
    nint hook;

    internal unsafe RemoteDesktopFocus(Action changed)
    {
        hook = SetWinEventHook(ForegroundEvent, ForegroundEvent, 0, &ForegroundChanged, 0, 0, OutOfContext);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Callbacks.Add(hook, changed);
    }

    internal static unsafe bool IsRemoteClientFocused()
    {
        // An out-of-context notification can arrive after another focus change. Read the current window.
        var window = CaptureNative.GetForegroundWindow();
        if (window == 0) return false;
        var processId = CaptureNative.GetWindowProcessId(window);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return false;

        using var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process.IsInvalid) return false;
        Span<char> path = stackalloc char[512];
        var length = (uint)path.Length;
        fixed (char* buffer = path)
        {
            if (!QueryFullProcessImageName(process, 0, buffer, ref length)) return false;
        }
        return WindowsHotkeyBinding.IsRemoteClient(Path.GetFileName(path[..(int)length]).ToString());
    }

    [UnmanagedCallersOnly]
    static void ForegroundChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (!Callbacks.TryGetValue(hook, out var changed)) return;
        try { changed(); }
        catch (Exception error) { Dispatcher.UIThread.Post(() => SettingsStore.Log(error)); }
    }

    public void Dispose()
    {
        if (hook == 0) return;
        Callbacks.Remove(hook);
        UnhookWinEvent(hook);
        hook = 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern unsafe nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWinEvent(nint hook);

    [DllImport("kernel32.dll")]
    static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern unsafe bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* path, ref uint size);
}

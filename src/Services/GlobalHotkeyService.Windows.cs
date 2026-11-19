using System.ComponentModel;
using System.Runtime.InteropServices;
using SnipFlow.Platform.Windows;

namespace SnipFlow.Services;

public sealed partial class GlobalHotkeyService
{
    const uint WmHotkey = 0x0312;
    static readonly Dictionary<IntPtr, GlobalHotkeyService> MessageWindows = new();
    static ushort windowClass;
    IntPtr handle;
    WindowsHotkeyBinding binding = null!;
    RemoteDesktopFocus? remoteFocus;
    bool preferRemoteDesktop;
    bool focusObserverFailed;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    partial void InitializeNative()
    {
        // HWND_MESSAGE has no visual or focus. Its lifetime is independent of the editor window.
        // Avalonia's UI-thread message loop dispatches WM_HOTKEY to it.
        var instance = GetModuleHandle(null);
        if (windowClass == 0)
        {
            unsafe
            {
                fixed (char* name = "SnipFlow.GlobalHotkey")
                {
                    var registration = new WindowClass
                    {
                        Size = (uint)Marshal.SizeOf<WindowClass>(),
                        WindowProcedure = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ReceiveMessage,
                        Instance = instance,
                        ClassName = (IntPtr)name,
                    };
                    windowClass = RegisterClassEx(ref registration);
                }
            }
            if (windowClass == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        handle = CreateWindowEx(0, "SnipFlow.GlobalHotkey", "SnipFlow.GlobalHotkey", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        binding = new(
            (id, gesture) => RegisterHotKey(handle, id, gesture.NativeModifiers, gesture.VirtualKey) ? 0 : Marshal.GetLastPInvokeError(),
            id => UnregisterHotKey(handle, id) ? 0 : Marshal.GetLastPInvokeError());
        MessageWindows[handle] = this;
    }

    private partial bool TryRegisterNative(HotkeyGesture gesture, out int errorCode)
    {
        var yielding = ShouldYieldHotkey();
        if (!binding.TrySetGesture(gesture, yielding, out errorCode)) return false;
        PublishBindingState(0, yielding);
        return true;
    }

    partial void SetRemoteDesktopPriorityNative(bool enabled)
    {
        if (preferRemoteDesktop == enabled) return;
        preferRemoteDesktop = enabled;
        focusObserverFailed = false;
        if (enabled)
        {
            try { remoteFocus = new RemoteDesktopFocus(() => UpdateForeground(ShouldYieldHotkey())); }
            catch (Win32Exception error) { focusObserverFailed = true; SettingsStore.Log(error); }
        }
        else
        {
            remoteFocus?.Dispose();
            remoteFocus = null;
        }
        UpdateForeground(ShouldYieldHotkey());
    }

    bool ShouldYieldHotkey() => remoteFocus is not null && RemoteDesktopFocus.IsRemoteClientFocused();

    void UpdateForeground(bool yielding)
    {
        if (disposed) return;
        var previous = (ActiveGesture, IsPausedForRemoteDesktop, PendingError);
        PublishBindingState(binding.ForegroundChanged(yielding), yielding);
        // The Windows callback only updates the binding. Notify controls after returning to the message loop.
        if (previous != (ActiveGesture, IsPausedForRemoteDesktop, PendingError))
            Dispatcher.UIThread.Post(RaiseStateChanged);
    }

    void PublishBindingState(int error, bool yielding)
    {
        SetActive(binding.ActiveGesture);
        IsPausedForRemoteDesktop = binding.IsPaused;
        var message = "";
        if (error != 0)
        {
            if (yielding) message = I18n.F("無法暫停本機快捷鍵（Windows 錯誤 {0}）。", error);
            else if (error == ErrorInUse)
                message = I18n.F("{0} 已被其他程式使用；可透過按鈕或系統匣截圖。", binding.RequestedGesture);
            else message = I18n.F("{0} 無法啟用（Windows 錯誤 {1}）。", binding.RequestedGesture, error);
        }
        else if (focusObserverFailed) message = I18n.T("無法偵測遠端視窗焦點。請確認本機和遠端使用不同的快捷鍵。");
        SetPending(false, message);
    }

    [UnmanagedCallersOnly]
    static IntPtr ReceiveMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && MessageWindows.TryGetValue(hwnd, out var service)
            && service.binding.ActiveId is var id && id != 0 && wParam == (nint)id)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (service.disposed || !service.binding.Accepts(id)) return;
                if (service.ShouldYieldHotkey()) { service.UpdateForeground(yielding: true); return; }
                service.RaisePressed();
            });
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    partial void DisposeNative()
    {
        if (handle == IntPtr.Zero) return;
        remoteFocus?.Dispose();
        remoteFocus = null;
        binding.Release();
        MessageWindows.Remove(handle);
        DestroyWindow(handle);
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowClass
    {
        public uint Size, Style;
        public IntPtr WindowProcedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? moduleName);
}

using System.Runtime.InteropServices;

namespace SnipFlow.Services;

public sealed partial class GlobalHotkeyService
{
    const uint WmHotkey = 0x0312;
    static readonly Dictionary<IntPtr, GlobalHotkeyService> MessageWindows = new();
    static ushort windowClass;
    IntPtr handle;
    int activeId;

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
        MessageWindows[handle] = this;
    }

    private partial bool TryRegisterNative(HotkeyGesture gesture, out int errorCode)
    {
        errorCode = 0;
        int nextId = activeId == 1 ? 2 : 1;
        if (!RegisterHotKey(handle, nextId, gesture.NativeModifiers, gesture.VirtualKey))
        {
            errorCode = Marshal.GetLastWin32Error(); return false;
        }
        // A failed replacement must leave the previously registered shortcut usable.
        if (ActiveGesture != null && !UnregisterHotKey(handle, activeId))
        {
            errorCode = Marshal.GetLastWin32Error();
            UnregisterHotKey(handle, nextId); return false;
        }
        activeId = nextId; SetActive(gesture); return true;
    }

    [UnmanagedCallersOnly]
    static IntPtr ReceiveMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey && MessageWindows.TryGetValue(hwnd, out var service) && service.ActiveGesture != null
            && wParam.ToInt32() == service.activeId)
        {
            int receivedId = service.activeId;
            Dispatcher.UIThread.Post(() =>
            {
                if (service.activeId == receivedId) service.RaisePressed();
            });
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    partial void DisposeNative()
    {
        if (handle == IntPtr.Zero) return;
        if (ActiveGesture != null) UnregisterHotKey(handle, activeId);
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

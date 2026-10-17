using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SnipFlow.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    readonly HwndSource source;
    int activeId;
    bool disposed;
    public HotkeyGesture? ActiveGesture { get; private set; }
    public event EventHandler? Pressed;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public GlobalHotkeyService()
    {
        // HWND_MESSAGE has no visual or focus. Its lifetime is independent of the editor window.
        source = new HwndSource(new HwndSourceParameters("SnipFlow.GlobalHotkey")
        {
            ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0,
            HwndSourceHook = ReceiveMessage
        });
    }

    public bool TryRegister(HotkeyGesture gesture, out int errorCode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        source.Dispatcher.VerifyAccess();
        errorCode = 0;
        if (ActiveGesture == gesture) return true;
        int nextId = activeId == 1 ? 2 : 1;
        if (!RegisterHotKey(source.Handle, nextId, gesture.NativeModifiers, gesture.VirtualKey))
        {
            errorCode = Marshal.GetLastWin32Error(); return false;
        }
        // A failed replacement must leave the previously registered shortcut usable.
        if (ActiveGesture != null && !UnregisterHotKey(source.Handle, activeId))
        {
            errorCode = Marshal.GetLastWin32Error();
            UnregisterHotKey(source.Handle, nextId); return false;
        }
        activeId = nextId; ActiveGesture = gesture; return true;
    }

    IntPtr ReceiveMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && ActiveGesture != null && wParam.ToInt32() == activeId)
        {
            handled = true;
            int receivedId = activeId;
            source.Dispatcher.BeginInvoke(() =>
            {
                if (!disposed && activeId == receivedId) Pressed?.Invoke(this, EventArgs.Empty);
            });
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        source.Dispatcher.VerifyAccess(); disposed = true;
        if (ActiveGesture != null) UnregisterHotKey(source.Handle, activeId);
        ActiveGesture = null; Pressed = null; source.Dispose();
    }
}

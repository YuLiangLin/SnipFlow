namespace SnipFlow.Services;

/// <summary>Tracks the requested gesture separately from the shortcut currently reserved with Windows.</summary>
internal sealed class WindowsHotkeyBinding(Func<int, HotkeyGesture, int> register, Func<int, int> unregister)
{
    int lastId;
    internal HotkeyGesture? RequestedGesture { get; private set; }
    internal HotkeyGesture? ActiveGesture { get; private set; }
    internal int ActiveId { get; private set; }
    internal bool IsPaused { get; private set; }

    internal bool TrySetGesture(HotkeyGesture gesture, bool yieldToRemote, out int error)
    {
        error = Apply(gesture, yieldToRemote);
        if (error != 0) return false;
        RequestedGesture = gesture;
        IsPaused = yieldToRemote;
        return true;
    }

    internal int ForegroundChanged(bool yieldToRemote)
    {
        if (RequestedGesture is not { } gesture) return 0;
        var error = Apply(gesture, yieldToRemote);
        IsPaused = yieldToRemote && error == 0;
        return error;
    }

    int Apply(HotkeyGesture gesture, bool yieldToRemote)
    {
        if (yieldToRemote) return Release();
        if (ActiveGesture == gesture) return 0;

        // Use a fresh ID so a WM_HOTKEY queued for a released binding is ignored on resume.
        var id = lastId == 0xBFFF ? 1 : lastId + 1;
        lastId = id;
        var error = register(id, gesture);
        if (error != 0) return error;
        error = Release();
        if (error != 0)
        {
            unregister(id);
            return error;
        }
        ActiveId = id;
        ActiveGesture = gesture;
        return 0;
    }

    internal int Release()
    {
        if (ActiveId == 0) return 0;
        var error = unregister(ActiveId);
        if (error != 0) return error;
        ActiveId = 0;
        ActiveGesture = null;
        return 0;
    }

    internal bool Accepts(int id) => ActiveId != 0 && ActiveId == id;

    internal static bool IsRemoteClient(string? executable) => executable is not null
        && (executable.Equals("mRemoteNG.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("vncviewer.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("mstsc.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("msrdc.exe", StringComparison.OrdinalIgnoreCase));
}

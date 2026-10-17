using System.Windows.Input;

namespace SnipFlow.Services;

public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public const string DefaultShortcut = "Ctrl + Alt + S";

    public static bool TryCreate(ModifierKeys modifiers, Key key, out HotkeyGesture gesture)
    {
        gesture = default;
        if ((modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) != 0) return false;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        bool letterOrDigit = vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39;
        int modifierCount = ((modifiers & ModifierKeys.Control) != 0 ? 1 : 0)
            + ((modifiers & ModifierKeys.Alt) != 0 ? 1 : 0)
            + ((modifiers & ModifierKeys.Shift) != 0 ? 1 : 0);
        // Keep everyday typing and Ctrl+N/Ctrl+S available to the foreground app.
        if (!(letterOrDigit && modifierCount >= 2) && !(vk is >= 0x70 and <= 0x7A)) return false;
        gesture = new(modifiers, key); return true;
    }

    public static bool TryParse(string? shortcut, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = ModifierKeys.None;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModifierKeys.Control,
                "ALT" => ModifierKeys.Alt,
                "SHIFT" => ModifierKeys.Shift,
                _ => ModifierKeys.None
            };
            if (modifier == ModifierKeys.None || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        var keyName = parts[^1].ToUpperInvariant();
        if (keyName.Length == 1 && keyName[0] is >= '0' and <= '9') keyName = "D" + keyName;
        return Enum.TryParse<Key>(keyName, true, out var key) && Enum.IsDefined(key) && TryCreate(modifiers, key, out gesture);
    }

    public uint NativeModifiers => 0x4000u
        | ((Modifiers & ModifierKeys.Alt) != 0 ? 0x0001u : 0)
        | ((Modifiers & ModifierKeys.Control) != 0 ? 0x0002u : 0)
        | ((Modifiers & ModifierKeys.Shift) != 0 ? 0x0004u : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        var vk = VirtualKey;
        parts.Add(vk is >= 0x30 and <= 0x39 ? ((char)vk).ToString() : Key.ToString());
        return string.Join(" + ", parts);
    }
}

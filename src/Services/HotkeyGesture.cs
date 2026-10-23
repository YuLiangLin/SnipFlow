using Avalonia.Input;

namespace SnipFlow.Services;

/// <summary>A global capture shortcut: two or more of Ctrl/Alt/Shift plus a letter or digit, or F1–F11.</summary>
public readonly record struct HotkeyGesture(KeyModifiers Modifiers, Key Key)
{
    public const string DefaultShortcut = "Ctrl + Alt + S";
    const KeyModifiers Allowed = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;

    public static bool TryCreate(KeyModifiers modifiers, Key key, out HotkeyGesture gesture)
    {
        gesture = default;
        if ((modifiers & ~Allowed) != 0) return false;
        bool letterOrDigit = key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9;
        int modifierCount = ((modifiers & KeyModifiers.Control) != 0 ? 1 : 0)
            + ((modifiers & KeyModifiers.Alt) != 0 ? 1 : 0)
            + ((modifiers & KeyModifiers.Shift) != 0 ? 1 : 0);
        // Keep everyday typing and Ctrl+N/Ctrl+S available to the foreground app.
        if (!(letterOrDigit && modifierCount >= 2) && key is not (>= Key.F1 and <= Key.F11)) return false;
        gesture = new(modifiers, key);
        return true;
    }

    public static bool TryParse(string? shortcut, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Control,
                "ALT" => KeyModifiers.Alt,
                "SHIFT" => KeyModifiers.Shift,
                _ => KeyModifiers.None
            };
            if (modifier == KeyModifiers.None || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        var keyName = parts[^1].ToUpperInvariant();
        if (keyName.Length == 1 && keyName[0] is >= '0' and <= '9') keyName = "D" + keyName;
        return Enum.TryParse<Key>(keyName, true, out var key) && Enum.IsDefined(key) && TryCreate(modifiers, key, out gesture);
    }

    /// <summary>Win32 RegisterHotKey modifiers, including MOD_NOREPEAT.</summary>
    public uint NativeModifiers => 0x4000u
        | ((Modifiers & KeyModifiers.Alt) != 0 ? 0x0001u : 0)
        | ((Modifiers & KeyModifiers.Control) != 0 ? 0x0002u : 0)
        | ((Modifiers & KeyModifiers.Shift) != 0 ? 0x0004u : 0);

    /// <summary>Win32 virtual-key code.</summary>
    public uint VirtualKey => Key switch
    {
        >= Key.A and <= Key.Z => 0x41u + (uint)(Key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30u + (uint)(Key - Key.D0),
        >= Key.F1 and <= Key.F24 => 0x70u + (uint)(Key - Key.F1),
        _ => 0,
    };

    /// <summary>X11 keysym (lower-case letters, digits, XK_F1…).</summary>
    public uint X11KeySym => Key switch
    {
        >= Key.A and <= Key.Z => 0x61u + (uint)(Key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30u + (uint)(Key - Key.D0),
        >= Key.F1 and <= Key.F24 => 0xFFBEu + (uint)(Key - Key.F1),
        _ => 0,
    };

    /// <summary>GTK / GNOME accelerator syntax, for example "&lt;Control&gt;&lt;Alt&gt;s".</summary>
    public string GtkAccelerator
    {
        get
        {
            var text = "";
            if ((Modifiers & KeyModifiers.Control) != 0) text += "<Control>";
            if ((Modifiers & KeyModifiers.Alt) != 0) text += "<Alt>";
            if ((Modifiers & KeyModifiers.Shift) != 0) text += "<Shift>";
            return text + Key switch
            {
                >= Key.A and <= Key.Z => ((char)('a' + (Key - Key.A))).ToString(),
                >= Key.D0 and <= Key.D9 => ((char)('0' + (Key - Key.D0))).ToString(),
                _ => Key.ToString(),
            };
        }
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (Key - Key.D0))).ToString() : Key.ToString());
        return string.Join(" + ", parts);
    }
}

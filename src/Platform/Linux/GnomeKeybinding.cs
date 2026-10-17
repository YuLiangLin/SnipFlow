using System.Text;
using Avalonia.Input;

namespace SnipFlow.Platform.Linux;

/// <summary>SnipFlow's entry in GNOME's custom keyboard shortcuts, as read from gsettings.</summary>
internal sealed record GnomeShortcutState(bool Registered, HotkeyGesture? Gesture, string Binding, string Command, bool CommandIsCurrent);

/// <summary>
/// GNOME custom keyboard shortcut that runs <c>"&lt;launcher&gt;" --capture</c>. GNOME on Wayland (before the
/// GlobalShortcuts portal) lets no client grab keys, so its own media-keys daemon starts SnipFlow and the
/// running instance receives the capture request over its pipe. SnipFlow only writes these settings when
/// the user presses the button in Settings, and never touches other custom shortcuts.
/// </summary>
internal static class GnomeKeybinding
{
    const string Schema = "org.gnome.settings-daemon.plugins.media-keys";
    const string ListKey = "custom-keybindings";
    const string EntrySchema = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding";
    const string EntryPath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/snipflow/";
    static string Entry => EntrySchema + ":" + EntryPath;

    // Unknown until the first read; false when gsettings has no media-keys schema (no gnome-settings-daemon).
    static bool? schemaAvailable;

    /// <summary>GNOME (or Ubuntu's GNOME session) with gsettings and the media-keys schema available.</summary>
    public static bool IsAvailable => PlatformInfo.IsLinux && PlatformInfo.IsGnome && schemaAvailable != false && ExternalTool.IsAvailable("gsettings");

    /// <summary>The command GNOME runs for SnipFlow's shortcut.</summary>
    public static string CreateCommand(string launcher) => QuoteShellArgument(launcher) + " --capture";

    public static async Task<GnomeShortcutState?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;
        var list = await ReadListAsync(cancellationToken);
        schemaAvailable = list is not null;
        if (list is null) return null;
        if (!list.Contains(EntryPath, StringComparer.Ordinal)) return new(false, null, "", "", false);
        var binding = ParseString(await GetAsync(Entry, "binding", cancellationToken)) ?? "";
        var command = ParseString(await GetAsync(Entry, "command", cancellationToken)) ?? "";
        bool current;
        try { current = command == CreateCommand(StartupService.ResolveLauncher()); }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException) { current = command.Contains("--capture", StringComparison.Ordinal); }
        return new(true, ParseAccelerator(binding), binding, command, current);
    }

    /// <summary>Creates or updates SnipFlow's entry and adds it to the custom shortcut list.</summary>
    public static async Task RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken = default)
    {
        var command = CreateCommand(StartupService.ResolveLauncher());
        // Write the entry first, then publish it in the list, so GNOME never sees a half-made shortcut.
        await SetAsync(Entry, "name", QuoteVariantString("SnipFlow"), cancellationToken);
        await SetAsync(Entry, "command", QuoteVariantString(command), cancellationToken);
        await SetAsync(Entry, "binding", QuoteVariantString(gesture.GtkAccelerator), cancellationToken);
        // Read the list again right before writing it; other shortcuts are kept exactly as they are.
        var list = await ReadListAsync(cancellationToken) ?? throw new InvalidOperationException(I18n.T("無法讀取 GNOME 鍵盤快捷鍵設定。"));
        if (!list.Contains(EntryPath, StringComparer.Ordinal))
            await SetAsync(Schema, ListKey, FormatList(list.Append(EntryPath)), cancellationToken);
    }

    static async Task<List<string>?> ReadListAsync(CancellationToken cancellationToken)
    {
        var text = await GetAsync(Schema, ListKey, cancellationToken);
        return text is null ? null : ParseStringArray(text);
    }

    static async Task<string?> GetAsync(string schema, string key, CancellationToken cancellationToken)
    {
        var result = await ExternalTool.RunAsync("gsettings", new[] { "get", schema, key }, cancellationToken, timeout: TimeSpan.FromSeconds(10));
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    static async Task SetAsync(string schema, string key, string value, CancellationToken cancellationToken)
    {
        var result = await ExternalTool.RunAsync("gsettings", new[] { "set", schema, key, value }, cancellationToken, timeout: TimeSpan.FromSeconds(10));
        if (!result.Succeeded)
            throw new IOException(I18n.F("GNOME 鍵盤快捷鍵未更新：{0}", result.StandardError.Trim()));
    }

    /// <summary>Quotes one argument for GLib's g_shell_parse_argv, which GNOME uses to run the command.</summary>
    static string QuoteShellArgument(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '"' or '\\' or '$' or '`') builder.Append('\\');
            builder.Append(character);
        }
        return builder.Append('"').ToString();
    }

    /// <summary>GVariant text for a string, as gsettings expects ('…' with backslash escapes).</summary>
    static string QuoteVariantString(string value)
        => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    static string FormatList(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(QuoteVariantString)) + "]";

    /// <summary>Parses gsettings' GVariant text of an array of strings, such as "['/a/', '/b/']" or "@as []".</summary>
    internal static List<string> ParseStringArray(string text)
    {
        var result = new List<string>();
        var span = text.Trim();
        if (span.StartsWith("@as", StringComparison.Ordinal)) span = span[3..].Trim();
        if (!span.StartsWith('[') || !span.EndsWith(']')) return result;
        var index = 1;
        while (index < span.Length - 1)
        {
            var character = span[index];
            if (character is '\'' or '"')
            {
                var (value, next) = ReadQuoted(span, index);
                result.Add(value);
                index = next;
            }
            else index++;
        }
        return result;
    }

    /// <summary>Parses a GVariant string literal ('…' or "…"); returns null for anything else.</summary>
    internal static string? ParseString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed[0] is '\'' or '"' ? ReadQuoted(trimmed, 0).Value : null;
    }

    static (string Value, int Next) ReadQuoted(string text, int start)
    {
        var quote = text[start];
        var builder = new StringBuilder();
        var index = start + 1;
        while (index < text.Length && text[index] != quote)
        {
            if (text[index] == '\\' && index + 1 < text.Length) index++;
            builder.Append(text[index]);
            index++;
        }
        return (builder.ToString(), index + 1);
    }

    /// <summary>Converts a GTK accelerator such as "&lt;Control&gt;&lt;Alt&gt;s" to a gesture, or null.</summary>
    internal static HotkeyGesture? ParseAccelerator(string accelerator)
    {
        var modifiers = KeyModifiers.None;
        var rest = accelerator.Trim();
        while (rest.StartsWith('<'))
        {
            var close = rest.IndexOf('>');
            if (close < 0) return null;
            var name = rest[1..close].ToLowerInvariant();
            modifiers |= name switch
            {
                "control" or "ctrl" or "primary" or "ctl" => KeyModifiers.Control,
                "alt" or "mod1" => KeyModifiers.Alt,
                "shift" => KeyModifiers.Shift,
                _ => KeyModifiers.Meta,
            };
            rest = rest[(close + 1)..];
        }
        if (rest.Length == 1 && char.IsAsciiLetterOrDigit(rest[0]))
            rest = char.IsAsciiDigit(rest[0]) ? "D" + rest : rest.ToUpperInvariant();
        return Enum.TryParse<Key>(rest, true, out var key) && HotkeyGesture.TryCreate(modifiers, key, out var gesture) ? gesture : null;
    }
}

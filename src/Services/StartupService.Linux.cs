using System.Text;

namespace SnipFlow.Services;

public static partial class StartupService
{
    const string DesktopFileName = "snipflow.desktop";

    static string AutostartPath => Path.Combine(LinuxPaths.ConfigHome, "autostart", DesktopFileName);

    static StartupRegistration ReadRegistrationCore()
    {
        var path = AutostartPath;
        if (!File.Exists(path)) return new(null, null, false);
        var text = File.ReadAllText(path);
        return new(text, null, IsBackgroundEntry(text));
    }

    static string CreateCommandCore(string launcher) => $"{QuoteExecArgument(launcher)} --background";

    /// <summary>
    /// The program the login entry starts: the AppImage itself when running from one (the mounted
    /// copy changes on every launch), otherwise the current executable.
    /// </summary>
    internal static string ResolveLauncher()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && Path.IsPathFullyQualified(appImage) && File.Exists(appImage))
            return Path.GetFullPath(appImage);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !string.Equals(Path.GetFileName(executable), "SnipFlow", StringComparison.Ordinal))
            throw new InvalidOperationException(I18n.T("目前執行的是預覽程式，無法設定登入啟動。"));
        if (!File.Exists(executable)) throw MissingExecutable(executable);
        return Path.GetFullPath(executable);
    }

    static void WriteCommandCore(string? command)
    {
        var path = AutostartPath;
        if (command is null)
        {
            if (File.Exists(path) && IsOwnEntry(File.ReadAllText(path))) File.Delete(path);
            return;
        }
        WriteAtomically(path, CreateDesktopEntry(command));
    }

    static void RestoreCore(StartupRegistration previous)
    {
        var path = AutostartPath;
        if (previous.Value is string text) WriteAtomically(path, text);
        else if (File.Exists(path)) File.Delete(path);
    }

    static string CreateDesktopEntry(string command) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Name=SnipFlow",
        "Comment=" + I18n.T("截圖、標註與螢幕錄影"),
        "Exec=" + command,
        "Icon=snipflow",
        "Terminal=false",
        "X-GNOME-Autostart-enabled=true",
        "X-SnipFlow-Autostart=true",
        "") ;

    static bool IsOwnEntry(string text) => text.Contains("X-SnipFlow-Autostart=true", StringComparison.Ordinal);

    static bool IsBackgroundEntry(string text)
    {
        if (!IsOwnEntry(text)) return false;
        var exec = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Exec=", StringComparison.Ordinal));
        if (exec is null) return false;
        var arguments = SplitExec(exec[5..]);
        return arguments.Count == 2 && arguments[1] == "--background"
            && Path.IsPathFullyQualified(arguments[0]) && File.Exists(arguments[0]);
    }

    static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Quotes one argument for a Desktop Entry Exec key.</summary>
    internal static string QuoteExecArgument(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '"' or '`' or '$' or '\\') builder.Append('\\');
            if (character == '%') builder.Append('%');
            builder.Append(character);
        }
        return builder.Append('"').ToString();
    }

    static List<string> SplitExec(string exec)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        for (var i = 0; i < exec.Length; i++)
        {
            var character = exec[i];
            if (quoted && character == '\\' && i + 1 < exec.Length) { current.Append(exec[++i]); continue; }
            if (character == '%' && i + 1 < exec.Length && exec[i + 1] == '%') { current.Append('%'); i++; any = true; continue; }
            if (character == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(character))
            {
                if (any) result.Add(current.ToString());
                current.Clear(); any = false;
                continue;
            }
            current.Append(character); any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
    }
}

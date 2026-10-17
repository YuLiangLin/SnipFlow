namespace SnipFlow.Platform;

/// <summary>The desktop SnipFlow runs on. Linux sessions are X11, or Wayland with SnipFlow on XWayland.</summary>
public static class PlatformInfo
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();
    public static bool IsLinux { get; } = OperatingSystem.IsLinux();

    /// <summary>
    /// True in a Wayland session. SnipFlow's own windows still use X11 through XWayland, but screen
    /// pixels, other windows and global shortcuts are only reachable through xdg-desktop-portal.
    /// </summary>
    public static bool IsWayland { get; } = IsLinux && DetectWayland();

    /// <summary>True in a plain X11 session, where the X server exposes screens and windows directly.</summary>
    public static bool IsX11 { get; } = IsLinux && !IsWayland && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>XDG_CURRENT_DESKTOP entries, such as "ubuntu", "GNOME", "KDE".</summary>
    public static IReadOnlyList<string> DesktopEnvironments { get; } =
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsGnome => DesktopEnvironments.Any(name => name.Equals("GNOME", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ubuntu", StringComparison.OrdinalIgnoreCase) || name.Equals("Unity", StringComparison.OrdinalIgnoreCase));

    public static bool IsKde => DesktopEnvironments.Any(name => name.Equals("KDE", StringComparison.OrdinalIgnoreCase));

    /// <summary>File system path comparison for the current platform.</summary>
    public static StringComparison PathComparison { get; } = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer PathComparer { get; } = IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Executable name of SnipFlow on this platform.</summary>
    public static string ExecutableName => IsWindows ? "SnipFlow.exe" : "SnipFlow";

    static bool DetectWayland()
    {
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(session, "x11", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }
}

/// <summary>XDG base directories (Linux). Values fall back to the specification defaults.</summary>
public static class LinuxPaths
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DataHome => FromEnvironment("XDG_DATA_HOME", Path.Combine(Home, ".local", "share"));
    public static string ConfigHome => FromEnvironment("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));
    public static string CacheHome => FromEnvironment("XDG_CACHE_HOME", Path.Combine(Home, ".cache"));

    /// <summary>$XDG_RUNTIME_DIR, or a private folder under the cache directory when it is missing.</summary>
    public static string RuntimeDirectory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime) && Path.IsPathFullyQualified(runtime) && Directory.Exists(runtime)) return runtime;
            var fallback = Path.Combine(CacheHome, "SnipFlow", "run");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    static string FromEnvironment(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : fallback;
    }
}

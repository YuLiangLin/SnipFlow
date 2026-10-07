using System.Text.Json;
namespace SnipFlow.Services;

public sealed class UserSettings
{
    public string Language { get; set; } = "zh-TW";
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = true;
    public bool CopyAfterCapture { get; set; } = true;
    public bool AutoSaveCaptures { get; set; } = true;
    public string CaptureSaveDirectory { get; set; } = SettingsStore.DefaultCaptureSaveDirectory;
    public bool KeepHistory { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string Hotkey { get; set; } = HotkeyGesture.DefaultShortcut;
    public int HistoryLimit { get; set; } = 80;
    public List<string> RecentColors { get; set; } = new();
}

public static class SettingsStore
{
    // Outside Velopack's replaced install directory. Captures never enter the repository.
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipFlowData");
    public static string HistoryDirectory => Path.Combine(DataRoot, "History");
    public static string DefaultCaptureSaveDirectory
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "SnipFlow");
        }
    }
    public static UserSettings Current { get; private set; } = Load();
    public static event EventHandler? Changed;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static UserSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(DataRoot, "settings.json"))) ?? new();
            settings.RecentColors = (settings.RecentColors ?? new()).Where(value =>
                value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit))
                .Select(value => value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(8).ToList();
            try { settings.CaptureSaveDirectory = NormalizeCaptureSaveDirectory(settings.CaptureSaveDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            { settings.CaptureSaveDirectory = DefaultCaptureSaveDirectory; }
            RefreshStartupPreference(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            var settings = new UserSettings(); RefreshStartupPreference(settings); return settings;
        }
    }
    static void RefreshStartupPreference(UserSettings settings)
    {
        try { settings.StartWithWindows = StartupService.ReadRegistration().IsEnabled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
    public static string NormalizeCaptureSaveDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var expanded = Environment.ExpandEnvironmentVariables(directory.Trim());
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException("The capture folder must be an absolute path.", nameof(directory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }
    public static void Save()
    {
        RefreshStartupPreference(Current);
        Directory.CreateDirectory(DataRoot);
        var path = Path.Combine(DataRoot, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(path + ".tmp", path, true);
        // A listener failure must not turn a completed disk write into a failed save.
        if (Changed is { } listeners)
            foreach (EventHandler listener in listeners.GetInvocationList())
                try { listener(null, EventArgs.Empty); }
                catch (Exception ex) { Log(ex); }
    }
    public static void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            var path = Path.Combine(DataRoot, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

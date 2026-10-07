using System.Text.Json;
namespace SnipFlow.Services;

public sealed class UserSettings
{
    public string Language { get; set; } = "zh-TW";
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = true;
    public bool CopyAfterCapture { get; set; } = true;
    public bool KeepHistory { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string Hotkey { get; set; } = HotkeyGesture.DefaultShortcut;
    public int HistoryLimit { get; set; } = 80;
}

public static class SettingsStore
{
    // Outside Velopack's replaced install directory. Captures never enter the repository.
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipFlowData");
    public static string HistoryDirectory => Path.Combine(DataRoot, "History");
    public static UserSettings Current { get; private set; } = Load();
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static UserSettings Load()
    {
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(DataRoot, "settings.json"))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static void Save()
    {
        Directory.CreateDirectory(DataRoot);
        var path = Path.Combine(DataRoot, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(path + ".tmp", path, true);
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

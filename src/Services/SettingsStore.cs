using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SnipFlow.Editor;
namespace SnipFlow.Services;

public sealed class UserSettings
{
    public string Language { get; set; } = "zh-TW";
    public string Theme { get; set; } = "dark";
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = true;
    public bool CopyAfterCapture { get; set; } = true;
    public bool AutoSaveCaptures { get; set; } = true;
    public string CaptureSaveDirectory { get; set; } = SettingsStore.DefaultCaptureSaveDirectory;
    public bool KeepHistory { get; set; } = true;
    // Login start on every platform; the JSON name is kept so existing Windows settings still load.
    public bool StartWithWindows { get; set; }
    public string Hotkey { get; set; } = HotkeyGesture.DefaultShortcut;
    public int HistoryLimit { get; set; } = 80;
    public List<string> RecentColors { get; set; } = new();
    public ArrowStyleOptions ArrowStyle { get; set; } = ArrowStyleOptions.Default;
    public string ArrowColor { get; set; } = "#63D5C5";
    public double ArrowStrokeWidth { get; set; } = 4;
    // The settings-file codec reads entries individually so one malformed tool cannot discard
    // other preferences. It also excludes Arrow, whose original fields remain authoritative.
    [JsonIgnore]
    public Dictionary<AnnotationTool, ToolAppearanceOptions> ToolAppearances { get; set; } = new();

    public ToolAppearanceOptions GetToolAppearance(AnnotationTool tool)
    {
        if (!ToolAppearanceOptions.Supports(tool)) throw new ArgumentOutOfRangeException(nameof(tool));
        var appearance = tool == AnnotationTool.Arrow
            ? new ToolAppearanceOptions { Color = ArrowColor, StrokeWidth = ArrowStrokeWidth }
            : ToolAppearances?.GetValueOrDefault(tool);
        var color = ToolAppearanceOptions.TryNormalizeColor(appearance?.Color, out var normalized)
            ? normalized : ToolAppearanceOptions.Default.Color;
        var width = appearance is not null && double.IsFinite(appearance.StrokeWidth) && appearance.StrokeWidth is >= 1 and <= 32
            ? appearance.StrokeWidth : ToolAppearanceOptions.Default.StrokeWidth;
        return new ToolAppearanceOptions { Color = color, StrokeWidth = width };
    }

    public void SetToolAppearance(AnnotationTool tool, ToolAppearanceOptions appearance)
    {
        if (!ToolAppearanceOptions.Supports(tool)) throw new ArgumentOutOfRangeException(nameof(tool));
        if (!ToolAppearanceOptions.IsValid(appearance)) throw new ArgumentException("Tool appearance must contain an opaque #RRGGBB color and a finite stroke width between 1 and 32.", nameof(appearance));
        ToolAppearanceOptions.TryNormalizeColor(appearance.Color, out var color);
        if (tool == AnnotationTool.Arrow)
        {
            ArrowColor = color;
            ArrowStrokeWidth = appearance.StrokeWidth;
        }
        else (ToolAppearances ??= new())[tool] = appearance with { Color = color };
    }
}

public static class SettingsStore
{
    // Outside Velopack's replaced install directory. Captures never enter the repository.
    // Windows: %LocalAppData%\SnipFlowData. Linux: $XDG_DATA_HOME/SnipFlow (~/.local/share/SnipFlow).
    public static string DataRoot => PlatformInfo.IsWindows
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipFlowData")
        : Path.Combine(LinuxPaths.DataHome, "SnipFlow");
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
    private static readonly Lazy<UserSettings> LoadedSettings = new(Load);
    public static UserSettings Current => LoadedSettings.Value;
    public static event EventHandler? Changed;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static readonly JsonSerializerOptions ArrowJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    static UserSettings Load()
    {
        try
        {
            var settings = ReadSettingsFile(Path.Combine(DataRoot, "settings.json"));
            RefreshStartupPreference(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            var settings = new UserSettings(); RefreshStartupPreference(settings); return settings;
        }
    }
    // These file operations do not load Current or inspect startup registration. They also let
    // isolated regression processes exercise the actual settings format without touching user data.
    internal static UserSettings ReadSettingsFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Settings must be an object.");
        JsonElement arrow = default, arrowColor = default, arrowStrokeWidth = default, toolAppearances = default;
        using var otherSettings = new MemoryStream();
        using (var writer = new Utf8JsonWriter(otherSettings))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(nameof(UserSettings.ArrowStyle))) arrow = property.Value;
                else if (property.NameEquals(nameof(UserSettings.ArrowColor))) arrowColor = property.Value;
                else if (property.NameEquals(nameof(UserSettings.ArrowStrokeWidth))) arrowStrokeWidth = property.Value;
                else if (property.NameEquals(nameof(UserSettings.ToolAppearances))) toolAppearances = property.Value;
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        // Keep the previous serializer's last-value-wins behavior for repeated settings keys.
        var settings = JsonSerializer.Deserialize<UserSettings>(otherSettings.ToArray(), JsonOptions) ?? new();
        // A malformed arrow preference must not discard unrelated user preferences.
        try
        {
            var style = arrow.ValueKind == JsonValueKind.Undefined ? null : arrow.Deserialize<ArrowStyleOptions>(ArrowJsonOptions);
            settings.ArrowStyle = ArrowStyleOptions.IsValid(style) ? style! : ArrowStyleOptions.Default;
        }
        catch (JsonException) { settings.ArrowStyle = ArrowStyleOptions.Default; }
        if (arrowColor.ValueKind == JsonValueKind.String && TryNormalizeArrowColor(arrowColor.GetString(), out var color))
            settings.ArrowColor = color;
        if (arrowStrokeWidth.ValueKind == JsonValueKind.Number && arrowStrokeWidth.TryGetDouble(out var width)
            && IsValidArrowStrokeWidth(width))
            settings.ArrowStrokeWidth = width;
        settings.ToolAppearances = ReadToolAppearances(toolAppearances);
        settings.Theme = ThemeService.NormalizeMode(settings.Theme);
        settings.RecentColors = (settings.RecentColors ?? new()).Where(value =>
            value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit))
            .Select(value => value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(8).ToList();
        try { settings.CaptureSaveDirectory = NormalizeCaptureSaveDirectory(settings.CaptureSaveDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { settings.CaptureSaveDirectory = DefaultCaptureSaveDirectory; }
        return settings;
    }

    internal static void WriteSettingsFile(string path, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Reject invalid caller data before serializing or creating a temporary file. In particular,
        // NaN/Infinity must not fail halfway through a settings publication.
        if (!TryNormalizeArrowColor(settings.ArrowColor, out var color))
            throw new ArgumentException("Arrow color must be an opaque #RRGGBB value.", nameof(settings));
        if (!IsValidArrowStrokeWidth(settings.ArrowStrokeWidth))
            throw new ArgumentOutOfRangeException(nameof(settings), "Arrow stroke width must be finite and between 1 and 32.");
        var tools = new JsonObject();
        if (settings.ToolAppearances is { } appearances)
            foreach (var (tool, appearance) in appearances.OrderBy(pair => pair.Key))
            {
                // Match the reader: Arrow cannot shadow its original fields, and unsupported
                // entries never become preferences for tools without drawing appearance.
                if (tool == AnnotationTool.Arrow || !ToolAppearanceOptions.Supports(tool)) continue;
                if (!ToolAppearanceOptions.IsValid(appearance))
                    throw new ArgumentException("Tool appearance must contain an opaque #RRGGBB color and a finite stroke width between 1 and 32.", nameof(settings));
                ToolAppearanceOptions.TryNormalizeColor(appearance.Color, out var toolColor);
                tools[tool.ToString()] = new JsonObject { [nameof(ToolAppearanceOptions.Color)] = toolColor, [nameof(ToolAppearanceOptions.StrokeWidth)] = appearance.StrokeWidth };
            }
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        var style = ArrowStyleOptions.IsValid(settings.ArrowStyle) ? settings.ArrowStyle : ArrowStyleOptions.Default;
        json[nameof(UserSettings.ArrowStyle)] = JsonSerializer.SerializeToNode(style, ArrowJsonOptions);
        json[nameof(UserSettings.ArrowColor)] = color;
        json[nameof(UserSettings.ArrowStrokeWidth)] = settings.ArrowStrokeWidth;
        json[nameof(UserSettings.ToolAppearances)] = tools;
        File.WriteAllText(path + ".tmp", json.ToJsonString(JsonOptions));
        File.Move(path + ".tmp", path, true);
    }
    static bool TryNormalizeArrowColor(string? value, out string color) => ToolAppearanceOptions.TryNormalizeColor(value, out color);
    static bool IsValidArrowStrokeWidth(double width) => double.IsFinite(width) && width is >= 1 and <= 32;
    static Dictionary<AnnotationTool, ToolAppearanceOptions> ReadToolAppearances(JsonElement element)
    {
        var result = new Dictionary<AnnotationTool, ToolAppearanceOptions>();
        if (element.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in element.EnumerateObject())
        {
            if (!Enum.TryParse<AnnotationTool>(property.Name, out var tool) || tool.ToString() != property.Name
                || tool == AnnotationTool.Arrow || !ToolAppearanceOptions.Supports(tool)) continue;
            JsonElement color = default, width = default;
            if (property.Value.ValueKind == JsonValueKind.Object)
                foreach (var field in property.Value.EnumerateObject())
                {
                    if (field.NameEquals(nameof(ToolAppearanceOptions.Color))) color = field.Value;
                    else if (field.NameEquals(nameof(ToolAppearanceOptions.StrokeWidth))) width = field.Value;
                }
            var appearance = ToolAppearanceOptions.Default;
            if (color.ValueKind == JsonValueKind.String && ToolAppearanceOptions.TryNormalizeColor(color.GetString(), out var normalized))
                appearance = appearance with { Color = normalized };
            if (width.ValueKind == JsonValueKind.Number && width.TryGetDouble(out var strokeWidth) && IsValidArrowStrokeWidth(strokeWidth))
                appearance = appearance with { StrokeWidth = strokeWidth };
            result[tool] = appearance;
        }
        return result;
    }
    static void RefreshStartupPreference(UserSettings settings)
    {
        try { settings.StartWithWindows = StartupService.ReadRegistration().IsEnabled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or InvalidOperationException or PlatformNotSupportedException) { }
    }
    public static string NormalizeCaptureSaveDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var expanded = Environment.ExpandEnvironmentVariables(directory.Trim());
        if (!PlatformInfo.IsWindows && (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal)))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded.Length > 2 ? expanded[2..] : "");
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException("The capture folder must be an absolute path.", nameof(directory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }
    public static void Save()
    {
        RefreshStartupPreference(Current);
        WriteSettingsFile(Path.Combine(DataRoot, "settings.json"), Current);
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

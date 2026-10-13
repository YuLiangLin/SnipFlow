using Microsoft.Win32;
using System.ComponentModel;
using System.Security;
using System.Windows.Data;
using System.Windows.Interop;

namespace SnipFlow.Services;

/// <summary>UI colors only. Captured pixels, annotation colors and export palettes are independent.</summary>
public static class ThemeService
{
    // Every brush, including copies made by WPF styles, binds to the same explicit
    // source. Update that source rather than coercing expression-backed brush values.
    static readonly (string Key, string Dark, string Light)[] Palette =
    [
        ("Bg", "#181818", "#F7F7F5"),
        ("Panel", "#202020", "#F0F0EE"),
        ("Raised", "#2B2B2B", "#E8E8E6"),
        ("Line", "#383838", "#D6D6D1"),
        ("Ink", "#ECECEC", "#202020"),
        ("Muted", "#A3A3A3", "#676763"),
        ("Accent", "#E8E8E8", "#202020"),
        ("AccentInk", "#191919", "#FFFFFF"),
        ("Hover", "#303030", "#E2E2DF"),
        ("Selected", "#333333", "#DEDEDA"),
        ("Soft", "#242424", "#F5F5F3"),
        ("PrimaryHover", "#FFFFFF", "#343434"),
        ("PrimaryPressed", "#CFCFCF", "#4E4E4E"),
        ("TextSelection", "#626262", "#B6D0EC"),
        ("ControlBorder", "#6B6B6B", "#9D9D97"),
        ("SwitchOff", "#555555", "#CDCDCA"),
        ("ScrollGrip", "#646464", "#B9B9B3"),
        ("ScrollGripHover", "#858585", "#96968F"),
        ("ScrollGripActive", "#A3A3A3", "#777771"),
        ("TrackFilled", "#BDBDBD", "#575753"),
        ("TrackEmpty", "#4C4C4C", "#D0D0CB"),
        ("Error", "#F29D98", "#B42318"),
        ("Warning", "#EAC88D", "#835C12"),
        ("SelectionBorder", "#B8B8B8", "#2267A5"),
        ("SelectionHandle", "#ECECEC", "#FFFFFF"),
        ("SelectionFill", "#204AA5FF", "#302267A5"),
        ("Badge", "#D9242424", "#F2F0F0EE"),
        ("OverlayPanel", "#F2202020", "#F7F0F0EE"),
        ("OverlayInk", "#ECECEC", "#202020"),
        ("OverlayMuted", "#A3A3A3", "#60605C"),
        ("OverlayLine", "#949494", "#9D9D97"),
    ];
    public static ThemePalette Colors { get; } = new(CreateColors(dark: true));
    static bool initialized;
    static bool observingSystem;
    static bool hasPalette;

    public static bool IsDark { get; private set; } = true;
    public static event EventHandler? Changed;

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "system" => "system",
        _ => "dark",
    };

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static void Initialize()
    {
        if (initialized) return;
        Application.Current.Dispatcher.VerifyAccess();
        initialized = true;
        Apply();
        SettingsStore.Changed += SettingsChanged;
        try
        {
            SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
            observingSystem = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            SettingsStore.Log(ex);
        }
    }

    public static void Shutdown()
    {
        initialized = false;
        SettingsStore.Changed -= SettingsChanged;
        if (observingSystem) SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        observingSystem = false;
    }

    static void SettingsChanged(object? sender, EventArgs e) => RefreshOnDispatcher();

    static void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows broadcasts application theme changes as user preference changes.
        // Resolve the preference on the UI thread; never write system settings.
        RefreshOnDispatcher(onlyForSystem: true);
    }

    static void RefreshOnDispatcher(bool onlyForSystem = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        void Refresh()
        {
            if (!initialized || (onlyForSystem && NormalizeMode(SettingsStore.Current.Theme) != "system")) return;
            Apply();
        }
        if (dispatcher.CheckAccess()) Refresh();
        else dispatcher.BeginInvoke(Refresh);
    }

    static void Apply()
    {
        var mode = NormalizeMode(SettingsStore.Current.Theme);
        var dark = mode == "dark" || (mode == "system" && SystemUsesDarkAppTheme());
        if (hasPalette && IsDark == dark) return;
        IsDark = dark;
        Colors.Update(CreateColors(dark));
        hasPalette = true;

        foreach (Window window in Application.Current.Windows)
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) WindowAppearance.Apply(window);

        if (Changed is { } listeners)
            foreach (EventHandler listener in listeners.GetInvocationList())
                try { listener(null, EventArgs.Empty); }
                catch (Exception ex) { SettingsStore.Log(ex); }
    }

    static Dictionary<string, Color> CreateColors(bool dark) => Palette.ToDictionary(
        entry => entry.Key,
        entry => (Color)ColorConverter.ConvertFromString(dark ? entry.Dark : entry.Light),
        StringComparer.Ordinal);

    static bool SystemUsesDarkAppTheme()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return personalize?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            SettingsStore.Log(ex);
            return true;
        }
    }
}

/// <summary>Explicit binding source shared by application brushes and their style clones.</summary>
public sealed class ThemePalette : INotifyPropertyChanged
{
    IReadOnlyDictionary<string, Color> colors;

    internal ThemePalette(IReadOnlyDictionary<string, Color> initialColors) => colors = initialColors;
    public Color this[string key] => colors[key];
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(IReadOnlyDictionary<string, Color> newColors)
    {
        // Publish a complete palette before any binding reads it, so notifications
        // never expose a mixture of the old and new theme.
        colors = newColors;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }
}

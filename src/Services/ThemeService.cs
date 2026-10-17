using Avalonia.Platform;
using Avalonia.Styling;

namespace SnipFlow.Services;

/// <summary>UI colors only. Captured pixels, annotation colors and export palettes are independent.</summary>
/// <remarks>
/// Each palette key is one shared <see cref="SolidColorBrush"/> stored in the application resources.
/// A theme change rewrites the colors of those brushes in place, so XAML <c>{DynamicResource}</c>
/// references and code-built views holding <see cref="Brush(string)"/> both update without rebuilding.
/// Fluent control resource keys are pointed at the same brushes.
/// </remarks>
public static class ThemeService
{
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

    // Fluent theme keys mapped onto the palette so built-in controls follow the same colors.
    static readonly (string FluentKey, string PaletteKey)[] FluentAliases =
    [
        ("TextControlBackground", "Raised"), ("TextControlBackgroundPointerOver", "Raised"),
        ("TextControlBackgroundFocused", "Raised"), ("TextControlBackgroundDisabled", "Raised"),
        ("TextControlBorderBrush", "Line"), ("TextControlBorderBrushPointerOver", "ControlBorder"),
        ("TextControlBorderBrushFocused", "Muted"), ("TextControlBorderBrushDisabled", "Line"),
        ("TextControlForeground", "Ink"), ("TextControlForegroundPointerOver", "Ink"),
        ("TextControlForegroundFocused", "Ink"), ("TextControlForegroundDisabled", "Muted"),
        ("TextControlPlaceholderForeground", "Muted"), ("TextControlPlaceholderForegroundPointerOver", "Muted"),
        ("TextControlPlaceholderForegroundFocused", "Muted"), ("TextControlSelectionHighlightColor", "TextSelection"),
        ("ComboBoxBackground", "Raised"), ("ComboBoxBackgroundPointerOver", "Hover"), ("ComboBoxBackgroundPressed", "Selected"),
        ("ComboBoxBackgroundDisabled", "Raised"), ("ComboBoxBackgroundUnfocused", "Raised"),
        ("ComboBoxBorderBrush", "Line"), ("ComboBoxBorderBrushPointerOver", "ControlBorder"),
        ("ComboBoxBorderBrushPressed", "Muted"), ("ComboBoxBorderBrushDisabled", "Line"),
        ("ComboBoxForeground", "Ink"), ("ComboBoxForegroundDisabled", "Muted"), ("ComboBoxForegroundFocused", "Ink"),
        ("ComboBoxForegroundFocusedPressed", "Ink"), ("ComboBoxDropDownGlyphForeground", "Muted"),
        ("ComboBoxDropDownBackground", "Panel"), ("ComboBoxDropDownBorderBrush", "Line"),
        ("ComboBoxItemForeground", "Ink"), ("ComboBoxItemForegroundPointerOver", "Ink"), ("ComboBoxItemForegroundSelected", "Ink"),
        ("ComboBoxItemForegroundSelectedPointerOver", "Ink"), ("ComboBoxItemForegroundPressed", "Ink"),
        ("ComboBoxItemBackgroundPointerOver", "Hover"), ("ComboBoxItemBackgroundSelected", "Selected"),
        ("ComboBoxItemBackgroundSelectedPointerOver", "Hover"), ("ComboBoxItemBackgroundPressed", "Selected"),
        ("ComboBoxItemBackgroundSelectedPressed", "Selected"),
        ("MenuFlyoutPresenterBackground", "Panel"), ("MenuFlyoutPresenterBorderBrush", "Line"),
        ("MenuFlyoutItemForeground", "Ink"), ("MenuFlyoutItemForegroundPointerOver", "Ink"),
        ("MenuFlyoutItemForegroundPressed", "Ink"), ("MenuFlyoutItemForegroundDisabled", "Muted"),
        ("MenuFlyoutItemBackgroundPointerOver", "Hover"), ("MenuFlyoutItemBackgroundPressed", "Selected"),
        ("MenuFlyoutItemKeyboardAcceleratorTextForeground", "Muted"),
        ("FlyoutPresenterBackground", "Panel"),
        ("ToolTipBackground", "Raised"), ("ToolTipForeground", "Ink"), ("ToolTipBorderBrush", "Line"),
        ("SliderTrackFill", "TrackEmpty"), ("SliderTrackFillPointerOver", "TrackEmpty"), ("SliderTrackFillPressed", "TrackEmpty"),
        ("SliderTrackValueFill", "TrackFilled"), ("SliderTrackValueFillPointerOver", "TrackFilled"), ("SliderTrackValueFillPressed", "TrackFilled"),
        ("SliderThumbBackground", "Accent"), ("SliderThumbBackgroundPointerOver", "PrimaryHover"), ("SliderThumbBackgroundPressed", "PrimaryPressed"),
        ("ScrollBarThumbFillPointerOver", "ScrollGripHover"), ("ScrollBarThumbFillPressed", "ScrollGripActive"),
        ("ScrollBarPanningThumbBackground", "ScrollGrip"),
        ("ScrollBarButtonArrowForeground", "Muted"),
        ("CheckBoxForegroundUnchecked", "Ink"), ("CheckBoxForegroundChecked", "Ink"),
    ];

    static readonly Dictionary<string, SolidColorBrush> Brushes = Palette.ToDictionary(
        entry => entry.Key, entry => new SolidColorBrush(Color.Parse(entry.Dark)), StringComparer.Ordinal);
    static bool initialized;
    static bool hasPalette;

    public static bool IsDark { get; private set; } = true;
    public static event EventHandler? Changed;

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "system" => "system",
        _ => "dark",
    };

    /// <summary>The shared, theme-updated brush for a palette key such as "Panel" or "Ink".</summary>
    public static SolidColorBrush Brush(string key) => Brushes[key];

    /// <summary>The current color of a palette key.</summary>
    public static Color GetColor(string key) => Brushes[key].Color;

    public static void Initialize()
    {
        if (initialized) return;
        Dispatcher.UIThread.VerifyAccess();
        initialized = true;
        var resources = Application.Current!.Resources;
        foreach (var (key, brush) in Brushes) resources[key] = brush;
        foreach (var (fluentKey, paletteKey) in FluentAliases) resources[fluentKey] = Brushes[paletteKey];
        Apply();
        SettingsStore.Changed += SettingsChanged;
        if (Application.Current.PlatformSettings is { } platform) platform.ColorValuesChanged += SystemColorsChanged;
    }

    public static void Shutdown()
    {
        if (!initialized) return;
        initialized = false;
        SettingsStore.Changed -= SettingsChanged;
        if (Application.Current?.PlatformSettings is { } platform) platform.ColorValuesChanged -= SystemColorsChanged;
    }

    static void SettingsChanged(object? sender, EventArgs e) => RefreshOnDispatcher();

    static void SystemColorsChanged(object? sender, PlatformColorValues e) => RefreshOnDispatcher(onlyForSystem: true);

    static void RefreshOnDispatcher(bool onlyForSystem = false)
    {
        void Refresh()
        {
            if (!initialized || (onlyForSystem && NormalizeMode(SettingsStore.Current.Theme) != "system")) return;
            Apply();
        }
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    static void Apply()
    {
        var mode = NormalizeMode(SettingsStore.Current.Theme);
        var dark = mode == "dark" || (mode == "system" && SystemUsesDarkAppTheme());
        if (Application.Current is { } app)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (hasPalette && IsDark == dark) return;
        IsDark = dark;
        foreach (var (key, darkValue, lightValue) in Palette)
            Brushes[key].Color = Avalonia.Media.Color.Parse(dark ? darkValue : lightValue);
        hasPalette = true;

        if (Changed is { } listeners)
            foreach (EventHandler listener in listeners.GetInvocationList())
                try { listener(null, EventArgs.Empty); }
                catch (Exception ex) { SettingsStore.Log(ex); }
    }

    static bool SystemUsesDarkAppTheme()
    {
        try
        {
            var values = Application.Current?.PlatformSettings?.GetColorValues();
            return values is null || values.ThemeVariant == PlatformThemeVariant.Dark;
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            return true;
        }
    }
}

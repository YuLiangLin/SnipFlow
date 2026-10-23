namespace SnipFlow.CodeCapture;

/// <summary>
/// Monospaced font choices for code images. Windows keeps the original fonts and fallback chain;
/// Linux offers the monospaced families that are installed, then falls back to the app's
/// monospaced font list and to CJK fonts so comments and strings in Chinese still render.
/// </summary>
internal static class CodeFonts
{
    static readonly string[] WindowsFonts = ["Cascadia Mono", "Consolas", "Courier New"];

    // Shown first, in this order, when installed.
    static readonly string[] LinuxPreferred =
    [
        "DejaVu Sans Mono", "Ubuntu Mono", "Ubuntu Sans Mono", "Noto Sans Mono", "Liberation Mono",
        "Cascadia Mono", "Cascadia Code", "JetBrains Mono", "Fira Code", "Fira Mono", "Source Code Pro",
        "Hack", "Inconsolata", "Roboto Mono", "IBM Plex Mono",
    ];

    static readonly string[] LinuxTraditional =
        ["Noto Sans Mono CJK TC", "Noto Sans CJK TC", "Source Han Sans TC", "AR PL UMing TW", "WenQuanYi Zen Hei Mono", "WenQuanYi Micro Hei Mono", "Droid Sans Fallback"];
    static readonly string[] LinuxSimplified =
        ["Noto Sans Mono CJK SC", "Noto Sans CJK SC", "Source Han Sans SC", "WenQuanYi Zen Hei Mono", "WenQuanYi Micro Hei Mono", "AR PL UMing CN", "Droid Sans Fallback"];

    const int MaxChoices = 20;
    static IReadOnlyList<string>? choices;

    /// <summary>The font names offered in the font list.</summary>
    internal static IReadOnlyList<string> Choices => choices ??= PlatformInfo.IsWindows ? WindowsFonts : FindLinuxFonts();

    /// <summary>The initially selected font: Consolas on Windows, DejaVu Sans Mono (or the first installed) on Linux.</summary>
    internal static string DefaultFont
    {
        get
        {
            if (PlatformInfo.IsWindows) return "Consolas";
            var list = Choices;
            return list.FirstOrDefault(name => name.Equals("DejaVu Sans Mono", StringComparison.OrdinalIgnoreCase)) ?? list[0];
        }
    }

    /// <summary>The selected font followed by the fallbacks used for the editor and the rendered image.</summary>
    internal static FontFamily Family(string fontName)
    {
        if (PlatformInfo.IsWindows) return new FontFamily(fontName + ", Consolas, Microsoft JhengHei UI, Segoe UI");
        var names = new List<string> { fontName };
        foreach (var name in UiThemes.MonoFont.FamilyNames) Add(names, name);
        foreach (var name in I18n.LanguageTag == "zh-CN" ? LinuxSimplified : LinuxTraditional) Add(names, name);
        return new FontFamily(string.Join(", ", names));
    }

    static void Add(List<string> names, string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
    }

    static IReadOnlyList<string> FindLinuxFonts()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in FontManager.Current.SystemFonts)
                if (!string.IsNullOrWhiteSpace(family.Name)) installed.Add(family.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SettingsStore.Log(ex); }
        var result = LinuxPreferred.Where(installed.Contains).ToList();
        foreach (var name in installed.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= MaxChoices) break;
            if (LooksMonospaced(name)) Add(result, name);
        }
        if (result.Count == 0) result.Add("monospace");
        return result;
    }

    // Name-based: loading every installed font to read its pitch flag would slow down opening the window.
    // Of the regional CJK variants only Traditional and Simplified Chinese are offered.
    static bool LooksMonospaced(string name)
        => !name.Contains("Emoji", StringComparison.OrdinalIgnoreCase)
           && !(name.Contains("CJK", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(" TC", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(" SC", StringComparison.OrdinalIgnoreCase))
           && (name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Courier", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Consol", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" Code", StringComparison.OrdinalIgnoreCase));
}

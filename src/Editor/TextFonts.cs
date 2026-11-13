using System.Collections.Concurrent;

namespace SnipFlow.Editor;

/// <summary>Installed text families and the shared fallback/typeface policy for annotations.</summary>
public static class TextFonts
{
    public const string DefaultFamily = "";
    private static readonly char[] FamilySeparators = [',', '#', '/', '\\', ':', ';', '|'];
    private static readonly Lazy<IReadOnlyList<string>> Families = new(FindFamilies);
    private static readonly ConcurrentDictionary<string, Typeface> Typefaces = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AvailableFamilies => Families.Value;

    public static bool TryNormalizeFamily(string? value, out string family)
    {
        family = DefaultFamily;
        if (value is null || value.Any(char.IsControl)) return false;
        var normalized = value.Trim();
        if (normalized.Length > 128 || normalized.IndexOfAny(FamilySeparators) >= 0) return false;
        family = normalized;
        return true;
    }

    public static string NormalizeFamily(string value) => TryNormalizeFamily(value, out var family)
        ? family : throw new ArgumentException("Text font family must be a plain family name of at most 128 characters.", nameof(value));

    public static bool IsInstalled(string family) => TryNormalizeFamily(family, out var normalized)
        && (normalized == DefaultFamily || AvailableFamilies.Contains(normalized, StringComparer.OrdinalIgnoreCase));

    public static Typeface ResolveTypeface(string family)
        => Typefaces.GetOrAdd(NormalizeFamily(family), CreateTypeface);

    private static IReadOnlyList<string> FindFamilies()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var installed in FontManager.Current.SystemFonts)
                if (TryNormalizeFamily(installed.Name, out var name) && name != DefaultFamily) names.Add(name);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Font discovery is optional, including before a platform font manager is available.
            // Do not initialize settings or write to user data while resolving a document font.
        }
        return names.Order(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    private static Typeface CreateTypeface(string requested)
    {
        var family = UiThemes.AppFont;
        if (requested != DefaultFamily && IsInstalled(requested))
        {
            var names = new[] { requested }.Concat(family.FamilyNames).Distinct(StringComparer.OrdinalIgnoreCase);
            family = new FontFamily(string.Join(", ", names));
        }
        // Keep the established default weight, using a real Bold face when SemiBold would
        // synthesize another bold pass over a family's existing Bold face.
        var semiBold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
        try
        {
            if ((semiBold.GlyphTypeface.FontSimulations & FontSimulations.Bold) != 0)
            {
                var bold = new Typeface(family, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal);
                if ((bold.GlyphTypeface.FontSimulations & FontSimulations.Bold) == 0) return bold;
            }
        }
        catch (InvalidOperationException)
        {
            // No platform font manager: retain the requested weight, as the legacy default did.
        }
        return semiBold;
    }
}

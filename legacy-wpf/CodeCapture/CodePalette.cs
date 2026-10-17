using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace SnipFlow.CodeCapture;

internal sealed class CodePalette
{
    readonly bool dark;
    readonly Dictionary<HighlightingColor, HighlightingColor> mapped = new();
    internal Brush Background { get; }
    internal Brush Foreground { get; }
    internal Brush Gutter { get; }
    internal Brush LineNumbers { get; }

    internal CodePalette(bool dark)
    {
        this.dark = dark;
        Background = Brush(dark ? "#202020" : "#FAFAF8");
        Foreground = Brush(dark ? "#ECECEC" : "#242424");
        Gutter = Brush(dark ? "#282828" : "#F0F0ED");
        LineNumbers = Brush(dark ? "#A6A6A6" : "#606060");
    }

    internal HighlightingColor Map(HighlightingColor original)
    {
        if (mapped.TryGetValue(original, out var found)) return found;
        var result = original.Clone();
        // A code image has one explicit monospaced font and size, including Markdown.
        result.FontFamily = null; result.FontSize = null;
        result.Background = null;
        if (original.Foreground != null)
        {
            var name = original.Name?.ToLowerInvariant() ?? "";
            string? value = name.Contains("comment") ? (dark ? "#9AB58D" : "#4C713A")
                : name.Contains("string") || name.Contains("char") ? (dark ? "#DEBB8D" : "#88581F")
                : name.Contains("keyword") || name.Contains("preprocessor") ? (dark ? "#C9AFE9" : "#7842A5")
                : name.Contains("number") || name.Contains("digit") ? (dark ? "#A6CEAC" : "#397143")
                : name.Contains("type") ? (dark ? "#8DC8D1" : "#236A76")
                : name.Contains("tag") || name.Contains("attribute") || name.Contains("property") ? (dark ? "#ACC8E4" : "#335F89")
                : null;
            var color = value != null ? (Color)ColorConverter.ConvertFromString(value)
                : original.Foreground.GetColor(null) ?? ((SolidColorBrush)Foreground).Color;
            result.Foreground = new SimpleHighlightingBrush(Readable(color));
        }
        result.Freeze(); mapped.Add(original, result); return result;
    }

    Color Readable(Color color)
    {
        color.A = 255;
        var background = ((SolidColorBrush)Background).Color;
        for (int step = 0; step < 20 && Contrast(color, background) < 4.5; step++)
        {
            byte Blend(byte channel) => (byte)Math.Round(channel * .88 + (dark ? 255 : 0) * .12);
            color = Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
        }
        return color;
    }

    static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v) { double n = v / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        }
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze(); return brush;
    }
}

internal sealed class CodeColorizer(IHighlightingDefinition definition, CodePalette palette)
    : HighlightingColorizer(definition)
{
    protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        => base.ApplyColorToElement(element, palette.Map(color));
}

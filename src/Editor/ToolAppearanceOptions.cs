namespace SnipFlow.Editor;

/// <summary>Color and stroke width used for future annotations of one drawing tool.</summary>
public sealed record ToolAppearanceOptions
{
    public string Color { get; init; } = "#63D5C5";
    public double StrokeWidth { get; init; } = 4;

    public static ToolAppearanceOptions Default { get; } = new();

    public static bool Supports(AnnotationTool tool) => tool is AnnotationTool.Arrow
        or AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Pen
        or AnnotationTool.Highlight or AnnotationTool.Text or AnnotationTool.Mosaic;

    public static bool IsValid(ToolAppearanceOptions? value) => value is not null
        && TryNormalizeColor(value.Color, out _)
        && double.IsFinite(value.StrokeWidth) && value.StrokeWidth is >= 1 and <= 32;

    public static bool TryNormalizeColor(string? value, out string color)
    {
        color = "#63D5C5";
        if (value is not { Length: 7 } || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit)) return false;
        color = value.ToUpperInvariant();
        return true;
    }
}

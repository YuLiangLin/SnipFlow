namespace SnipFlow.Ui;

/// <summary>
/// Vector icons shared by every platform. They replace the Windows-only Segoe Fluent Icons font;
/// the drawn icon uses the parent's foreground unless a brush is given.
/// </summary>
public static partial class Icons
{
    static readonly Dictionary<IconKind, Geometry> Cache = new();

    public static Geometry Geometry(IconKind kind)
    {
        if (!Cache.TryGetValue(kind, out var geometry))
            Cache[kind] = geometry = StreamGeometry.Parse(PathData(kind));
        return geometry;
    }

    /// <summary>An icon sized in device-independent pixels that follows the inherited Foreground.</summary>
    public static PathIcon Create(IconKind kind, double size = 16, IBrush? foreground = null)
    {
        var icon = new PathIcon { Data = Geometry(kind), Width = size, Height = size };
        if (foreground is not null) icon.Foreground = foreground;
        return icon;
    }

    /// <summary>An icon followed by a label, for buttons that showed a glyph and text in WPF.</summary>
    public static StackPanel WithText(IconKind kind, string text, double size = 16, double spacing = 7, IBrush? foreground = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        panel.Children.Add(Create(kind, size, foreground));
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (foreground is not null) label.Foreground = foreground;
        panel.Children.Add(label);
        return panel;
    }
}

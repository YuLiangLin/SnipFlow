using Avalonia.Markup.Xaml;

namespace SnipFlow;

/// <summary>XAML access to the shared vector icons: <c>&lt;PathIcon Data="{local:Icon Camera}" /&gt;</c>.</summary>
public sealed class IconExtension : MarkupExtension
{
    public IconExtension(IconKind kind) => Kind = kind;

    public IconKind Kind { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Icons.Geometry(Kind);
}

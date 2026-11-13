namespace SnipFlow.Editor;

public enum ArrowHeadKind { None, Arrow, Triangle, Open, Diamond, Circle }
public enum ArrowHeadSize { Small, Medium, Large }
public enum ArrowLineStyle { Solid, Dash, Dot }

/// <summary>Independent endpoint shapes and sizes, plus the line style of the arrow's shaft.</summary>
public sealed record ArrowStyleOptions
{
    public ArrowHeadKind StartHead { get; init; } = ArrowHeadKind.None;
    public ArrowHeadKind EndHead { get; init; } = ArrowHeadKind.Arrow;
    public ArrowHeadSize StartSize { get; init; } = ArrowHeadSize.Medium;
    public ArrowHeadSize EndSize { get; init; } = ArrowHeadSize.Medium;
    public ArrowLineStyle LineStyle { get; init; } = ArrowLineStyle.Solid;

    public static ArrowStyleOptions Default { get; } = new();

    public static bool IsValid(ArrowStyleOptions? value) => value is not null
        && Enum.IsDefined(value.StartHead) && Enum.IsDefined(value.EndHead)
        && Enum.IsDefined(value.StartSize) && Enum.IsDefined(value.EndSize)
        && Enum.IsDefined(value.LineStyle);
}

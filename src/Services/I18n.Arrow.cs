namespace SnipFlow.Services;

public static partial class I18n
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterArrow() => Register(new Dictionary<string, (string English, string Simplified)>(StringComparer.Ordinal)
    {
        ["雙向箭頭"] = ("Double-headed arrow", "双向箭头"),
        ["在箭頭兩端顯示箭頭"] = ("Show arrowheads at both ends of the arrow", "在箭头两端显示箭头")
    });
}

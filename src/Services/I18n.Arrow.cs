namespace SnipFlow.Services;

public static partial class I18n
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterArrow() => Register(new Dictionary<string, (string English, string Simplified)>(StringComparer.Ordinal)
    {
        ["雙向箭頭"] = ("Double-headed arrow", "双向箭头"),
        ["在箭頭兩端顯示箭頭"] = ("Show arrowheads at both ends of the arrow", "在箭头两端显示箭头"),
        ["箭頭設定"] = ("Arrow settings", "箭头设置"),
        ["箭頭設定 ▾"] = ("Arrow settings ▾", "箭头设置 ▾"),
        ["設定兩端樣式、大小與線條格式"] = ("Set the style and size of both ends and the line pattern", "设置两端样式、大小与线条格式"),
        ["單向箭頭"] = ("Single-headed arrow", "单向箭头"),
        ["即時預覽"] = ("Live preview", "实时预览"),
        ["箭頭預覽"] = ("Arrow preview", "箭头预览"),
        ["起點樣式"] = ("Start style", "起点样式"),
        ["起點大小"] = ("Start size", "起点大小"),
        ["終點樣式"] = ("End style", "终点样式"),
        ["終點大小"] = ("End size", "终点大小"),
        ["線條格式"] = ("Line pattern", "线条格式"),
        ["無端點"] = ("None", "无端点"),
        ["三角形"] = ("Triangle", "三角形"),
        ["開放箭頭"] = ("Open arrow", "开放箭头"),
        ["菱形"] = ("Diamond", "菱形"),
        ["圓形"] = ("Circle", "圆形"),
        ["小"] = ("Small", "小"),
        ["中"] = ("Medium", "中"),
        ["大"] = ("Large", "大"),
        ["實線"] = ("Solid", "实线"),
        ["虛線"] = ("Dashed", "虚线"),
        ["點線"] = ("Dotted", "点线")
    });
}

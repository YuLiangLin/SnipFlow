namespace SnipFlow.Services;

public static partial class I18n
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterEditingTools() => Register(new Dictionary<string, (string English, string Simplified)>(StringComparer.Ordinal)
    {
        ["常用箭頭"] = ("Arrow presets", "常用箭头"),
        ["單向實線箭頭"] = ("Single-headed solid arrow", "单向实线箭头"),
        ["雙向實線箭頭"] = ("Double-headed solid arrow", "双向实线箭头"),
        ["無箭頭直線"] = ("Line without arrowheads", "无箭头直线"),
        ["開放式單向箭頭"] = ("Single-headed open arrow", "开放式单向箭头"),
        ["開放式雙向箭頭"] = ("Double-headed open arrow", "开放式双向箭头"),
        ["圓點端點直線"] = ("Line with a circle endpoint", "圆点端点直线"),
        ["菱形端點直線"] = ("Line with a diamond endpoint", "菱形端点直线"),
        ["單向虛線箭頭"] = ("Single-headed dashed arrow", "单向虚线箭头"),
        ["雙向虛線箭頭"] = ("Double-headed dashed arrow", "双向虚线箭头"),
        ["單向點線箭頭"] = ("Single-headed dotted arrow", "单向点线箭头"),
        ["圖形"] = ("Shape", "图形"),
        ["圖形 (G)"] = ("Shape (G)", "图形 (G)"),
        ["常用圖形"] = ("Shapes", "常用图形"),
        ["填滿圖形"] = ("Fill shape", "填充图形"),
        ["圓角矩形"] = ("Rounded rectangle", "圆角矩形"),
        ["直角三角形"] = ("Right triangle", "直角三角形"),
        ["五邊形"] = ("Pentagon", "五边形"),
        ["六邊形"] = ("Hexagon", "六边形"),
        ["星形"] = ("Star", "星形"),
        ["愛心"] = ("Heart", "爱心"),
        ["十字形"] = ("Plus", "十字形"),
        ["雲形"] = ("Cloud", "云形"),
        ["對話方塊"] = ("Callout", "对话框"),
        ["向右粗箭頭"] = ("Right block arrow", "向右粗箭头"),
        ["向左粗箭頭"] = ("Left block arrow", "向左粗箭头"),
        ["向上粗箭頭"] = ("Up block arrow", "向上粗箭头"),
        ["向下粗箭頭"] = ("Down block arrow", "向下粗箭头"),
        ["雙向粗箭頭"] = ("Left-right block arrow", "双向粗箭头"),
        ["梯形"] = ("Trapezoid", "梯形"),
        ["平行四邊形"] = ("Parallelogram", "平行四边形")
    });
}

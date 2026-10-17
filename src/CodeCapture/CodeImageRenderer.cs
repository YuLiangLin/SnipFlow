using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using ITextSource = Avalonia.Media.TextFormatting.ITextSource;

namespace SnipFlow.CodeCapture;

internal static class CodeImageRenderer
{
    const double Padding = 24;

    // Called on the UI dispatcher. Avalonia's text formatter preserves Unicode and whitespace,
    // tabs advance to real tab stops, and the syntax engine tracks multiline comments and strings.
    // Yield between bounded chunks so edits/closing can cancel without a second render.
    internal static async Task<Bitmap> RenderAsync(string code, CodeImageOptions options, CancellationToken cancellation)
    {
        CodeImageLimits.ValidateText(code);
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException(I18n.T("請先貼上程式碼。"));
        if (options.Width is < 640 or > 4096 || options.FontSize is < 12 or > 32
            || options.StartingLine is < 1 or > 1_000_000)
            throw new ArgumentException(I18n.T("請確認圖片寬度、字級與起始行號。"));

        var palette = new CodePalette(options.Dark);
        var family = CodeFonts.Family(options.FontName);
        var typeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        var ordinary = new RunProperties(typeface, options.FontSize, palette.Foreground);
        var document = new TextDocument(code);
        var definition = options.HighlightingName == null ? null : HighlightingManager.Instance.GetDefinition(options.HighlightingName);
        using var highlighter = definition == null ? null : new DocumentHighlighter(document, definition);
        var formatter = TextFormatter.Current;

        var lastNumber = (options.StartingLine + document.LineCount - 1).ToString(CultureInfo.InvariantCulture);
        double numberWidth = options.LineNumbers ? MakeText(lastNumber, typeface, options.FontSize, palette.LineNumbers).Width + 16 : 0;
        double textLeft = Padding + numberWidth;
        double textWidth = options.Width - textLeft - Padding;
        double tabWidth = MakeText("    ", typeface, options.FontSize, palette.Foreground).WidthIncludingTrailingWhitespace;
        double lineHeight = options.FontSize * 1.5;
        // Every row is exactly one line height and shares one baseline placed in proportion to the
        // main font's ascent, as WPF does for an explicit line height. Avalonia would otherwise let
        // taller fallback fonts (CJK, emoji) grow a row or move its baseline.
        double baseline = Baseline(typeface, lineHeight);
        var rows = new List<Row>();
        double top = Padding;
        var elapsed = Stopwatch.StartNew();
        var chunk = Stopwatch.StartNew();
        int styledRuns = 0;

        try
        {
            foreach (var logicalLine in document.Lines)
            {
                cancellation.ThrowIfCancellationRequested();
                var text = document.GetText(logicalLine.Offset, logicalLine.Length);
                var highlighted = highlighter?.HighlightLine(logicalLine.LineNumber);
                var runs = MakeRuns(text.Length, logicalLine.Offset, highlighted, palette, family, options.FontSize, ordinary);
                styledRuns += runs.Count;
                if (styledRuns > 30_000) throw new InvalidOperationException(I18n.T("程式碼格式過於複雜，請分段製作。"));
                var source = new LineSource(text, runs, ordinary, formatter, tabWidth);
                int position = 0;
                bool first = true;
                TextLineBreak? lineBreak = null;
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    source.BeginRow(position, textWidth);
                    var row = formatter.FormatLine(source, position, textWidth,
                        new ParagraphProperties(ordinary, options.WordWrap, first, lineHeight, tabWidth), lineBreak)
                        ?? throw new InvalidOperationException(I18n.T("程式碼無法排版，請縮短內容後再試。"));
                    var number = first && options.LineNumbers
                        ? MakeText((options.StartingLine + logicalLine.LineNumber - 1).ToString(CultureInfo.InvariantCulture),
                            typeface, options.FontSize, palette.LineNumbers)
                        : null;
                    rows.Add(new Row(row, top, number));
                    lineBreak = row.TextLineBreak;
                    if (row.Length <= 0 && text.Length > 0) throw new InvalidOperationException(I18n.T("程式碼無法排版，請縮短內容後再試。"));
                    // Wrapped rows may end in a break space that hangs past the edge; it draws nothing.
                    if ((options.WordWrap ? row.Width : row.WidthIncludingTrailingWhitespace) > textWidth + .5)
                        throw new InvalidOperationException(I18n.F("第 {0:N0} 行超出圖片寬度，請開啟自動換行或增加寬度。", options.StartingLine + logicalLine.LineNumber - 1));
                    var nextTop = top + lineHeight;
                    ValidateDimensions(options.Width, nextTop + Padding);
                    top = nextTop; position += Math.Max(1, row.Length); first = false;
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                        throw new InvalidOperationException(I18n.T("程式碼排版時間過長，請分段製作。"));
                    if (chunk.ElapsedMilliseconds >= 12)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        cancellation.ThrowIfCancellationRequested();
                        chunk.Restart();
                    }
                } while (position < text.Length);
            }

            cancellation.ThrowIfCancellationRequested();
            int height = checked((int)Math.Ceiling(top + Padding));
            ValidateDimensions(options.Width, height);
            return ImageTools.Render(new PixelSize(options.Width, height), drawing =>
            {
                // Grayscale anti-aliasing, as in WPF's RenderTargetBitmap; colored subpixel fringes would be baked into the PNG.
                using var textOptions = drawing.PushTextOptions(new TextOptions
                {
                    TextRenderingMode = TextRenderingMode.Antialias, TextHintingMode = TextHintingMode.Light,
                    BaselinePixelAlignment = BaselinePixelAlignment.Aligned,
                });
                drawing.DrawRectangle(palette.Background, null, new Rect(0, 0, options.Width, height));
                if (options.LineNumbers)
                    drawing.DrawRectangle(palette.Gutter, null, new Rect(0, 0, textLeft - 8, height));
                foreach (var row in rows)
                {
                    if (row.Number is { } number)
                        drawing.DrawText(number, new Point(textLeft - 16 - number.Width, row.Top + baseline - number.Baseline));
                    row.Line.Draw(drawing, new Point(textLeft, row.Top + baseline - row.Line.Baseline));
                }
            });
        }
        finally
        {
            foreach (var row in rows) row.Line.Dispose();
        }
    }

    static double Baseline(Typeface typeface, double lineHeight)
    {
        var metrics = typeface.GlyphTypeface.Metrics;
        double ascent = -metrics.Ascent, spacing = metrics.Descent - metrics.Ascent + metrics.LineGap;
        return ascent > 0 && spacing >= ascent ? lineHeight * ascent / spacing : lineHeight * .8;
    }

    static void ValidateDimensions(int width, double height)
    {
        if (!double.IsFinite(height) || height > CodeImageLimits.MaxHeight || width * height > CodeImageLimits.MaxPixels)
            throw new InvalidOperationException(I18n.F("圖片超過 {0:N0} 像素高或 {1:N0} 百萬像素，請分段製作。", CodeImageLimits.MaxHeight, CodeImageLimits.MaxPixels / 1_000_000));
    }

    static FormattedText MakeText(string text, Typeface typeface, double size, IBrush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);

    static List<StyledRun> MakeRuns(int length, int documentOffset, HighlightedLine? highlighted,
        CodePalette palette, FontFamily family, double size, RunProperties ordinary)
    {
        if (length == 0) return new();
        var sections = highlighted?.Sections.ToArray() ?? Array.Empty<HighlightedSection>();
        if (sections.Length > 2_048)
            throw new InvalidOperationException(I18n.T("程式碼格式過於複雜，請分段製作。"));
        var boundaries = new SortedSet<int> { 0, length };
        foreach (var section in sections)
        {
            boundaries.Add(Math.Clamp(section.Offset - documentOffset, 0, length));
            boundaries.Add(Math.Clamp(section.Offset + section.Length - documentOffset, 0, length));
        }
        var points = boundaries.ToArray();
        var result = new List<StyledRun>(points.Length - 1);
        for (int index = 0; index < points.Length - 1; index++)
        {
            var start = points[index]; var end = points[index + 1];
            IBrush foreground = palette.Foreground;
            var weight = FontWeight.Normal; var style = FontStyle.Normal;
            bool styled = false;
            // AvaloniaEdit sections may nest. Apply in the engine's order, preserving
            // outer properties that the inner color does not override.
            foreach (var section in sections)
            {
                if (section.Offset > documentOffset + start || section.Offset + section.Length <= documentOffset + start) continue;
                var color = palette.Map(section.Color); styled = true;
                if (color.Foreground?.GetColor(null!) is Color value) foreground = new SolidColorBrush(value);
                if (color.FontWeight is FontWeight namedWeight) weight = namedWeight;
                if (color.FontStyle is FontStyle namedStyle) style = namedStyle;
            }
            var properties = styled ? new RunProperties(new Typeface(family, style, weight), size, foreground) : ordinary;
            result.Add(new StyledRun(start, end, properties));
        }
        return result;
    }

    sealed record StyledRun(int Start, int End, RunProperties Properties);

    sealed record Row(TextLine Line, double Top, FormattedText? Number);

    /// <summary>One logical line. Tabs become runs that reach the next tab stop of the current row.</summary>
    sealed class LineSource(string text, IReadOnlyList<StyledRun> runs, RunProperties ordinary, TextFormatter formatter, double tabWidth)
        : ITextSource
    {
        readonly Dictionary<int, double> tabs = new();

        // Tab widths depend on the text before them in the same row, so measure them before formatting each row.
        internal void BeginRow(int rowStart, double maxWidth)
        {
            tabs.Clear();
            double x = 0;
            int segment = rowStart;
            for (int tab = text.IndexOf('\t', rowStart); tab >= 0; tab = text.IndexOf('\t', tab + 1))
            {
                if (tab > segment) x += Measure(segment, tab);
                var next = (Math.Floor((x + .01) / tabWidth) + 1) * tabWidth;
                tabs[tab] = next - x;
                x = next; segment = tab + 1;
                // Later tabs fall on a following row (wrapping) or the row fails the width check anyway.
                if (x > maxWidth) break;
            }
        }

        double Measure(int start, int end)
        {
            using var line = formatter.FormatLine(new Segment(this, end), start, double.PositiveInfinity,
                new ParagraphProperties(ordinary, false, true, 0, tabWidth), null);
            return line?.WidthIncludingTrailingWhitespace ?? 0;
        }

        public TextRun? GetTextRun(int textSourceIndex)
        {
            if (textSourceIndex >= text.Length) return new TextEndOfParagraph(1);
            if (text[textSourceIndex] == '\t')
                return new TabRun(tabs.TryGetValue(textSourceIndex, out var width) ? width : tabWidth, PropertiesAt(textSourceIndex, out _));
            return Characters(textSourceIndex, text.Length);
        }

        TextRun Characters(int index, int limit)
        {
            var properties = PropertiesAt(index, out var end);
            end = Math.Min(end, limit);
            int tab = text.IndexOf('\t', index, end - index);
            if (tab >= 0) end = tab;
            return new TextCharacters(text.AsMemory(index, end - index), properties);
        }

        RunProperties PropertiesAt(int index, out int end)
        {
            foreach (var run in runs)
                if (index >= run.Start && index < run.End) { end = run.End; return run.Properties; }
            end = text.Length;
            return ordinary;
        }

        /// <summary>The text between two tabs, for measuring.</summary>
        sealed class Segment(LineSource owner, int end) : ITextSource
        {
            public TextRun? GetTextRun(int textSourceIndex)
                => textSourceIndex >= end ? new TextEndOfParagraph(1) : owner.Characters(textSourceIndex, end);
        }
    }

    sealed class TabRun(double width, TextRunProperties properties) : DrawableTextRun
    {
        public override int Length => 1;
        public override TextRunProperties? Properties => properties;
        public override Size Size => new(width, 0);
        public override double Baseline => 0;
        public override void Draw(DrawingContext drawingContext, Point origin) { }
    }

    sealed class RunProperties(Typeface face, double size, IBrush brush) : TextRunProperties
    {
        public override Typeface Typeface => face;
        public override double FontRenderingEmSize => size;
        public override IBrush? ForegroundBrush => brush;
        public override IBrush? BackgroundBrush => null;
        public override CultureInfo? CultureInfo => CultureInfo.InvariantCulture;
        public override TextDecorationCollection? TextDecorations => null;
    }

    sealed class ParagraphProperties(RunProperties ordinary, bool wrap, bool first, double lineHeight, double tabWidth) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => lineHeight;
        public override bool FirstLineInParagraph => first;
        public override TextRunProperties DefaultTextRunProperties => ordinary;
        public override TextWrapping TextWrapping => wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        public override double Indent => 0;
        public override double DefaultIncrementalTab => tabWidth;
    }
}

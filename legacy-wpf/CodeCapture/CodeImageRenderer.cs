using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using SnipFlow.Services;

namespace SnipFlow.CodeCapture;

internal static class CodeImageRenderer
{
    const double Padding = 24;

    // Called on the window dispatcher. WPF shaping preserves Unicode, whitespace,
    // and real tab stops; the syntax engine tracks multiline comments and strings.
    // Yield between bounded chunks so edits/closing can cancel without a second render.
    internal static async Task<BitmapSource> RenderAsync(string code, CodeImageOptions options, CancellationToken cancellation)
    {
        CodeImageLimits.ValidateText(code);
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException(I18n.T("請先貼上程式碼。"));
        if (options.Width is < 640 or > 4096 || options.FontSize is < 12 or > 32
            || options.StartingLine is < 1 or > 1_000_000)
            throw new ArgumentException(I18n.T("請確認圖片寬度、字級與起始行號。"));

        var palette = new CodePalette(options.Dark);
        var family = new FontFamily(options.FontName + ", Consolas, Microsoft JhengHei UI, Segoe UI");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ordinary = new RunProperties(typeface, options.FontSize, palette.Foreground);
        var document = new TextDocument(code);
        var definition = options.HighlightingName == null ? null : HighlightingManager.Instance.GetDefinition(options.HighlightingName);
        using var highlighter = definition == null ? null : new DocumentHighlighter(document, definition);
        using var formatter = TextFormatter.Create(TextFormattingMode.Ideal);

        var lastNumber = (options.StartingLine + document.LineCount - 1).ToString(CultureInfo.InvariantCulture);
        double numberWidth = options.LineNumbers ? MakeText(lastNumber, typeface, options.FontSize, palette.LineNumbers).Width + 16 : 0;
        double textLeft = Padding + numberWidth;
        double textWidth = options.Width - textLeft - Padding;
        double tabWidth = MakeText("    ", typeface, options.FontSize, palette.Foreground).WidthIncludingTrailingWhitespace;
        var textVisual = new DrawingVisual();
        double top = Padding;
        var elapsed = Stopwatch.StartNew();
        var chunk = Stopwatch.StartNew();
        int styledRuns = 0;

        using (var drawing = textVisual.RenderOpen())
        {
            foreach (var logicalLine in document.Lines)
            {
                cancellation.ThrowIfCancellationRequested();
                var text = document.GetText(logicalLine.Offset, logicalLine.Length);
                var highlighted = highlighter?.HighlightLine(logicalLine.LineNumber);
                var runs = MakeRuns(text.Length, logicalLine.Offset, highlighted, palette, family, options.FontSize, ordinary);
                styledRuns += runs.Count;
                if (styledRuns > 30_000) throw new InvalidOperationException(I18n.T("程式碼格式過於複雜，請分段製作。"));
                var source = new LineSource(text, runs, ordinary);
                int position = 0;
                bool first = true;
                TextLineBreak? lineBreak = null;
                try
                {
                    do
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var previousBreak = lineBreak;
                        using var row = formatter.FormatLine(source, position, textWidth,
                            new ParagraphProperties(ordinary, options.WordWrap, first, options.FontSize * 1.5, tabWidth), lineBreak);
                        lineBreak = row.GetTextLineBreak();
                        previousBreak?.Dispose();
                        if (row.Length <= 0) throw new InvalidOperationException(I18n.T("程式碼無法排版，請縮短內容後再試。"));
                        if (row.WidthIncludingTrailingWhitespace > textWidth + .5)
                            throw new InvalidOperationException(I18n.F("第 {0:N0} 行超出圖片寬度，請開啟自動換行或增加寬度。", options.StartingLine + logicalLine.LineNumber - 1));
                        var nextTop = top + row.Height;
                        ValidateDimensions(options.Width, nextTop + Padding);
                        if (first && options.LineNumbers)
                        {
                            var number = MakeText((options.StartingLine + logicalLine.LineNumber - 1).ToString(CultureInfo.InvariantCulture),
                                typeface, options.FontSize, palette.LineNumbers);
                            drawing.DrawText(number, new Point(textLeft - 16 - number.Width, top + row.Baseline - number.Baseline));
                        }
                        row.Draw(drawing, new Point(textLeft, top), InvertAxes.None);
                        top = nextTop; position += row.Length; first = false;
                        if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                            throw new InvalidOperationException(I18n.T("程式碼排版時間過長，請分段製作。"));
                        if (chunk.ElapsedMilliseconds >= 12)
                        {
                            await Dispatcher.Yield(DispatcherPriority.Background);
                            cancellation.ThrowIfCancellationRequested();
                            chunk.Restart();
                        }
                    } while (position < text.Length);
                }
                finally { lineBreak?.Dispose(); }
            }
        }

        cancellation.ThrowIfCancellationRequested();
        int height = checked((int)Math.Ceiling(top + Padding));
        ValidateDimensions(options.Width, height);
        var complete = new DrawingVisual();
        using (var drawing = complete.RenderOpen())
        {
            drawing.DrawRectangle(palette.Background, null, new Rect(0, 0, options.Width, height));
            if (options.LineNumbers)
                drawing.DrawRectangle(palette.Gutter, null, new Rect(0, 0, textLeft - 8, height));
            drawing.DrawDrawing(textVisual.Drawing);
        }
        var image = new RenderTargetBitmap(options.Width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(complete); image.Freeze();
        return image;
    }

    static void ValidateDimensions(int width, double height)
    {
        if (!double.IsFinite(height) || height > CodeImageLimits.MaxHeight || width * height > CodeImageLimits.MaxPixels)
            throw new InvalidOperationException(I18n.F("圖片超過 {0:N0} 像素高或 {1:N0} 百萬像素，請分段製作。", CodeImageLimits.MaxHeight, CodeImageLimits.MaxPixels / 1_000_000));
    }

    static FormattedText MakeText(string text, Typeface typeface, double size, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, 1);

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
            Brush foreground = palette.Foreground;
            var weight = FontWeights.Normal; var style = FontStyles.Normal;
            bool styled = false;
            // AvalonEdit sections may nest. Apply in the engine's order, preserving
            // outer properties that the inner color does not override.
            foreach (var section in sections)
            {
                if (section.Offset > documentOffset + start || section.Offset + section.Length <= documentOffset + start) continue;
                var color = palette.Map(section.Color); styled = true;
                if (color.Foreground?.GetColor(null) is Color value)
                {
                    var brush = new SolidColorBrush(value); brush.Freeze(); foreground = brush;
                }
                if (color.FontWeight is FontWeight namedWeight) weight = namedWeight;
                if (color.FontStyle is FontStyle namedStyle) style = namedStyle;
            }
            var properties = styled ? new RunProperties(new Typeface(family, style, weight, FontStretches.Normal), size, foreground) : ordinary;
            result.Add(new StyledRun(start, end, properties));
        }
        return result;
    }

    sealed record StyledRun(int Start, int End, RunProperties Properties);

    sealed class LineSource(string text, IReadOnlyList<StyledRun> runs, RunProperties ordinary) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex)
        {
            if (textSourceCharacterIndex >= text.Length) return new TextEndOfParagraph(1, ordinary);
            foreach (var run in runs)
                if (textSourceCharacterIndex >= run.Start && textSourceCharacterIndex < run.End)
                    return new TextCharacters(text, textSourceCharacterIndex, run.End - textSourceCharacterIndex, run.Properties);
            return new TextCharacters(text, textSourceCharacterIndex, text.Length - textSourceCharacterIndex, ordinary);
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit)
        {
            int length = Math.Clamp(textSourceCharacterIndexLimit, 0, text.Length);
            return new(length, new CultureSpecificCharacterBufferRange(CultureInfo.InvariantCulture, new CharacterBufferRange(text, 0, length)));
        }

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex) => textSourceCharacterIndex;
    }

    sealed class RunProperties(Typeface face, double size, Brush brush) : TextRunProperties
    {
        public override Typeface Typeface => face;
        public override double FontRenderingEmSize => size;
        public override double FontHintingEmSize => size;
        public override Brush ForegroundBrush => brush;
        public override Brush BackgroundBrush => null!;
        public override CultureInfo CultureInfo => CultureInfo.InvariantCulture;
        public override TextDecorationCollection TextDecorations => null!;
        public override TextEffectCollection TextEffects => null!;
    }

    sealed class ParagraphProperties(RunProperties ordinary, bool wrap, bool first, double lineHeight, double tabWidth) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => lineHeight;
        public override bool FirstLineInParagraph => first;
        public override TextRunProperties DefaultTextRunProperties => ordinary;
        public override TextWrapping TextWrapping => wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        public override TextMarkerProperties TextMarkerProperties => null!;
        public override double Indent => 0;
        public override double DefaultIncrementalTab => tabWidth;
    }
}

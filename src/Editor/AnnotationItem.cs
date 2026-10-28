using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace SnipFlow.Editor;

internal sealed class AnnotationItem
{
    private static Typeface? _textTypeface;
    // The WPF version used Segoe UI; AppFont keeps it first on Windows and falls back to Noto Sans CJK
    // (and other system fonts) on Linux, so Chinese and Japanese text renders in both places.
    internal static Typeface TextTypeface => _textTypeface ??= CreateTextTypeface();

    private static Typeface CreateTextTypeface()
    {
        var semiBold = new Typeface(UiThemes.AppFont, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
        try
        {
            // Families without a SemiBold face (Noto Sans CJK ships Regular and Bold) resolve to the
            // Bold face and are then emboldened a second time, unevenly. Use the real Bold face instead.
            if ((semiBold.GlyphTypeface.FontSimulations & FontSimulations.Bold) != 0)
            {
                var bold = new Typeface(UiThemes.AppFont, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal);
                if ((bold.GlyphTypeface.FontSimulations & FontSimulations.Bold) == 0)
                    return bold;
            }
        }
        catch (InvalidOperationException)
        {
            // No font manager (headless); keep the requested weight.
        }
        return semiBold;
    }

    internal AnnotationTool Tool { get; init; }
    internal Point Start { get; set; }
    internal Point End { get; set; }
    internal Color Color { get; set; }
    internal double Width { get; set; }
    internal List<Point> Points { get; init; } = new();
    internal string Text { get; set; } = string.Empty;
    internal double FontSize { get; set; } = 26;
    internal double TextWidth { get; set; }
    internal double TextBoxHeight { get; set; }
    internal int TextOrientation { get; set; }
    internal Bitmap? Mosaic { get; set; }
    internal Bitmap? Image { get; set; }
    // Normalized source viewport; it can extend beyond the bitmap to leave blank space in the frame.
    // The complete bitmap is kept so its scale and position can always be reset.
    internal Rect? ImageCrop { get; set; }

    // Laying out text is the expensive part of Bounds; the cache is keyed by every input of the layout.
    private TextLayout? _layout;
    private (string Text, double FontSize, double TextWidth, Color Color) _layoutKey;

    internal AnnotationItem Clone() => new()
    {
        Tool = Tool,
        Start = Start,
        End = End,
        Color = Color,
        Width = Width,
        Points = new List<Point>(Points),
        Text = Text,
        FontSize = FontSize,
        TextWidth = TextWidth,
        TextBoxHeight = TextBoxHeight,
        TextOrientation = TextOrientation,
        // Bitmaps are never modified after they are stored.
        Mosaic = Mosaic,
        Image = Image,
        ImageCrop = ImageCrop
    };

    internal Rect Bounds
    {
        get
        {
            if (Tool == AnnotationTool.Text)
            {
                var size = TextLayoutSize;
                return new Rect(Start, ImageOrientation.SwapsAxes(TextOrientation)
                    ? new Size(size.Height, size.Width) : size);
            }

            if (Points.Count != 0)
            {
                double left = Points[0].X;
                double right = left;
                double top = Points[0].Y;
                double bottom = top;
                foreach (Point point in Points)
                {
                    left = Math.Min(left, point.X);
                    right = Math.Max(right, point.X);
                    top = Math.Min(top, point.Y);
                    bottom = Math.Max(bottom, point.Y);
                }
                return new Rect(new Point(left, top), new Point(right, bottom));
            }

            return Span(Start, End);
        }
    }

    internal Size TextLayoutSize
    {
        get
        {
            var text = FormatText();
            return new Size(TextWidth > 0 ? TextWidth : Math.Max(1, text.WidthIncludingTrailingWhitespace),
                Math.Max(TextBoxHeight, Math.Max(1, text.Height)));
        }
    }

    internal Matrix TextToDocumentMatrix
    {
        get
        {
            var size = TextLayoutSize;
            return ImageOrientation.InFrame(TextOrientation, size.Width, size.Height) * Matrix.CreateTranslation(Start.X, Start.Y);
        }
    }

    internal void Offset(Vector offset)
    {
        Start += offset;
        End += offset;
        for (int i = 0; i < Points.Count; i++)
            Points[i] += offset;
    }

    internal void CopyFrom(AnnotationItem source)
    {
        if (ReferenceEquals(source, this))
            return;
        Start = source.Start;
        End = source.End;
        Color = source.Color;
        Width = source.Width;
        Text = source.Text;
        FontSize = source.FontSize;
        TextWidth = source.TextWidth;
        TextBoxHeight = source.TextBoxHeight;
        TextOrientation = source.TextOrientation;
        Mosaic = source.Mosaic;
        Image = source.Image;
        ImageCrop = source.ImageCrop;
        Points.Clear();
        Points.AddRange(source.Points);
    }

    internal bool ContentEquals(AnnotationItem other)
    {
        static bool Near(double first, double second) => Math.Abs(first - second) < 0.00001;
        static bool SamePoint(Point first, Point second) => ((Vector)(first - second)).SquaredLength < 0.0000000001;
        return Tool == other.Tool && SamePoint(Start, other.Start) && SamePoint(End, other.End)
            && Color == other.Color && Near(Width, other.Width) && Near(FontSize, other.FontSize)
            && Near(TextWidth, other.TextWidth) && Near(TextBoxHeight, other.TextBoxHeight)
            && TextOrientation == other.TextOrientation
            && ReferenceEquals(Image, other.Image)
            && ImageCrop == other.ImageCrop
            && Text.Replace("\r\n", "\n", StringComparison.Ordinal) == other.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            && Points.Count == other.Points.Count && Points.Zip(other.Points).All(pair => SamePoint(pair.First, pair.Second));
    }

    internal void Draw(DrawingContext context)
    {
        var brush = new ImmutableSolidColorBrush(Color);
        var pen = new ImmutablePen(brush, Width, null, PenLineCap.Round, PenLineJoin.Round);

        switch (Tool)
        {
            case AnnotationTool.Image:
                if (Image is not null && ImageCropGeometry.GetDrawRects(Image, ImageCrop, Bounds) is { } draw)
                    using (context.PushClip(Bounds))
                        context.DrawImage(Image, draw.Source, draw.Destination);
                break;
            case AnnotationTool.Arrow:
                DrawArrow(context, pen, brush);
                break;
            case AnnotationTool.Rectangle:
                context.DrawRectangle(null, pen, Bounds);
                break;
            case AnnotationTool.Ellipse:
                Rect bounds = Bounds;
                context.DrawEllipse(null, pen, new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), bounds.Width / 2, bounds.Height / 2);
                break;
            case AnnotationTool.Pen:
            case AnnotationTool.Highlight:
                DrawStroke(context);
                break;
            case AnnotationTool.Text:
                using (context.PushTransform(TextToDocumentMatrix))
                    FormatText().Draw(context, new Point(0, 0));
                break;
            case AnnotationTool.Mosaic:
                if (Mosaic is not null)
                    context.DrawImage(Mosaic, Bounds);
                else
                {
                    var previewBrush = new ImmutableSolidColorBrush(Color.FromArgb(185, 12, 17, 27));
                    context.DrawRectangle(previewBrush, pen, Bounds);
                }
                break;
        }
    }

    internal bool HitTest(Point point, double tolerance)
    {
        if (Tool is AnnotationTool.Text or AnnotationTool.Mosaic or AnnotationTool.Image)
            return Bounds.Inflate(tolerance).Contains(point);

        if (Tool == AnnotationTool.Rectangle)
        {
            Rect outer = Bounds.Inflate(tolerance + Width / 2);
            if (!outer.Contains(point))
                return false;
            Rect inner = Bounds;
            double inset = tolerance + Width / 2;
            if (inner.Width <= inset * 2 || inner.Height <= inset * 2)
                return true;
            inner = inner.Deflate(inset);
            return !inner.Contains(point);
        }

        if (Tool == AnnotationTool.Ellipse)
        {
            Rect bounds = Bounds;
            double radiusX = bounds.Width / 2;
            double radiusY = bounds.Height / 2;
            if (radiusX < 0.5 || radiusY < 0.5)
                return DistanceToSegment(point, Start, End) <= tolerance + Width / 2;
            double x = (point.X - bounds.X - radiusX) / radiusX;
            double y = (point.Y - bounds.Y - radiusY) / radiusY;
            double normalizedDistance = Math.Sqrt(x * x + y * y);
            return Math.Abs(normalizedDistance - 1) * Math.Min(radiusX, radiusY) <= tolerance + Width / 2;
        }

        if (Tool == AnnotationTool.Arrow)
        {
            if (DistanceToSegment(point, Start, End) <= tolerance + Width / 2)
                return true;
            Vector direction = End - Start;
            if (direction.Length < 1)
                return false;
            direction = direction.Normalize();
            Vector perpendicular = new(-direction.Y, direction.X);
            double head = Math.Max(12, Width * 4);
            Point first = End - direction * head + perpendicular * head * 0.5;
            Point second = End - direction * head - perpendicular * head * 0.5;
            return DistanceToSegment(point, End, first) <= tolerance + Width || DistanceToSegment(point, End, second) <= tolerance + Width;
        }

        double radius = tolerance + (Tool == AnnotationTool.Highlight ? Width * 3 : Width / 2);
        if (Points.Count == 1)
            return Point.Distance(point, Points[0]) <= radius;
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(point, Points[i - 1], Points[i]) <= radius)
                return true;
        return false;
    }

    /// <summary>
    /// The text layout used for measuring and drawing. It is built exactly like the editor's
    /// TextPresenter builds its layout (same typeface, size, wrapping width and default line
    /// height), so the text box, the canvas and the exported image place every glyph identically.
    /// </summary>
    internal TextLayout FormatText()
    {
        var key = (Text, FontSize, TextWidth, Color);
        if (_layout is not null && _layoutKey == key)
            return _layout;
        // Do not set a maximum height: narrowing a box must wrap and grow, never trim text.
        _layout = new TextLayout(
            Text,
            TextTypeface,
            FontSize,
            new ImmutableSolidColorBrush(Color),
            TextAlignment.Left,
            TextWrapping.Wrap,
            null,
            null,
            FlowDirection.LeftToRight,
            TextWidth > 0 ? Math.Max(1, TextWidth) : double.PositiveInfinity,
            double.PositiveInfinity,
            double.NaN,
            0,
            0,
            null,
            null);
        _layoutKey = key;
        return _layout;
    }

    /// <summary>The rectangle spanned by two corners in any order (WPF's Rect(Point, Point)).</summary>
    internal static Rect Span(Point first, Point second) => new(
        new Point(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y)),
        new Point(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y)));

    private void DrawArrow(DrawingContext context, IPen pen, IBrush brush)
    {
        Vector direction = End - Start;
        if (direction.Length < 0.5)
            return;
        double length = direction.Length;
        direction = direction.Normalize();
        Vector perpendicular = new(-direction.Y, direction.X);
        double head = Math.Min(length * 0.8, Math.Max(12, Width * 4));
        context.DrawLine(pen, Start, End - direction * head * 0.55);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(End, true);
            path.LineTo(End - direction * head + perpendicular * head * 0.48);
            path.LineTo(End - direction * head * 0.7);
            path.LineTo(End - direction * head - perpendicular * head * 0.48);
            path.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawStroke(DrawingContext context)
    {
        if (Points.Count == 0)
            return;

        var brush = new ImmutableSolidColorBrush(Color, Tool == AnnotationTool.Highlight ? 0.35 : 1);
        double lineWidth = Tool == AnnotationTool.Highlight ? Width * 6 : Width;
        if (Points.Count == 1)
        {
            context.DrawEllipse(brush, null, Points[0], lineWidth / 2, lineWidth / 2);
            return;
        }

        var pen = new ImmutablePen(brush, lineWidth, null, PenLineCap.Round, PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(Points[0], false);
            for (int i = 1; i < Points.Count; i++)
                path.LineTo(Points[i]);
            path.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        Vector segment = end - start;
        if (segment.SquaredLength < 0.0001)
            return Point.Distance(point, start);
        double amount = Math.Clamp(Vector.Dot(point - start, segment) / segment.SquaredLength, 0, 1);
        return Point.Distance(point, start + segment * amount);
    }
}

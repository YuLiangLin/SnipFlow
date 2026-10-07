using System.Globalization;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfVector = System.Windows.Vector;
using WpfColor = System.Windows.Media.Color;

namespace SnipFlow.Editor;

internal sealed class AnnotationItem
{
    internal static Typeface TextTypeface { get; } = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    internal static CultureInfo TextCulture => CultureInfo.CurrentUICulture;

    internal AnnotationTool Tool { get; init; }
    internal WpfPoint Start { get; set; }
    internal WpfPoint End { get; set; }
    internal WpfColor Color { get; set; }
    internal double Width { get; set; }
    internal List<WpfPoint> Points { get; init; } = new();
    internal string Text { get; set; } = string.Empty;
    internal double FontSize { get; set; } = 26;
    internal double TextWidth { get; set; }
    internal double TextBoxHeight { get; set; }
    internal BitmapSource? Mosaic { get; set; }

    internal AnnotationItem Clone() => new()
    {
        Tool = Tool,
        Start = Start,
        End = End,
        Color = Color,
        Width = Width,
        Points = new List<WpfPoint>(Points),
        Text = Text,
        FontSize = FontSize,
        TextWidth = TextWidth,
        TextBoxHeight = TextBoxHeight,
        // BitmapSource instances are frozen before they are stored.
        Mosaic = Mosaic
    };

    internal WpfRect Bounds
    {
        get
        {
            if (Tool == AnnotationTool.Text)
            {
                FormattedText text = FormatText();
                return new WpfRect(Start, new Size(TextWidth > 0 ? TextWidth : Math.Max(1, text.WidthIncludingTrailingWhitespace), Math.Max(TextBoxHeight, Math.Max(1, text.Height))));
            }

            if (Points.Count != 0)
            {
                double left = Points[0].X;
                double right = left;
                double top = Points[0].Y;
                double bottom = top;
                foreach (WpfPoint point in Points)
                {
                    left = Math.Min(left, point.X);
                    right = Math.Max(right, point.X);
                    top = Math.Min(top, point.Y);
                    bottom = Math.Max(bottom, point.Y);
                }
                return new WpfRect(new WpfPoint(left, top), new WpfPoint(right, bottom));
            }

            return new WpfRect(Start, End);
        }
    }

    internal void Offset(WpfVector offset)
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
        Mosaic = source.Mosaic;
        Points.Clear();
        Points.AddRange(source.Points);
    }

    internal bool ContentEquals(AnnotationItem other)
    {
        static bool Near(double first, double second) => Math.Abs(first - second) < 0.00001;
        static bool SamePoint(WpfPoint first, WpfPoint second) => (first - second).LengthSquared < 0.0000000001;
        return Tool == other.Tool && SamePoint(Start, other.Start) && SamePoint(End, other.End)
            && Color == other.Color && Near(Width, other.Width) && Near(FontSize, other.FontSize)
            && Near(TextWidth, other.TextWidth) && Near(TextBoxHeight, other.TextBoxHeight)
            && Text.Replace("\r\n", "\n", StringComparison.Ordinal) == other.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            && Points.Count == other.Points.Count && Points.Zip(other.Points).All(pair => SamePoint(pair.First, pair.Second));
    }

    internal void Draw(DrawingContext context, double pixelsPerDip = 1.0)
    {
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        var pen = new Pen(brush, Width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        switch (Tool)
        {
            case AnnotationTool.Arrow:
                DrawArrow(context, pen, brush);
                break;
            case AnnotationTool.Rectangle:
                context.DrawRectangle(null, pen, Bounds);
                break;
            case AnnotationTool.Ellipse:
                WpfRect bounds = Bounds;
                context.DrawEllipse(null, pen, new WpfPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), bounds.Width / 2, bounds.Height / 2);
                break;
            case AnnotationTool.Pen:
            case AnnotationTool.Highlight:
                DrawStroke(context, brush);
                break;
            case AnnotationTool.Text:
                context.DrawText(FormatText(pixelsPerDip), Start);
                break;
            case AnnotationTool.Mosaic:
                if (Mosaic is not null)
                    context.DrawImage(Mosaic, Bounds);
                else
                {
                    var previewBrush = new SolidColorBrush(WpfColor.FromArgb(185, 12, 17, 27));
                    context.DrawRectangle(previewBrush, pen, Bounds);
                }
                break;
        }
    }

    internal bool HitTest(WpfPoint point, double tolerance)
    {
        if (Tool is AnnotationTool.Text or AnnotationTool.Mosaic)
        {
            WpfRect bounds = Bounds;
            bounds.Inflate(tolerance, tolerance);
            return bounds.Contains(point);
        }

        if (Tool == AnnotationTool.Rectangle)
        {
            WpfRect outer = Bounds;
            outer.Inflate(tolerance + Width / 2, tolerance + Width / 2);
            if (!outer.Contains(point))
                return false;
            WpfRect inner = Bounds;
            double inset = tolerance + Width / 2;
            if (inner.Width <= inset * 2 || inner.Height <= inset * 2)
                return true;
            inner.Inflate(-inset, -inset);
            return !inner.Contains(point);
        }

        if (Tool == AnnotationTool.Ellipse)
        {
            WpfRect bounds = Bounds;
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
            WpfVector direction = End - Start;
            if (direction.Length < 1)
                return false;
            direction.Normalize();
            WpfVector perpendicular = new(-direction.Y, direction.X);
            double head = Math.Max(12, Width * 4);
            WpfPoint first = End - direction * head + perpendicular * head * 0.5;
            WpfPoint second = End - direction * head - perpendicular * head * 0.5;
            return DistanceToSegment(point, End, first) <= tolerance + Width || DistanceToSegment(point, End, second) <= tolerance + Width;
        }

        double radius = tolerance + (Tool == AnnotationTool.Highlight ? Width * 3 : Width / 2);
        if (Points.Count == 1)
            return (point - Points[0]).Length <= radius;
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(point, Points[i - 1], Points[i]) <= radius)
                return true;
        return false;
    }

    internal FormattedText FormatText(double pixelsPerDip = 1.0)
    {
        var formatted = new FormattedText(
            Text,
            TextCulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            FontSize,
            new SolidColorBrush(Color),
            null,
            TextFormattingMode.Ideal,
            pixelsPerDip)
        {
            Trimming = TextTrimming.None,
            TextAlignment = TextAlignment.Left,
            LineHeight = 0
        };
        if (TextWidth > 0)
            formatted.MaxTextWidth = Math.Max(1, TextWidth);
        // Do not set MaxTextHeight: narrowing a box must wrap and grow, never trim text.
        return formatted;
    }

    private void DrawArrow(DrawingContext context, Pen pen, Brush brush)
    {
        WpfVector direction = End - Start;
        if (direction.Length < 0.5)
            return;
        double length = direction.Length;
        direction.Normalize();
        WpfVector perpendicular = new(-direction.Y, direction.X);
        double head = Math.Min(length * 0.8, Math.Max(12, Width * 4));
        context.DrawLine(pen, Start, End - direction * head * 0.55);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(End, true, true);
            path.LineTo(End - direction * head + perpendicular * head * 0.48, true, false);
            path.LineTo(End - direction * head * 0.7, true, false);
            path.LineTo(End - direction * head - perpendicular * head * 0.48, true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawStroke(DrawingContext context, SolidColorBrush brush)
    {
        if (Points.Count == 0)
            return;

        if (Tool == AnnotationTool.Highlight)
        {
            brush = new SolidColorBrush(Color) { Opacity = 0.35 };
            brush.Freeze();
        }
        double lineWidth = Tool == AnnotationTool.Highlight ? Width * 6 : Width;
        if (Points.Count == 1)
        {
            context.DrawEllipse(brush, null, Points[0], lineWidth / 2, lineWidth / 2);
            return;
        }

        var pen = new Pen(brush, lineWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(Points[0], false, false);
            for (int i = 1; i < Points.Count; i++)
                path.LineTo(Points[i], true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static double DistanceToSegment(WpfPoint point, WpfPoint start, WpfPoint end)
    {
        WpfVector segment = end - start;
        if (segment.LengthSquared < 0.0001)
            return (point - start).Length;
        double amount = Math.Clamp(WpfVector.Multiply(point - start, segment) / segment.LengthSquared, 0, 1);
        return (point - (start + segment * amount)).Length;
    }
}

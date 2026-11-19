namespace SnipFlow.Editor;

/// <summary>One normalized outline shared by the document, hit testing and tool previews.</summary>
internal static class ShapeGeometry
{
    internal static Geometry Create(ShapeKind kind, Rect bounds, int orientation = 0)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!ImageOrientation.IsValid(orientation)) throw new ArgumentOutOfRangeException(nameof(orientation));
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width < 0 || bounds.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        var geometry = new StreamGeometry();
        if (bounds.Width == 0 || bounds.Height == 0) return geometry;
        using (var path = geometry.Open())
        {
            void Polygon(params Point[] points)
            {
                path.BeginFigure(points[0], true);
                foreach (var point in points.Skip(1)) path.LineTo(point);
                path.EndFigure(true);
            }
            void Curve(double x1, double y1, double x2, double y2, double x, double y) =>
                path.CubicBezierTo(new Point(x1, y1), new Point(x2, y2), new Point(x, y));
            switch (kind)
            {
                case ShapeKind.RoundedRectangle:
                    path.BeginFigure(new Point(.18, 0), true);
                    path.LineTo(new Point(.82, 0)); Curve(.92, 0, 1, .08, 1, .18);
                    path.LineTo(new Point(1, .82)); Curve(1, .92, .92, 1, .82, 1);
                    path.LineTo(new Point(.18, 1)); Curve(.08, 1, 0, .92, 0, .82);
                    path.LineTo(new Point(0, .18)); Curve(0, .08, .08, 0, .18, 0);
                    path.EndFigure(true);
                    break;
                case ShapeKind.Triangle: Polygon(new(.5, 0), new(1, 1), new(0, 1)); break;
                case ShapeKind.RightTriangle: Polygon(new(0, 0), new(1, 1), new(0, 1)); break;
                case ShapeKind.Diamond: Polygon(new(.5, 0), new(1, .5), new(.5, 1), new(0, .5)); break;
                case ShapeKind.Pentagon: Polygon(RegularPolygon(5)); break;
                case ShapeKind.Hexagon: Polygon(new(.25, 0), new(.75, 0), new(1, .5), new(.75, 1), new(.25, 1), new(0, .5)); break;
                case ShapeKind.Star:
                    Polygon(Enumerable.Range(0, 10).Select(index =>
                    {
                        var angle = -Math.PI / 2 + index * Math.PI / 5;
                        var radius = index % 2 == 0 ? .5 : .22;
                        return new Point(.5 + radius * Math.Cos(angle), .5 + radius * Math.Sin(angle));
                    }).ToArray());
                    break;
                case ShapeKind.Heart:
                    path.BeginFigure(new Point(.5, 1), true);
                    Curve(.4, .86, 0, .54, 0, .26); Curve(0, -.02, .36, -.1, .5, .2);
                    Curve(.64, -.1, 1, -.02, 1, .26); Curve(1, .54, .6, .86, .5, 1);
                    path.EndFigure(true);
                    break;
                case ShapeKind.Plus:
                    Polygon(new(.34, 0), new(.66, 0), new(.66, .34), new(1, .34), new(1, .66), new(.66, .66), new(.66, 1), new(.34, 1), new(.34, .66), new(0, .66), new(0, .34), new(.34, .34));
                    break;
                case ShapeKind.Cloud:
                    path.BeginFigure(new Point(.18, .82), true);
                    Curve(-.04, .82, -.05, .5, .12, .44); Curve(.02, .2, .22, .04, .39, .15);
                    Curve(.47, -.08, .8, -.02, .81, .22); Curve(1.02, .16, 1.08, .48, .91, .58);
                    Curve(1.07, .79, .86, 1.02, .68, .87); Curve(.57, 1.08, .29, 1, .3, .83);
                    Curve(.26, .85, .22, .85, .18, .82);
                    path.EndFigure(true);
                    break;
                case ShapeKind.Callout:
                    Polygon(new(0, 0), new(1, 0), new(1, .75), new(.48, .75), new(.22, 1), new(.26, .75), new(0, .75));
                    break;
                case ShapeKind.RightArrow:
                    Polygon(new(0, .3), new(.62, .3), new(.62, 0), new(1, .5), new(.62, 1), new(.62, .7), new(0, .7));
                    break;
                case ShapeKind.LeftArrow:
                    Polygon(new(1, .3), new(.38, .3), new(.38, 0), new(0, .5), new(.38, 1), new(.38, .7), new(1, .7));
                    break;
                case ShapeKind.UpArrow:
                    Polygon(new(.3, 1), new(.3, .38), new(0, .38), new(.5, 0), new(1, .38), new(.7, .38), new(.7, 1));
                    break;
                case ShapeKind.DownArrow:
                    Polygon(new(.3, 0), new(.3, .62), new(0, .62), new(.5, 1), new(1, .62), new(.7, .62), new(.7, 0));
                    break;
                case ShapeKind.DoubleArrow:
                    Polygon(new(0, .5), new(.28, 0), new(.28, .3), new(.72, .3), new(.72, 0), new(1, .5), new(.72, 1), new(.72, .7), new(.28, .7), new(.28, 1));
                    break;
                case ShapeKind.Trapezoid: Polygon(new(.22, 0), new(.78, 0), new(1, 1), new(0, 1)); break;
                case ShapeKind.Parallelogram: Polygon(new(.25, 0), new(1, 0), new(.75, 1), new(0, 1)); break;
            }
        }
        var localBounds = geometry.Bounds;
        geometry.Transform = new MatrixTransform(Matrix.CreateTranslation(-localBounds.X, -localBounds.Y)
            * Matrix.CreateScale(1 / localBounds.Width, 1 / localBounds.Height)
            * ImageOrientation.InFrame(orientation, 1, 1)
            * Matrix.CreateScale(bounds.Width, bounds.Height) * Matrix.CreateTranslation(bounds.X, bounds.Y));
        return geometry;
    }

    private static Point[] RegularPolygon(int sides) => Enumerable.Range(0, sides).Select(index =>
    {
        var angle = -Math.PI / 2 + index * 2 * Math.PI / sides;
        return new Point(.5 + .5 * Math.Cos(angle), .5 + .5 * Math.Sin(angle));
    }).ToArray();
}

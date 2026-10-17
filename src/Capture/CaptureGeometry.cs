namespace SnipFlow.Capture;

/// <summary>
/// System.Drawing.Rectangle semantics for <see cref="PixelRect"/>: right and bottom edges are exclusive
/// for points, and rectangles that only touch do not intersect.
/// </summary>
internal static class CaptureGeometry
{
    internal static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    internal static bool Contains(PixelRect rectangle, PixelPoint point) =>
        point.X >= rectangle.X && point.X < rectangle.Right && point.Y >= rectangle.Y && point.Y < rectangle.Bottom;

    /// <summary>True when <paramref name="inner"/> lies completely inside <paramref name="outer"/>.</summary>
    internal static bool Contains(PixelRect outer, PixelRect inner) =>
        outer.X <= inner.X && inner.Right <= outer.Right && outer.Y <= inner.Y && inner.Bottom <= outer.Bottom;

    internal static bool IntersectsWith(PixelRect first, PixelRect second) =>
        second.X < first.Right && first.X < second.Right && second.Y < first.Bottom && first.Y < second.Bottom;

    /// <summary>The overlapping area, or an empty rectangle when the rectangles do not overlap.</summary>
    internal static PixelRect Intersect(PixelRect first, PixelRect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : default;
    }

    internal static PixelRect Union(PixelRect first, PixelRect second) => FromLTRB(Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y), Math.Max(first.Right, second.Right), Math.Max(first.Bottom, second.Bottom));

    internal static bool IsEmpty(PixelRect rectangle) => rectangle.Width <= 0 || rectangle.Height <= 0;

    /// <summary>The parts of <paramref name="region"/> that none of <paramref name="covers"/> overlap.</summary>
    internal static List<PixelRect> Subtract(PixelRect region, IEnumerable<PixelRect> covers)
    {
        var uncovered = new List<PixelRect> { region };
        foreach (var cover in covers)
        {
            var remaining = new List<PixelRect>();
            foreach (var piece in uncovered)
            {
                var intersection = Intersect(piece, cover);
                if (IsEmpty(intersection)) { remaining.Add(piece); continue; }
                if (piece.Y < intersection.Y)
                    remaining.Add(FromLTRB(piece.X, piece.Y, piece.Right, intersection.Y));
                if (intersection.Bottom < piece.Bottom)
                    remaining.Add(FromLTRB(piece.X, intersection.Bottom, piece.Right, piece.Bottom));
                if (piece.X < intersection.X)
                    remaining.Add(FromLTRB(piece.X, intersection.Y, intersection.X, intersection.Bottom));
                if (intersection.Right < piece.Right)
                    remaining.Add(FromLTRB(intersection.Right, intersection.Y, piece.Right, intersection.Bottom));
            }
            uncovered = remaining;
            if (uncovered.Count == 0) break;
        }
        return uncovered;
    }
}

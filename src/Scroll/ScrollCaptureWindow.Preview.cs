namespace SnipFlow.Scroll;

public sealed partial class ScrollCaptureWindow
{
    private void RefreshCollectedPreview()
    {
        if (_frames.Count == 0)
        {
            _preview.Source = null;
            _previewBorder.Height = 58;
            return;
        }
        const int maximumWidth = 94;
        const int maximumHeight = 158;
        var bodyHeight = BodyHeight;
        var firstDocumentRow = checked(_positions[0] + _topTrim);
        var fromRow = checked((_rangeStart ?? firstDocumentRow) - firstDocumentRow);
        var endRow = checked((_rangeEnd ?? (_positions[^1] + _targetBounds.Height - _bottomTrim)) - firstDocumentRow);
        var totalHeight = (long)endRow - fromRow;
        var scale = Math.Min((double)maximumWidth / _targetBounds.Width, (double)maximumHeight / totalHeight);
        var width = Math.Clamp((int)Math.Ceiling(_targetBounds.Width * scale), 1, maximumWidth);
        var height = Math.Clamp((int)Math.Ceiling(totalHeight * scale), 1, maximumHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            long documentRow = 0;
            for (var index = 0; index < _frames.Count; index++)
            {
                var skipped = index == 0 ? 0 : checked(bodyHeight - (_positions[index] - _positions[index - 1]));
                var copiedRows = bodyHeight - skipped;
                var from = Math.Max(documentRow, fromRow);
                var to = Math.Min(documentRow + copiedRows, endRow);
                if (to <= from) { documentRow += copiedRows; continue; }
                var image = Crop(_frames[index]);
                drawing.PushClip(new RectangleGeometry(new Rect(0, (from - fromRow) * scale,
                    _targetBounds.Width * scale, (to - from) * scale)));
                drawing.DrawImage(image, new Rect(0, (documentRow - skipped - fromRow) * scale,
                    _targetBounds.Width * scale, bodyHeight * scale));
                drawing.Pop();
                documentRow += copiedRows;
            }
        }
        // Render only a bounded thumbnail, never an extra full-resolution stitched image.
        var thumbnail = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        thumbnail.Render(visual);
        thumbnail.Freeze();
        _preview.Source = thumbnail;
        _previewBorder.Height = Math.Clamp(height + 2, 58, maximumHeight + 2);
    }
}

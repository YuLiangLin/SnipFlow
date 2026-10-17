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
        var frameWidth = FrameWidth;
        var topTrim = _topTrim;
        var firstDocumentRow = checked(_positions[0] + _topTrim);
        var fromRow = checked((_rangeStart ?? firstDocumentRow) - firstDocumentRow);
        var endRow = checked((_rangeEnd ?? (_positions[^1] + FrameHeight - _bottomTrim)) - firstDocumentRow);
        var totalHeight = (long)endRow - fromRow;
        var scale = Math.Min((double)maximumWidth / frameWidth, (double)maximumHeight / totalHeight);
        var width = Math.Clamp((int)Math.Ceiling(frameWidth * scale), 1, maximumWidth);
        var height = Math.Clamp((int)Math.Ceiling(totalHeight * scale), 1, maximumHeight);
        // Render only a bounded thumbnail, never an extra full-resolution stitched image.
        var thumbnail = ImageTools.Render(new PixelSize(width, height), drawing =>
        {
            using var quality = drawing.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality });
            long documentRow = 0;
            for (var index = 0; index < _frames.Count; index++)
            {
                var skipped = index == 0 ? 0 : checked(bodyHeight - (_positions[index] - _positions[index - 1]));
                var copiedRows = bodyHeight - skipped;
                var from = Math.Max(documentRow, fromRow);
                var to = Math.Min(documentRow + copiedRows, endRow);
                if (to <= from) { documentRow += copiedRows; continue; }
                // The trimmed body rows of the raw frame, drawn without creating a cropped copy. Clip edges
                // snap to whole thumbnail pixels so adjacent frames do not leave an antialiased seam.
                var clipTop = Math.Round((from - fromRow) * scale);
                var clipBottom = Math.Round((to - fromRow) * scale);
                using (drawing.PushClip(new Rect(0, clipTop, frameWidth * scale, Math.Max(0, clipBottom - clipTop))))
                    drawing.DrawImage(_frames[index], new Rect(0, topTrim, frameWidth, bodyHeight),
                        new Rect(0, (documentRow - skipped - fromRow) * scale, frameWidth * scale, bodyHeight * scale));
                documentRow += copiedRows;
            }
        });
        _preview.Source = thumbnail;
        _previewBorder.Height = Math.Clamp(height + 2, 58, maximumHeight + 2);
    }
}

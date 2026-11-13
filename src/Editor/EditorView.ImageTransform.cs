using SnipFlow.Services;

namespace SnipFlow.Editor;

public sealed partial class EditorView
{
    public int SelectedImageCount => _selection.Count(item => item.Tool == AnnotationTool.Image);

    internal void ApplyImageTransform(ImageTransformCommand command, bool entireCanvas = false)
    {
        VerifyAccess();
        var orientation = ImageOrientation.ForCommand(command);
        if (_image is null) throw new InvalidOperationException(I18n.T("請先開啟或擷取影像。"));
        PrepareObjectCommand();
        var selectedIndices = _annotations.Select((item, index) => (item, index))
            .Where(pair => _selection.Contains(pair.item)).Select(pair => pair.index).ToArray();
        var imageIndices = selectedIndices.Where(index => _annotations[index].Tool == AnnotationTool.Image).ToArray();
        var transformSelection = !entireCanvas && IsCollage && imageIndices.Length != 0;
        var prepared = _annotations.Select(item => item.Clone()).ToList();
        foreach (var item in prepared) ValidateTransformAnnotation(item);
        var board = _image;
        var boardWidth = board.PixelSize.Width;
        var boardHeight = board.PixelSize.Height;
        if (transformSelection)
        {
            foreach (var index in imageIndices)
            {
                var item = prepared[index];
                var bounds = item.Bounds;
                ValidateTransformBounds(bounds);
                item.ImageCrop = ImageCropGeometry.Transform(item.ImageCrop, orientation);
                item.Image = ImageOrientation.TransformBitmap(item.Image
                    ?? throw new InvalidOperationException(I18n.T("選取的圖片資料無法旋轉或翻轉。")), orientation);
                var width = ImageOrientation.SwapsAxes(orientation) ? bounds.Height : bounds.Width;
                var height = ImageOrientation.SwapsAxes(orientation) ? bounds.Width : bounds.Height;
                // Keep the center and scale when possible; fit the entire image inside this board.
                var scale = Math.Min(1, Math.Min(boardWidth / width, boardHeight / height));
                width *= scale;
                height *= scale;
                var left = Math.Clamp(bounds.X + bounds.Width / 2 - width / 2, 0, Math.Max(0, boardWidth - width));
                var top = Math.Clamp(bounds.Y + bounds.Height / 2 - height / 2, 0, Math.Max(0, boardHeight - height));
                item.Start = new Point(left, top);
                item.End = new Point(left + width, top + height);
            }
            // A moved image changes the pixels beneath a mask. Prepare replacement masks off-state.
            PixelBuffer? pixelCache = null;
            foreach (var item in prepared.Where(item => item.Tool == AnnotationTool.Mosaic))
            {
                var start = item.Start;
                var end = item.End;
                PrepareMosaic(item, board, prepared, ref pixelCache);
                item.Start = start;
                item.End = end;
            }
        }
        else
        {
            var matrix = ImageOrientation.InFrame(orientation, boardWidth, boardHeight);
            // A blank collage board stays a shared blank board; only its dimensions change.
            board = IsBoard(board)
                ? CreateBoard(ImageOrientation.SwapsAxes(orientation) ? boardHeight : boardWidth, ImageOrientation.SwapsAxes(orientation) ? boardWidth : boardHeight)
                : ImageOrientation.TransformBitmap(board, orientation);
            foreach (var item in prepared)
            {
                if (item.Tool == AnnotationTool.Text)
                {
                    var bounds = ImageOrientation.TransformBounds(item.Bounds, matrix);
                    ValidateTransformBounds(bounds);
                    item.TextOrientation = ImageOrientation.Compose(item.TextOrientation, orientation);
                    item.Start = item.End = bounds.TopLeft;
                }
                else
                {
                    item.Start = matrix.Transform(item.Start);
                    item.End = matrix.Transform(item.End);
                    for (var index = 0; index < item.Points.Count; index++)
                        item.Points[index] = matrix.Transform(item.Points[index]);
                    if (item.Tool == AnnotationTool.Image)
                    {
                        item.ImageCrop = ImageCropGeometry.Transform(item.ImageCrop, orientation);
                        item.Image = ImageOrientation.TransformBitmap(item.Image
                            ?? throw new InvalidOperationException(I18n.T("選取的圖片資料無法旋轉或翻轉。")), orientation);
                    }
                    else if (item.Tool == AnnotationTool.Mosaic)
                        item.Mosaic = ImageOrientation.TransformBitmap(item.Mosaic
                            ?? throw new InvalidOperationException(I18n.T("馬賽克資料無法旋轉或翻轉。")), orientation);
                }
            }
        }
        foreach (var item in prepared) ValidateTransformAnnotation(item);
        // All payloads and caches are ready before one undoable document replacement.
        RememberMutation();
        _annotations.Clear();
        _annotations.AddRange(prepared);
        SetBoard(board);
        SetSelection(selectedIndices.Select(index => _annotations[index]));
        if (_fitMode) FitToView();
        else { ClampPan(); RefreshView(); }
        FinishMutation();
    }

    private static void ValidateTransformAnnotation(AnnotationItem item)
    {
        ValidateTransformPoint(item.Start);
        ValidateTransformPoint(item.End);
        foreach (var point in item.Points) ValidateTransformPoint(point);
        if (!TextFonts.TryNormalizeFamily(item.TextFontFamily, out var fontFamily)
            || (item.Tool != AnnotationTool.Text && fontFamily != TextFonts.DefaultFamily))
            throw new InvalidOperationException(I18n.T("圖面資料無法旋轉或翻轉。"));
        if (!ImageOrientation.IsValid(item.TextOrientation) || (item.Tool != AnnotationTool.Text && item.TextOrientation != 0)
            || !double.IsFinite(item.Width) || item.Width is < 1 or > 32
            || !double.IsFinite(item.FontSize) || item.FontSize is < 12 or > 240
            || !double.IsFinite(item.TextWidth) || item.TextWidth is < 0 or > 1_000_000
            || !double.IsFinite(item.TextBoxHeight) || item.TextBoxHeight is < 0 or > 1_000_000
            || (item.ImageCrop is { } crop && (item.Tool != AnnotationTool.Image || !ImageCropGeometry.IsValid(crop))))
            throw new InvalidOperationException(I18n.T("圖面資料無法旋轉或翻轉。"));
        if (item.Tool is AnnotationTool.Text or AnnotationTool.Image or AnnotationTool.Mosaic)
            ValidateTransformBounds(item.Bounds);
    }

    private static void ValidateTransformPoint(Point point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > 1_000_000 || Math.Abs(point.Y) > 1_000_000)
            throw new InvalidOperationException(I18n.T("圖面座標超出可旋轉或翻轉的範圍。"));
    }

    private static void ValidateTransformBounds(Rect bounds)
    {
        if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException(I18n.T("圖面座標超出可旋轉或翻轉的範圍。"));
        ValidateTransformPoint(bounds.TopLeft);
        ValidateTransformPoint(bounds.BottomRight);
    }
}

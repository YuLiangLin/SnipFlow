using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace SnipFlow.Editor;

public sealed partial class EditorView
{
    private ImageDropPreview? _imageDropPreview;

    internal bool ShowImageDropPreview(BitmapSource image, WpfPoint editorPoint)
    {
        VerifyAccess();
        if (!TryGetImageDropPoint(editorPoint, out WpfPoint center))
        {
            ClearImageDropPreview();
            return false;
        }
        _imageDropPreview ??= CreateImageDropPreview();
        _imageDropPreview.Width = PixelWidth;
        _imageDropPreview.Height = PixelHeight;
        _imageDropPreview.SetImage(image, InitialImageBounds(image, center));
        return true;
    }

    internal bool AddDroppedImage(BitmapSource image, WpfPoint editorPoint)
    {
        VerifyAccess();
        if (!TryGetImageDropPoint(editorPoint, out WpfPoint center)) return false;
        // Both the preview and the inserted object use the same image-pixel bounds.
        // Only this final drop passes through the document's mutation/undo path.
        AddImageAt(image, center);
        return true;
    }

    internal void ClearImageDropPreview()
    {
        VerifyAccess();
        if (_imageDropPreview is null) return;
        _documentLayer.Children.Remove(_imageDropPreview);
        _imageDropPreview = null;
    }

    private bool TryGetImageDropPoint(WpfPoint editorPoint, out WpfPoint point)
    {
        point = default;
        if (!IsCollage || !HasImage || !IsEnabled || !double.IsFinite(editorPoint.X) || !double.IsFinite(editorPoint.Y))
            return false;
        WpfPoint viewportPoint = TranslatePoint(editorPoint, _viewport);
        if (viewportPoint.X < 0 || viewportPoint.Y < 0 || viewportPoint.X >= _viewport.ActualWidth || viewportPoint.Y >= _viewport.ActualHeight)
            return false;
        point = ViewportToImage(viewportPoint);
        return double.IsFinite(point.X) && double.IsFinite(point.Y) &&
            point.X >= 0 && point.Y >= 0 && point.X < PixelWidth && point.Y < PixelHeight;
    }

    private ImageDropPreview CreateImageDropPreview()
    {
        // This visual is never part of DrawScene or the annotation/session lists.
        // It shares the document transform, so zoom and pan apply without rounding.
        var preview = new ImageDropPreview(this) { IsHitTestVisible = false, ClipToBounds = true };
        System.Windows.Controls.Canvas.SetZIndex(preview, int.MaxValue);
        _documentLayer.Children.Add(preview);
        return preview;
    }

    private sealed class ImageDropPreview(EditorView editor) : FrameworkElement
    {
        private BitmapSource? _image;
        private WpfRect _bounds;

        internal void SetImage(BitmapSource image, WpfRect bounds)
        {
            _image = image;
            _bounds = bounds;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext context)
        {
            if (_image is null) return;
            context.PushOpacity(0.45);
            context.DrawImage(_image, _bounds);
            context.Pop();
            context.DrawRectangle(null, new Pen(BrushFor("#4AA5FF"), 1.5 / editor._zoom) { DashStyle = DashStyles.Dash }, _bounds);
        }
    }
}

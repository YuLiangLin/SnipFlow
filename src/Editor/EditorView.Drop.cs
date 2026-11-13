using SnipFlow.Services;

namespace SnipFlow.Editor;

// The editor has no drag-and-drop handlers of its own: file drops and the history thumbnail's
// in-process format route through it untouched to the handlers MainWindow attaches.
public sealed partial class EditorView
{
    private ImageDropPreview? _imageDropPreview;

    internal bool ShowImageDropPreview(Bitmap image, Point editorPoint)
    {
        VerifyAccess();
        if (!TryGetImageDropPoint(editorPoint, out Point center))
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

    internal bool AddDroppedImage(Bitmap image, Point editorPoint)
    {
        VerifyAccess();
        if (!TryGetImageDropPoint(editorPoint, out Point center)) return false;
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

    private bool TryGetImageDropPoint(Point editorPoint, out Point point)
    {
        point = default;
        if (!IsCollage || !HasImage || !IsEnabled || IsImageCropping || !double.IsFinite(editorPoint.X) || !double.IsFinite(editorPoint.Y))
            return false;
        if (this.TranslatePoint(editorPoint, _viewport) is not Point viewportPoint)
            return false;
        if (viewportPoint.X < 0 || viewportPoint.Y < 0 || viewportPoint.X >= _viewport.Bounds.Width || viewportPoint.Y >= _viewport.Bounds.Height)
            return false;
        point = ViewportToImage(viewportPoint);
        return double.IsFinite(point.X) && double.IsFinite(point.Y) &&
            point.X >= 0 && point.Y >= 0 && point.X < PixelWidth && point.Y < PixelHeight;
    }

    private ImageDropPreview CreateImageDropPreview()
    {
        // This visual is never part of DrawScene or the annotation/session lists.
        // It shares the document transform, so zoom and pan apply without rounding.
        var preview = new ImageDropPreview(this) { IsHitTestVisible = false, ClipToBounds = true, UseLayoutRounding = false, ZIndex = int.MaxValue };
        _documentLayer.Children.Add(preview);
        return preview;
    }

    private sealed class ImageDropPreview(EditorView editor) : Control
    {
        private Bitmap? _image;
        private Rect _bounds;

        internal void SetImage(Bitmap image, Rect bounds)
        {
            _image = image;
            _bounds = bounds;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (_image is null) return;
            using (context.PushRenderOptions(SceneRenderOptions))
            using (context.PushOpacity(0.45))
                context.DrawImage(_image, _bounds);
            context.DrawRectangle(null, new Pen(ThemeService.Brush("SelectionBorder"), 1.5 / editor._zoom, DashStyle.Dash), _bounds);
        }
    }
}

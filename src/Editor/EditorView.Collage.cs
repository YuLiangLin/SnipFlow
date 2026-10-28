using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using SnipFlow.Services;

namespace SnipFlow.Editor;

internal enum CollageLayout { Grid, Vertical, Horizontal }
internal enum ObjectAlignment { Left, Center, Right, Top, Middle, Bottom }
internal enum ObjectOrder { Front, Forward, Backward, Back }
internal sealed record EditorSessionSnapshot(Bitmap Image, List<AnnotationItem> Annotations, bool HasEdits, bool IsCollage);

public sealed partial class EditorView
{
    private const int MaximumImages = 24;
    private static readonly Dictionary<(int Width, int Height), WeakReference<Bitmap>> Boards = new();
    private static readonly ConditionalWeakTable<Bitmap, object> BoardImages = new();
    private static readonly object BoardMarker = new();
    private readonly HashSet<AnnotationItem> _selection = new();
    private readonly Dictionary<AnnotationItem, AnnotationItem> _transformSources = new();
    private readonly Dictionary<AnnotationItem, AnnotationItem> _propertySources = new();
    private Point? _marqueeStart;
    private Rect? _marquee;
    private HashSet<AnnotationItem>? _marqueeOriginal;
    public bool IsCollage { get; private set; }
    public int SelectionCount => _selection.Count;
    public int ImageCount => _annotations.Count(item => item.Tool == AnnotationTool.Image);
    public int PixelWidth => _image?.PixelSize.Width ?? 0;
    public int PixelHeight => _image?.PixelSize.Height ?? 0;

    internal void NewCollage()
    {
        VerifyAccess();
        RestoreSession(CreateBoard(1920, 1080), Array.Empty<AnnotationItem>(), hasEdits: true, isCollage: true);
        _color = Color.FromRgb(17, 24, 39);
        _tool = AnnotationTool.Select;
        UpdateCursor();
        RefreshLanguage();
    }

    internal void CreateCollage()
    {
        VerifyAccess();
        if (IsCollage) return;
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
        Bitmap? original = _image;
        int width = Math.Max(1920, (original?.PixelSize.Width ?? 0) + 80);
        int height = Math.Max(1080, (original?.PixelSize.Height ?? 0) + 80);
        Bitmap board = CreateBoard(width, height);
        if (original is null)
            LoadImage(board);
        RememberMutation();
        if (original is not null)
        {
            foreach (AnnotationItem item in _annotations) item.Offset(new Vector(40, 40));
            _annotations.Insert(0, new AnnotationItem
            {
                Tool = AnnotationTool.Image, Image = original,
                Start = new Point(40, 40), End = new Point(40 + original.PixelSize.Width, 40 + original.PixelSize.Height), Width = 1
            });
        }
        IsCollage = true;
        _color = Color.FromRgb(32, 32, 32);
        _tool = AnnotationTool.Select;
        SetBoard(board);
        SelectItem(original is null ? null : _annotations[0]);
        FinishMutation();
        FitToView();
        RefreshLanguage();
    }

    internal void AddImages(IReadOnlyList<Bitmap> images)
    {
        VerifyAccess();
        var prepared = PrepareImagesForInsertion(images, null);
        if (prepared.Count != 0) InsertPreparedImages(prepared);
    }

    internal void AddImageAt(Bitmap image, Point center)
    {
        VerifyAccess();
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !InsideImage(center))
            throw new ArgumentOutOfRangeException(nameof(center));
        InsertPreparedImages(PrepareImagesForInsertion(new[] { image }, center));
    }

    internal void ValidateImageInsertion(Bitmap image)
    {
        VerifyAccess();
        ValidateImagesForInsertion(new[] { image });
    }

    private void ValidateImagesForInsertion(IReadOnlyList<Bitmap> images)
    {
        if (!IsCollage) throw new InvalidOperationException(I18n.T("請先開啟多張合圖。"));
        if (ImageCount + images.Count > MaximumImages)
            throw new InvalidOperationException(I18n.T("每份合圖最多可放入 24 張圖片。"));
        long pixels = _annotations.Where(item => item.Image is not null).Sum(item => (long)item.Image!.PixelSize.Width * item.Image.PixelSize.Height);
        foreach (Bitmap input in images)
        {
            if (input.PixelSize.Width < 1 || input.PixelSize.Height < 1) throw new ArgumentException(I18n.T("影像尺寸不得為零。"));
            pixels += (long)input.PixelSize.Width * input.PixelSize.Height;
            if (pixels > 80_000_000) throw new InvalidOperationException(I18n.T("圖片總尺寸過大，請減少張數或縮小原圖。"));
        }
    }

    private List<AnnotationItem> PrepareImagesForInsertion(IReadOnlyList<Bitmap> images, Point? center)
    {
        ValidateImagesForInsertion(images);
        var prepared = new List<AnnotationItem>();
        foreach (Bitmap input in images)
        {
            Bitmap image = AtDocumentDpi(input);
            Rect bounds = InitialImageBounds(image, center, ImageCount + prepared.Count);
            prepared.Add(new AnnotationItem { Tool = AnnotationTool.Image, Image = image, Start = bounds.TopLeft, End = bounds.BottomRight, Width = 1 });
        }
        return prepared;
    }

    private Rect InitialImageBounds(Bitmap image, Point? center, int index = 0)
    {
        int imageWidth = image.PixelSize.Width, imageHeight = image.PixelSize.Height;
        double scale = Math.Min(1, Math.Min(Math.Max(1, PixelWidth - 80.0) / imageWidth, Math.Max(1, PixelHeight - 80.0) / imageHeight));
        double width = imageWidth * scale, height = imageHeight * scale;
        double offset = 40 + (index % 5) * 24;
        double x = Math.Clamp(center?.X - width / 2 ?? offset, 0, Math.Max(0, PixelWidth - width));
        double y = Math.Clamp(center?.Y - height / 2 ?? offset, 0, Math.Max(0, PixelHeight - height));
        return new Rect(x, y, width, height);
    }

    private void InsertPreparedImages(List<AnnotationItem> prepared)
    {
        PrepareObjectCommand();
        RememberMutation();
        // New photographs stay below text and callouts, while preserving photograph order.
        int index = _annotations.FindIndex(item => item.Tool != AnnotationTool.Image);
        _annotations.InsertRange(index < 0 ? _annotations.Count : index, prepared);
        RefreshMosaics();
        SetSelection(prepared);
        _tool = AnnotationTool.Select;
        EnsureRenderCache();
        FinishMutation();
        UpdateCursor();
    }

    internal void ArrangeImages(CollageLayout layout)
    {
        VerifyAccess();
        PrepareObjectCommand();
        var images = _annotations.Where(item => item.Tool == AnnotationTool.Image).ToList();
        if (_selection.Count(item => item.Tool == AnnotationTool.Image) >= 2)
            images = images.Where(_selection.Contains).ToList();
        if (images.Count == 0) return;
        const double padding = 40, gap = 28;
        var placements = new List<Rect>();
        double right = PixelWidth, bottom = PixelHeight;
        if (layout == CollageLayout.Horizontal)
        {
            double x = padding, available = Math.Max(1, PixelHeight - 2 * padding);
            foreach (AnnotationItem item in images)
            {
                var sourceWidth = item.Bounds.Width;
                var sourceHeight = item.Bounds.Height;
                double scale = Math.Min(1, available / sourceHeight);
                double width = sourceWidth * scale, height = sourceHeight * scale;
                placements.Add(new Rect(x, padding, width, height));
                x += width + gap;
            }
            right = Math.Max(right, x - gap + padding);
        }
        else
        {
            int columns = layout == CollageLayout.Vertical ? 1 : (int)Math.Ceiling(Math.Sqrt(images.Count));
            double cell = Math.Max(1, (PixelWidth - 2 * padding - (columns - 1) * gap) / columns);
            double y = padding;
            for (int row = 0; row < images.Count; row += columns)
            {
                double rowHeight = 0;
                for (int column = 0; column < columns && row + column < images.Count; column++)
                {
                    var item = images[row + column];
                    double sourceWidth = item.Bounds.Width, sourceHeight = item.Bounds.Height;
                    double scale = Math.Min(1, cell / sourceWidth);
                    double width = sourceWidth * scale, height = sourceHeight * scale;
                    placements.Add(new Rect(padding + column * (cell + gap), y, width, height));
                    rowHeight = Math.Max(rowHeight, height);
                }
                y += rowHeight + gap;
            }
            bottom = Math.Max(bottom, y - gap + padding);
        }
        Bitmap board = CreateBoard((int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
        RememberMutation();
        for (int i = 0; i < images.Count; i++)
        {
            images[i].Start = placements[i].TopLeft;
            images[i].End = placements[i].BottomRight;
        }
        SetBoard(board);
        RefreshMosaics();
        SetSelection(images);
        FinishMutation();
        FitToView();
    }

    internal void ResizeCanvas(int width, int height)
    {
        VerifyAccess();
        PrepareObjectCommand();
        if (!IsCollage) return;
        if (_annotations.Any(item => item.Bounds.Right > width || item.Bounds.Bottom > height))
            throw new InvalidOperationException(I18n.T("物件超出新畫布範圍，請先縮小或移動物件。"));
        Bitmap board = CreateBoard(width, height);
        if (width == PixelWidth && height == PixelHeight) return;
        RememberMutation();
        SetBoard(board);
        FinishMutation();
        FitToView();
    }

    internal void FitCanvasToContent()
    {
        if (!IsCollage || _annotations.Count == 0) return;
        PrepareObjectCommand();
        Rect bounds = BoundsOf(_annotations);
        Bitmap board = CreateBoard((int)Math.Ceiling(bounds.Width + 80), (int)Math.Ceiling(bounds.Height + 80));
        RememberMutation();
        foreach (AnnotationItem item in _annotations) item.Offset(new Vector(40 - bounds.Left, 40 - bounds.Top));
        SetBoard(board);
        RefreshMosaics();
        FinishMutation();
        FitToView();
    }

    public void SelectAll()
    {
        VerifyAccess();
        PrepareObjectCommand();
        SetSelection(_annotations);
        _tool = AnnotationTool.Select;
        UpdateCursor();
    }

    public void DuplicateSelection()
    {
        VerifyAccess();
        PrepareObjectCommand();
        if (_selection.Count == 0) return;
        if (ImageCount + _selection.Count(item => item.Tool == AnnotationTool.Image) > MaximumImages)
            throw new InvalidOperationException(I18n.T("每份合圖最多可放入 24 張圖片。"));
        long pixels = _annotations.Concat(_annotations.Where(_selection.Contains))
            .Where(item => item.Image is not null).Sum(item => (long)item.Image!.PixelSize.Width * item.Image.PixelSize.Height);
        if (pixels > 80_000_000) throw new InvalidOperationException(I18n.T("圖片總尺寸過大，請減少張數或縮小原圖。"));
        var copies = _annotations.Where(_selection.Contains).Select(item => item.Clone()).ToList();
        Vector offset = ClampOffset(BoundsOf(copies), new Vector(20, 20));
        foreach (AnnotationItem item in copies) item.Offset(offset);
        RememberMutation();
        _annotations.AddRange(copies);
        EnsureRenderCache();
        SetSelection(copies);
        RefreshMosaics();
        FinishMutation();
    }

    public void NudgeSelection(double x, double y)
    {
        VerifyAccess();
        PrepareObjectCommand();
        if (_selection.Count == 0) return;
        Vector delta = ClampOffset(BoundsOf(_selection), new Vector(x, y));
        if (delta.SquaredLength < 0.0001) return;
        RememberMutation();
        foreach (AnnotationItem item in _selection) item.Offset(delta);
        RefreshMosaics();
        FinishMutation();
    }

    internal void AlignSelection(ObjectAlignment alignment)
    {
        VerifyAccess();
        PrepareObjectCommand();
        if (_selection.Count < 2) return;
        Rect group = BoundsOf(_selection);
        RememberMutation();
        foreach (AnnotationItem item in _selection)
        {
            Rect bounds = item.Bounds;
            Vector delta = alignment switch
            {
                ObjectAlignment.Left => new(group.Left - bounds.Left, 0),
                ObjectAlignment.Center => new(group.Left + (group.Width - bounds.Width) / 2 - bounds.Left, 0),
                ObjectAlignment.Right => new(group.Right - bounds.Right, 0),
                ObjectAlignment.Top => new(0, group.Top - bounds.Top),
                ObjectAlignment.Middle => new(0, group.Top + (group.Height - bounds.Height) / 2 - bounds.Top),
                _ => new(0, group.Bottom - bounds.Bottom)
            };
            item.Offset(delta);
        }
        RefreshMosaics();
        FinishMutation();
    }

    internal void DistributeSelection(bool horizontal)
    {
        VerifyAccess();
        PrepareObjectCommand();
        if (_selection.Count < 3) return;
        var items = _selection.OrderBy(item => horizontal ? item.Bounds.Left : item.Bounds.Top).ToList();
        Rect group = BoundsOf(items);
        double total = items.Sum(item => horizontal ? item.Bounds.Width : item.Bounds.Height);
        double gap = ((horizontal ? group.Width : group.Height) - total) / (items.Count - 1);
        double position = horizontal ? group.Left : group.Top;
        RememberMutation();
        foreach (AnnotationItem item in items)
        {
            Rect bounds = item.Bounds;
            item.Offset(horizontal ? new Vector(position - bounds.Left, 0) : new Vector(0, position - bounds.Top));
            position += (horizontal ? bounds.Width : bounds.Height) + gap;
        }
        RefreshMosaics();
        FinishMutation();
    }

    internal void ChangeOrder(ObjectOrder order)
    {
        VerifyAccess();
        PrepareObjectCommand();
        if (_selection.Count == 0) return;
        var before = Snapshot();
        var originalOrder = _annotations.ToArray();
        var selected = _annotations.Where(_selection.Contains).ToList();
        if (order is ObjectOrder.Front or ObjectOrder.Back)
        {
            _annotations.RemoveAll(_selection.Contains);
            _annotations.InsertRange(order == ObjectOrder.Front ? _annotations.Count : 0, selected);
        }
        else if (order == ObjectOrder.Forward)
        {
            for (int i = _annotations.Count - 2; i >= 0; i--)
                if (_selection.Contains(_annotations[i]) && !_selection.Contains(_annotations[i + 1]))
                    (_annotations[i], _annotations[i + 1]) = (_annotations[i + 1], _annotations[i]);
        }
        else
        {
            for (int i = 1; i < _annotations.Count; i++)
                if (_selection.Contains(_annotations[i]) && !_selection.Contains(_annotations[i - 1]))
                    (_annotations[i], _annotations[i - 1]) = (_annotations[i - 1], _annotations[i]);
        }
        if (_annotations.SequenceEqual(originalOrder)) return;
        _undo.Push(before);
        _redo.Clear();
        RefreshMosaics();
        FinishMutation();
    }

    private void PrepareObjectCommand()
    {
        CommitImageCrop();
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
    }

    private static Bitmap CreateBoard(int width, int height)
    {
        if (width < 80 || height < 80 || width > 16384 || height > 16384 || (long)width * height > 40_000_000)
            throw new InvalidOperationException(I18n.T("畫布過大，請改用網格排列或縮小圖片。"));
        // A shared 16-bit white bitmap per size keeps large empty canvases and undo snapshots small.
        if (Boards.TryGetValue((width, height), out var reference) && reference.TryGetTarget(out var cached))
            return cached;
        int stride = width * 2;
        nuint bytes = checked((nuint)stride * (nuint)height);
        Bitmap board;
        unsafe
        {
            void* memory = NativeMemory.Alloc(bytes);
            try
            {
                NativeMemory.Fill(memory, bytes, 0xFF);
                board = new Bitmap(PixelFormat.Rgb565, AlphaFormat.Opaque, (IntPtr)memory, new PixelSize(width, height), ImageTools.Dpi, stride);
            }
            finally { NativeMemory.Free(memory); }
        }
        BoardImages.AddOrUpdate(board, BoardMarker);
        foreach (var stale in Boards.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToList())
            Boards.Remove(stale);
        Boards[(width, height)] = new WeakReference<Bitmap>(board);
        return board;
    }

    /// <summary>True for the blank white collage canvases created by <see cref="CreateBoard"/>.</summary>
    private static bool IsBoard(Bitmap image) => BoardImages.TryGetValue(image, out _);

    /// <summary>A restored collage canvas that is entirely white is replaced by the shared compact board.</summary>
    private static Bitmap AsBoard(Bitmap image)
    {
        int width = image.PixelSize.Width, height = image.PixelSize.Height;
        if (IsBoard(image) || width < 80 || height < 80 || width > 16384 || height > 16384 || (long)width * height > 40_000_000
            || image.Format != PixelFormat.Bgra8888 || image.AlphaFormat is not (AlphaFormat.Premul or AlphaFormat.Opaque))
            return image;
        int rows = Math.Max(1, (1 << 20) / (width * 4));
        var buffer = new byte[width * 4 * rows];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            for (int y = 0; y < height; y += rows)
            {
                int count = Math.Min(rows, height - y);
                image.CopyPixels(new PixelRect(0, y, width, count), handle.AddrOfPinnedObject(), buffer.Length, width * 4);
                var span = buffer.AsSpan(0, width * 4 * count);
                if (image.AlphaFormat == AlphaFormat.Opaque)
                {
                    for (int index = 0; index < span.Length; index += 4)
                        if (span[index] != 255 || span[index + 1] != 255 || span[index + 2] != 255) return image;
                }
                else if (span.IndexOfAnyExcept((byte)255) >= 0)
                    return image;
            }
        }
        catch (NotSupportedException)
        {
            return image;
        }
        finally { handle.Free(); }
        return CreateBoard(width, height);
    }

    private void SetBoard(Bitmap image)
    {
        _image = image;
        _pixelSource = null;
        SizeDocument(image);
    }

    private static Rect BoundsOf(IEnumerable<AnnotationItem> items)
    {
        // WPF's Rect.Union also grows to include zero-size rectangles (single points).
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (AnnotationItem item in items)
        {
            Rect bounds = item.Bounds;
            left = Math.Min(left, bounds.Left);
            top = Math.Min(top, bounds.Top);
            right = Math.Max(right, bounds.Right);
            bottom = Math.Max(bottom, bounds.Bottom);
        }
        return left <= right && top <= bottom ? new Rect(new Point(left, top), new Point(right, bottom)) : default;
    }

    private Vector ClampOffset(Rect bounds, Vector delta) => new(
        Math.Clamp(delta.X, -bounds.Left, Math.Max(-bounds.Left, PixelWidth - bounds.Right)),
        Math.Clamp(delta.Y, -bounds.Top, Math.Max(-bounds.Top, PixelHeight - bounds.Bottom)));

    private void SetSelection(IEnumerable<AnnotationItem> items)
    {
        var selected = items.ToList();
        _selection.Clear();
        _selection.UnionWith(selected);
        _selected = selected.LastOrDefault();
        _hoverHandle = SelectionHandle.None;
        RaiseSelectionChanged();
        _overlay.InvalidateVisual();
    }

    private void RefreshMosaics()
    {
        foreach (AnnotationItem item in _annotations.Where(item => item.Tool == AnnotationTool.Mosaic))
            PrepareMosaic(item);
    }

    private void ShowObjectMenu(Point point)
    {
        PrepareObjectCommand();
        AnnotationItem? item = _annotations.LastOrDefault(candidate => candidate.HitTest(point, 6 / _zoom));
        if (item is null) return;
        if (!_selection.Contains(item)) SelectItem(item);
        var menu = new ContextMenu();
        void Add(string title, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = I18n.T(title), IsEnabled = enabled };
            entry.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception error) { AppDialog.Show(error.Message, I18n.T("無法修改物件")); }
            };
            menu.Items.Add(entry);
        }
        Add("編輯文字", () => BeginTextEdit(_selected, null, null), _selection.Count == 1 && item.Tool == AnnotationTool.Text);
        Add("裁剪圖片", () => BeginImageCrop(), IsCollage && _selection.Count == 1 && item.Tool == AnnotationTool.Image);
        Add("複製物件", DuplicateSelection);
        Add("刪除物件", DeleteSelection);
        menu.Items.Add(new Separator());
        Add("移到最上層", () => ChangeOrder(ObjectOrder.Front));
        Add("上移一層", () => ChangeOrder(ObjectOrder.Forward));
        Add("下移一層", () => ChangeOrder(ObjectOrder.Backward));
        Add("移到最下層", () => ChangeOrder(ObjectOrder.Back));
        menu.Items.Add(new Separator());
        Add("靠左對齊", () => AlignSelection(ObjectAlignment.Left), SelectionCount > 1);
        Add("水平置中", () => AlignSelection(ObjectAlignment.Center), SelectionCount > 1);
        Add("靠右對齊", () => AlignSelection(ObjectAlignment.Right), SelectionCount > 1);
        Add("靠上對齊", () => AlignSelection(ObjectAlignment.Top), SelectionCount > 1);
        Add("垂直置中", () => AlignSelection(ObjectAlignment.Middle), SelectionCount > 1);
        Add("靠下對齊", () => AlignSelection(ObjectAlignment.Bottom), SelectionCount > 1);
        Add("水平平均分布", () => DistributeSelection(true), SelectionCount > 2);
        Add("垂直平均分布", () => DistributeSelection(false), SelectionCount > 2);
        menu.Open(_viewport);
    }

    private Rect ResizeImage(Rect original, SelectionHandle handle, Vector delta)
    {
        bool left = handle is SelectionHandle.TopLeft or SelectionHandle.BottomLeft;
        bool top = handle is SelectionHandle.TopLeft or SelectionHandle.TopRight;
        Point anchor = new(left ? original.Right : original.Left, top ? original.Bottom : original.Top);
        double desiredWidth = original.Width + (left ? -delta.X : delta.X);
        double desiredHeight = original.Height + (top ? -delta.Y : delta.Y);
        double ratio = original.Width / original.Height;
        double scale = Math.Abs(delta.X / original.Width) >= Math.Abs(delta.Y / original.Height)
            ? desiredWidth / original.Width : desiredHeight / original.Height;
        double maxWidth = left ? anchor.X : PixelWidth - anchor.X;
        double maxHeight = top ? anchor.Y : PixelHeight - anchor.Y;
        double maxScale = Math.Min(maxWidth / original.Width, maxHeight / original.Height);
        scale = Math.Clamp(scale, Math.Min(2 / Math.Min(original.Width, original.Height), maxScale), maxScale);
        double width = original.Width * scale, height = width / ratio;
        return new Rect(left ? anchor.X - width : anchor.X, top ? anchor.Y - height : anchor.Y, width, height);
    }
}

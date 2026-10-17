using System.Windows.Input;
using SnipFlow.Services;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfVector = System.Windows.Vector;

namespace SnipFlow.Editor;

internal enum CollageLayout { Grid, Vertical, Horizontal }
internal enum ObjectAlignment { Left, Center, Right, Top, Middle, Bottom }
internal enum ObjectOrder { Front, Forward, Backward, Back }
internal sealed record EditorSessionSnapshot(BitmapSource Image, List<AnnotationItem> Annotations, bool HasEdits, bool IsCollage);

public sealed partial class EditorView
{
    private const int MaximumImages = 24;
    private readonly HashSet<AnnotationItem> _selection = new();
    private readonly Dictionary<AnnotationItem, AnnotationItem> _transformSources = new();
    private readonly Dictionary<AnnotationItem, AnnotationItem> _propertySources = new();
    private WpfPoint? _marqueeStart;
    private WpfRect? _marquee;
    private HashSet<AnnotationItem>? _marqueeOriginal;
    public bool IsCollage { get; private set; }
    public int SelectionCount => _selection.Count;
    public int ImageCount => _annotations.Count(item => item.Tool == AnnotationTool.Image);
    public int PixelWidth => _image?.PixelWidth ?? 0;
    public int PixelHeight => _image?.PixelHeight ?? 0;

    internal void NewCollage()
    {
        VerifyAccess();
        RestoreSession(CreateBoard(1920, 1080), Array.Empty<AnnotationItem>(), hasEdits: true, isCollage: true);
        _color = System.Windows.Media.Color.FromRgb(17, 24, 39);
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
        BitmapSource? original = _image;
        int width = Math.Max(1920, (original?.PixelWidth ?? 0) + 80);
        int height = Math.Max(1080, (original?.PixelHeight ?? 0) + 80);
        BitmapSource board = CreateBoard(width, height);
        if (original is null)
            LoadImage(board);
        RememberMutation();
        if (original is not null)
        {
            foreach (AnnotationItem item in _annotations) item.Offset(new WpfVector(40, 40));
            _annotations.Insert(0, new AnnotationItem
            {
                Tool = AnnotationTool.Image, Image = original,
                Start = new WpfPoint(40, 40), End = new WpfPoint(40 + original.PixelWidth, 40 + original.PixelHeight), Width = 1
            });
        }
        IsCollage = true;
        _color = System.Windows.Media.Color.FromRgb(32, 32, 32);
        _tool = AnnotationTool.Select;
        SetBoard(board);
        SelectItem(original is null ? null : _annotations[0]);
        FinishMutation();
        FitToView();
        RefreshLanguage();
    }

    internal void AddImages(IReadOnlyList<BitmapSource> images)
    {
        VerifyAccess();
        var prepared = PrepareImagesForInsertion(images, null);
        if (prepared.Count != 0) InsertPreparedImages(prepared);
    }

    internal void AddImageAt(BitmapSource image, WpfPoint center)
    {
        VerifyAccess();
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !InsideImage(center))
            throw new ArgumentOutOfRangeException(nameof(center));
        InsertPreparedImages(PrepareImagesForInsertion(new[] { image }, center));
    }

    internal void ValidateImageInsertion(BitmapSource image)
    {
        VerifyAccess();
        ValidateImagesForInsertion(new[] { image });
    }

    private void ValidateImagesForInsertion(IReadOnlyList<BitmapSource> images)
    {
        if (!IsCollage) throw new InvalidOperationException(I18n.T("請先開啟多張合圖。"));
        if (ImageCount + images.Count > MaximumImages)
            throw new InvalidOperationException(I18n.T("每份合圖最多可放入 24 張圖片。"));
        long pixels = _annotations.Where(item => item.Image is not null).Sum(item => (long)item.Image!.PixelWidth * item.Image.PixelHeight);
        foreach (BitmapSource input in images)
        {
            if (input.PixelWidth < 1 || input.PixelHeight < 1) throw new ArgumentException(I18n.T("影像尺寸不得為零。"));
            pixels += (long)input.PixelWidth * input.PixelHeight;
            if (pixels > 80_000_000) throw new InvalidOperationException(I18n.T("圖片總尺寸過大，請減少張數或縮小原圖。"));
        }
    }

    private List<AnnotationItem> PrepareImagesForInsertion(IReadOnlyList<BitmapSource> images, WpfPoint? center)
    {
        ValidateImagesForInsertion(images);
        var prepared = new List<AnnotationItem>();
        foreach (BitmapSource input in images)
        {
            BitmapSource image = input.IsFrozen ? input : input.CloneCurrentValue();
            if (!image.IsFrozen) image.Freeze();
            WpfRect bounds = InitialImageBounds(image, center, ImageCount + prepared.Count);
            prepared.Add(new AnnotationItem { Tool = AnnotationTool.Image, Image = image, Start = bounds.TopLeft, End = bounds.BottomRight, Width = 1 });
        }
        return prepared;
    }

    private WpfRect InitialImageBounds(BitmapSource image, WpfPoint? center, int index = 0)
    {
        double scale = Math.Min(1, Math.Min(Math.Max(1, PixelWidth - 80.0) / image.PixelWidth, Math.Max(1, PixelHeight - 80.0) / image.PixelHeight));
        double width = image.PixelWidth * scale, height = image.PixelHeight * scale;
        double offset = 40 + (index % 5) * 24;
        double x = Math.Clamp(center?.X - width / 2 ?? offset, 0, Math.Max(0, PixelWidth - width));
        double y = Math.Clamp(center?.Y - height / 2 ?? offset, 0, Math.Max(0, PixelHeight - height));
        return new WpfRect(x, y, width, height);
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
        var placements = new List<WpfRect>();
        double right = PixelWidth, bottom = PixelHeight;
        if (layout == CollageLayout.Horizontal)
        {
            double x = padding, available = Math.Max(1, PixelHeight - 2 * padding);
            foreach (AnnotationItem item in images)
            {
                double scale = Math.Min(1, available / item.Image!.PixelHeight);
                double width = item.Image.PixelWidth * scale, height = item.Image.PixelHeight * scale;
                placements.Add(new WpfRect(x, padding, width, height));
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
                    BitmapSource image = images[row + column].Image!;
                    double scale = Math.Min(1, cell / image.PixelWidth);
                    double width = image.PixelWidth * scale, height = image.PixelHeight * scale;
                    placements.Add(new WpfRect(padding + column * (cell + gap), y, width, height));
                    rowHeight = Math.Max(rowHeight, height);
                }
                y += rowHeight + gap;
            }
            bottom = Math.Max(bottom, y - gap + padding);
        }
        BitmapSource board = CreateBoard((int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
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
        BitmapSource board = CreateBoard(width, height);
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
        WpfRect bounds = BoundsOf(_annotations);
        BitmapSource board = CreateBoard((int)Math.Ceiling(bounds.Width + 80), (int)Math.Ceiling(bounds.Height + 80));
        RememberMutation();
        foreach (AnnotationItem item in _annotations) item.Offset(new WpfVector(40 - bounds.Left, 40 - bounds.Top));
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
            .Where(item => item.Image is not null).Sum(item => (long)item.Image!.PixelWidth * item.Image.PixelHeight);
        if (pixels > 80_000_000) throw new InvalidOperationException(I18n.T("圖片總尺寸過大，請減少張數或縮小原圖。"));
        var copies = _annotations.Where(_selection.Contains).Select(item => item.Clone()).ToList();
        WpfVector offset = ClampOffset(BoundsOf(copies), new WpfVector(20, 20));
        foreach (AnnotationItem item in copies) item.Offset(offset);
        RememberMutation();
        _annotations.AddRange(copies);
        SetSelection(copies);
        RefreshMosaics();
        FinishMutation();
    }

    public void NudgeSelection(double x, double y)
    {
        VerifyAccess();
        PrepareObjectCommand();
        if (_selection.Count == 0) return;
        WpfVector delta = ClampOffset(BoundsOf(_selection), new WpfVector(x, y));
        if (delta.LengthSquared < 0.0001) return;
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
        WpfRect group = BoundsOf(_selection);
        RememberMutation();
        foreach (AnnotationItem item in _selection)
        {
            WpfRect bounds = item.Bounds;
            WpfVector delta = alignment switch
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
        WpfRect group = BoundsOf(items);
        double total = items.Sum(item => horizontal ? item.Bounds.Width : item.Bounds.Height);
        double gap = ((horizontal ? group.Width : group.Height) - total) / (items.Count - 1);
        double position = horizontal ? group.Left : group.Top;
        RememberMutation();
        foreach (AnnotationItem item in items)
        {
            WpfRect bounds = item.Bounds;
            item.Offset(horizontal ? new WpfVector(position - bounds.Left, 0) : new WpfVector(0, position - bounds.Top));
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
        CompleteGesture();
        CommitTextEdit();
        EndPropertyEdit();
    }

    private static BitmapSource CreateBoard(int width, int height)
    {
        if (width < 80 || height < 80 || width > 16384 || height > 16384 || (long)width * height > 40_000_000)
            throw new InvalidOperationException(I18n.T("畫布過大，請改用網格排列或縮小圖片。"));
        // An indexed white bitmap keeps large empty canvases and undo snapshots small.
        var board = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed1,
            new BitmapPalette(new[] { Colors.White, Colors.White }), new byte[checked(((width + 7) / 8) * height)], (width + 7) / 8);
        board.Freeze();
        return board;
    }

    private void SetBoard(BitmapSource image)
    {
        _image = image;
        _pixelSource = null;
        _surface.Width = _documentLayer.Width = image.PixelWidth;
        _surface.Height = _documentLayer.Height = image.PixelHeight;
        _imageLabel.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px";
        _surface.InvalidateVisual();
    }

    private static WpfRect BoundsOf(IEnumerable<AnnotationItem> items)
    {
        WpfRect bounds = WpfRect.Empty;
        foreach (AnnotationItem item in items) bounds.Union(item.Bounds);
        return bounds;
    }

    private WpfVector ClampOffset(WpfRect bounds, WpfVector delta) => new(
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
        _surface.InvalidateVisual();
    }

    private void RefreshMosaics()
    {
        foreach (AnnotationItem item in _annotations.Where(item => item.Tool == AnnotationTool.Mosaic))
            PrepareMosaic(item);
    }

    private void ShowObjectMenu(WpfPoint point)
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
        menu.PlacementTarget = _viewport;
        menu.IsOpen = true;
    }

    private WpfRect ResizeImage(WpfRect original, SelectionHandle handle, WpfVector delta)
    {
        bool left = handle is SelectionHandle.TopLeft or SelectionHandle.BottomLeft;
        bool top = handle is SelectionHandle.TopLeft or SelectionHandle.TopRight;
        WpfPoint anchor = new(left ? original.Right : original.Left, top ? original.Bottom : original.Top);
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
        return new WpfRect(left ? anchor.X - width : anchor.X, top ? anchor.Y - height : anchor.Y, width, height);
    }
}

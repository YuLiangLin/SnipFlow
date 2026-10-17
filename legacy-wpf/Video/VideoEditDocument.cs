using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SnipFlow.Services;

namespace SnipFlow.Video;

internal sealed record VideoInfo(string SourcePath, int Width, int Height, TimeSpan Duration);
internal enum VideoOverlayKind { Text, Image }

/// <summary>Coordinates use encoded video pixels; all times use the original source timeline.</summary>
internal sealed class VideoOverlay : INotifyPropertyChanged
{
    public VideoOverlay() { }
    private double _x, _y, _width = 400, _height = 96, _fontSize = 32;
    private string _text = "", _colorHex = "#FFFFFF";
    private byte[]? _imagePng;
    private TimeSpan _start, _end;
    private bool _showEntireVideo = true;

    public Guid Id { get; init; } = Guid.NewGuid();
    public VideoOverlayKind Kind { get; init; }
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public string Text { get => _text; set => Set(ref _text, value ?? ""); }
    public string ColorHex { get => _colorHex; set => Set(ref _colorHex, value ?? "#FFFFFF"); }
    public byte[]? ImagePng { get => _imagePng; set => Set(ref _imagePng, value); }
    public TimeSpan Start { get => _start; set => Set(ref _start, value); }
    public TimeSpan End { get => _end; set => Set(ref _end, value); }
    public bool ShowEntireVideo { get => _showEntireVideo; set => Set(ref _showEntireVideo, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    internal VideoOverlay Clone() => new()
    {
        Id = Id, Kind = Kind, X = X, Y = Y, Width = Width, Height = Height,
        FontSize = FontSize, Text = Text, ColorHex = ColorHex, ImagePng = ImagePng,
        Start = Start, End = End, ShowEntireVideo = ShowEntireVideo
    };
}

internal sealed class VideoEditDocument : INotifyPropertyChanged
{
    internal const int MaximumOverlays = 32;
    internal const int MaximumImageBytes = 24 * 1024 * 1024;
    internal const long MaximumTotalImageBytes = 64 * 1024 * 1024;
    private readonly HashSet<VideoOverlay> _observed = [];
    private TimeSpan _trimStart, _trimEnd;
    private bool _isDirty;
    private string? _projectPath;

    public VideoInfo Source { get; }
    public ObservableCollection<VideoOverlay> Overlays { get; } = [];
    public TimeSpan TrimStart { get => _trimStart; set => Set(ref _trimStart, value); }
    public TimeSpan TrimEnd { get => _trimEnd; set => Set(ref _trimEnd, value); }
    public TimeSpan ExportDuration => TrimEnd - TrimStart;
    public string? ProjectPath
    {
        get => _projectPath;
        set { _projectPath = value; PropertyChanged?.Invoke(this, new(nameof(ProjectPath))); }
    }
    public bool IsDirty
    {
        get => _isDirty;
        set { if (_isDirty == value) return; _isDirty = value; PropertyChanged?.Invoke(this, new(nameof(IsDirty))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal VideoEditDocument(VideoInfo source)
    {
        Source = source;
        _trimEnd = source.Duration;
        Overlays.CollectionChanged += OverlaysChanged;
    }

    private void Set(ref TimeSpan field, TimeSpan value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        MarkDirty();
        PropertyChanged?.Invoke(this, new(name));
        PropertyChanged?.Invoke(this, new(nameof(ExportDuration)));
    }

    public void MarkDirty() => IsDirty = true;

    private void OverlaysChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var current = Overlays.ToHashSet();
        foreach (var removed in _observed.Except(current).ToArray())
        {
            removed.PropertyChanged -= OverlayChanged;
            _observed.Remove(removed);
        }
        foreach (var added in current.Except(_observed))
        {
            added.PropertyChanged += OverlayChanged;
            _observed.Add(added);
        }
        MarkDirty();
    }

    private void OverlayChanged(object? sender, PropertyChangedEventArgs args) => MarkDirty();

    public VideoEditDocument CreateSnapshot()
    {
        var copy = new VideoEditDocument(Source) { TrimStart = TrimStart, TrimEnd = TrimEnd, ProjectPath = ProjectPath };
        foreach (var overlay in Overlays) copy.Overlays.Add(overlay.Clone());
        copy.IsDirty = IsDirty;
        return copy;
    }

    public void Validate()
    {
        if (Source.Width < 2 || Source.Height < 2 || Source.Width > 8192 || Source.Height > 8192
            || (long)Source.Width * Source.Height > 40_000_000 || Source.Duration <= TimeSpan.Zero)
            throw new InvalidOperationException(I18n.T("影片尺寸或長度不受支援。"));
        if (TrimStart < TimeSpan.Zero || TrimEnd > Source.Duration || ExportDuration < TimeSpan.FromMilliseconds(100))
            throw new InvalidOperationException(I18n.T("保留範圍至少需要 0.1 秒，且必須在影片內。"));
        if (Overlays.Count > MaximumOverlays || Overlays.Select(o => o.Id).Distinct().Count() != Overlays.Count)
            throw new InvalidOperationException(I18n.T("每部影片最多可加入 32 個文字或圖片物件。"));
        long bytes = 0, pixels = 0;
        foreach (var overlay in Overlays)
        {
            if (!Enum.IsDefined(overlay.Kind) || !Finite(overlay.X, overlay.Y, overlay.Width, overlay.Height, overlay.FontSize)
                || overlay.X < 0 || overlay.Y < 0 || overlay.Width < 1 || overlay.Height < 1
                || overlay.X + overlay.Width > Source.Width + 0.01 || overlay.Y + overlay.Height > Source.Height + 0.01)
                throw new InvalidOperationException(I18n.T("標註物件必須在影片範圍內。"));
            if (!overlay.ShowEntireVideo && (overlay.Start < TimeSpan.Zero || overlay.End > Source.Duration || overlay.End <= overlay.Start))
                throw new InvalidOperationException(I18n.T("物件的消失時間必須晚於出現時間，且在影片內。"));
            if (overlay.Kind == VideoOverlayKind.Text)
            {
                if (overlay.Text.Length > 8000 || overlay.FontSize < 8 || overlay.FontSize > 240 || !IsColor(overlay.ColorHex))
                    throw new InvalidOperationException(I18n.T("文字內容、字級或顏色無效。"));
            }
            else
            {
                if (overlay.ImagePng is not { Length: > 0 } png || png.Length > MaximumImageBytes)
                    throw new InvalidOperationException(I18n.T("圖片物件資料無效或過大。"));
                var dimensions = VideoOverlayRenderer.ValidatePng(png);
                pixels += (long)dimensions.Width * dimensions.Height;
                bytes += png.Length;
            }
        }
        if (bytes > MaximumTotalImageBytes)
            throw new InvalidOperationException(I18n.T("影片專案的圖片合計不可超過 64 MB。"));
        if (pixels > 40_000_000)
            throw new InvalidOperationException(I18n.T("影片專案的圖片合計不可超過 40 百萬像素。"));
    }

    internal static bool Finite(params double[] values) => values.All(double.IsFinite);
    internal static bool IsColor(string value) => (value.Length is 7 or 9) && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
}

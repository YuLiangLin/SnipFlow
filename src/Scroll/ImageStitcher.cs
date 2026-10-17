using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using SnipFlow.Services;

namespace SnipFlow.Scroll;

public sealed record OverlapMatch(
    int OverlapPixels,
    double Confidence,
    double MeanError,
    bool IsDuplicate,
    bool IsReliable,
    string Message);

internal sealed record ScrollMatch(
    int AdvancePixels,
    int OverlapPixels,
    double Confidence,
    double MeanError,
    bool IsDuplicate,
    bool IsReliable);

/// <summary>
/// Rows [<see cref="Top"/>, <see cref="Top"/> + <see cref="Height"/>) of a captured frame at full width,
/// without copying pixels. It replaces the WPF <c>CroppedBitmap</c> views of trimmed frames: the
/// matcher and the stitcher read only these rows, so trimming never duplicates stored frames.
/// </summary>
internal readonly record struct FrameRows(Bitmap Image, int Top, int Height)
{
    public FrameRows(Bitmap image) : this(image, 0, image.PixelSize.Height) { }

    public int Width => Image.PixelSize.Width;

    /// <summary>The rows as their own bitmap (the frame itself when nothing is trimmed).</summary>
    public Bitmap ToBitmap() => Top == 0 && Height == Image.PixelSize.Height
        ? Image : ImageTools.Crop(Image, new PixelRect(0, Top, Width, Height));
}

/// <summary>Matches vertical movement; positive offsets move down and negative offsets move up.</summary>
/// <remarks>
/// Pixels are read as BGRA premultiplied (the capture layout; screen pixels are opaque, so the values
/// equal the WPF Bgra32 reads). Bitmaps passed here are treated as immutable snapshots: samples are
/// cached per bitmap, as the WPF version cached them per frozen bitmap.
/// </remarks>
public static class ImageStitcher
{
    public const long MaxOutputPixels = 40_000_000;
    public const long MaxStoredPixels = 60_000_000;
    public const int MaxFrames = 64;
    private static readonly ConditionalWeakTable<Bitmap, SampleImage> Samples = new();
    private static readonly ConditionalWeakTable<Bitmap, Bitmap> Converted = new();

    public static OverlapMatch FindOverlap(Bitmap previous, Bitmap current)
    {
        var match = FindScroll(previous, current);
        var reliable = match.IsReliable && (match.IsDuplicate || match.AdvancePixels > 0);
        return new OverlapMatch(match.OverlapPixels, match.Confidence, match.MeanError,
            match.IsDuplicate, reliable, I18n.T(match.IsDuplicate
                ? "畫面尚未捲動，或已到達頁面底部。"
                : reliable ? "已找到穩定的垂直重疊。" : "無法可靠對齊，請調整重疊高度並確認接縫。"));
    }

    internal static bool AreSameImages(Bitmap first, Bitmap second) => AreSameImages(new FrameRows(first), new FrameRows(second));

    internal static bool AreSameImages(FrameRows first, FrameRows second)
    {
        if (first.Width != second.Width || first.Height != second.Height) return false;
        return IsDuplicate(ReadSample(first), ReadSample(second));
    }

    internal static ScrollMatch FindScroll(Bitmap previous, Bitmap current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return FindScroll(new FrameRows(previous), new FrameRows(current));
    }

    internal static ScrollMatch FindScroll(FrameRows previous, FrameRows current)
    {
        ArgumentNullException.ThrowIfNull(previous.Image);
        ArgumentNullException.ThrowIfNull(current.Image);
        if (previous.Width != current.Width || previous.Height != current.Height)
            throw new ArgumentException(I18n.T("拼接畫面必須來自同一個截圖範圍，且大小一致。"));
        var height = previous.Height;
        if (height < 48 || previous.Width < 32)
            return new ScrollMatch(0, 0, 0, 1, false, false);

        var first = ReadSample(previous);
        var second = ReadSample(current);
        if (IsDuplicate(first, second))
            return new ScrollMatch(0, height, 1, 0, true, true);

        var minimumOverlap = Math.Max(24, height / 6);
        var minimumAdvance = 2;
        var maximumAdvance = height - minimumOverlap;
        var coarseStep = Math.Max(1, height / 480);
        var scores = new List<(int Advance, MatchScore Score)>();
        var bestAdvance = 0;
        var bestScore = new MatchScore(1, 0);
        for (var direction = -1; direction <= 1; direction += 2)
        {
            for (var magnitude = minimumAdvance; magnitude <= maximumAdvance; magnitude += coarseStep)
            {
                var advance = magnitude * direction;
                var score = Compare(first, second, advance);
                scores.Add((advance, score));
                if (score.Error < bestScore.Error)
                {
                    bestScore = score;
                    bestAdvance = advance;
                }
            }
        }
        var coarseWinner = bestAdvance;
        for (var advance = coarseWinner - coarseStep; advance <= coarseWinner + coarseStep; advance++)
        {
            if (Math.Abs(advance) < minimumAdvance || Math.Abs(advance) > maximumAdvance) continue;
            var score = Compare(first, second, advance);
            if (score.Error < bestScore.Error)
            {
                bestScore = score;
                bestAdvance = advance;
            }
        }

        // Repeated chat bubbles or blank regions must not choose an arbitrary alignment.
        var exclusionDistance = Math.Max(5, coarseStep * 3);
        var alternative = scores.Where(entry => Math.Abs(entry.Advance - bestAdvance) > exclusionDistance)
            .Select(entry => entry.Score.Error).DefaultIfEmpty(1d).Min();
        var separation = Math.Max(0, alternative - bestScore.Error);
        var stationaryError = Compare(first, second, 0).Error;
        var confidence = Math.Clamp(1 - bestScore.Error / 0.06, 0, 1) * 0.7
            + Math.Clamp(separation / 0.025, 0, 1) * 0.3;
        var reliable = bestAdvance != 0 && bestScore.Error <= 0.03
            && separation >= Math.Max(0.006, bestScore.Error * 0.3)
            && bestScore.DetailFraction >= 0.012
            && stationaryError - bestScore.Error >= Math.Max(0.004, bestScore.Error * 0.2);

        return new ScrollMatch(bestAdvance, height - Math.Abs(bestAdvance),
            confidence, bestScore.Error, false, reliable);
    }

    /// <summary>Flags detailed fixed edge bands without cropping any captured content.</summary>
    internal static bool HasFixedEdgeBand(Bitmap previous, Bitmap current) =>
        HasFixedEdgeBand(new FrameRows(previous), new FrameRows(current));

    internal static bool HasFixedEdgeBand(FrameRows previous, FrameRows current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height) return false;
        var first = ReadSample(previous);
        var second = ReadSample(current);
        if (IsDuplicate(first, second)) return false;
        var maximumRows = Math.Min(first.Height / 4, 240);
        return FixedRows(first, second, maximumRows, false) >= 16
            || FixedRows(first, second, maximumRows, true) >= 16;
    }

    public static Bitmap Stitch(
        IReadOnlyList<Bitmap> frames,
        IReadOnlyList<int> overlaps,
        long maxPixels = MaxOutputPixels)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return Stitch(Rows(frames), overlaps, maxPixels);
    }

    internal static Bitmap Stitch(
        IReadOnlyList<FrameRows> frames,
        IReadOnlyList<int> overlaps,
        long maxPixels = MaxOutputPixels)
    {
        var outputPixels = CalculateOutputPixels(frames, overlaps);
        if (outputPixels > maxPixels)
            throw new InvalidOperationException(I18n.F("長截圖超過 {0:N0} 百萬像素上限，請分段截取。", maxPixels / 1_000_000));
        var width = frames[0].Width;
        var height = checked((int)(outputPixels / width));
        // Rows are copied straight into the result, so no second full-size pixel array is allocated.
        var result = CreateOutput(width, height);
        using (var target = result.Lock())
        {
            var rowOffset = 0;
            for (var index = 0; index < frames.Count; index++)
            {
                var skippedRows = index == 0 ? 0 : overlaps[index - 1];
                var copiedRows = frames[index].Height - skippedRows;
                CopyRows(frames[index], skippedRows, copiedRows, target, rowOffset);
                rowOffset += copiedRows;
            }
        }
        return result;
    }

    public static long CalculateOutputPixels(IReadOnlyList<Bitmap> frames, IReadOnlyList<int> overlaps)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return CalculateOutputPixels(Rows(frames), overlaps);
    }

    internal static long CalculateOutputPixels(IReadOnlyList<FrameRows> frames, IReadOnlyList<int> overlaps)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(overlaps);
        if (frames.Count == 0)
            throw new ArgumentException(I18n.T("請先加入至少一個畫面。"), nameof(frames));
        if (overlaps.Count != frames.Count - 1)
            throw new ArgumentException(I18n.T("每兩個連續畫面都必須指定一個重疊高度。"), nameof(overlaps));
        var width = frames[0].Width;
        long totalHeight = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.Width != width)
                throw new ArgumentException(I18n.T("每個畫面的寬度必須一致。"), nameof(frames));
            var overlap = index == 0 ? 0 : overlaps[index - 1];
            if (overlap < 0 || overlap >= frame.Height
                || (index > 0 && overlap > frames[index - 1].Height))
                throw new ArgumentOutOfRangeException(nameof(overlaps), I18n.T("重疊高度必須小於畫面高度。"));
            totalHeight = checked(totalHeight + frame.Height - overlap);
        }
        return checked(width * totalHeight);
    }

    /// <summary>Copies only the selected document rows, so a short result does not retain a full long image.</summary>
    internal static Bitmap StitchRange(IReadOnlyList<Bitmap> frames, IReadOnlyList<int> overlaps,
        int firstRow, int endRow)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return StitchRange(Rows(frames), overlaps, firstRow, endRow);
    }

    internal static Bitmap StitchRange(IReadOnlyList<FrameRows> frames, IReadOnlyList<int> overlaps,
        int firstRow, int endRow)
    {
        var totalPixels = CalculateOutputPixels(frames, overlaps);
        var width = frames[0].Width;
        var totalHeight = totalPixels / width;
        if (firstRow < 0 || endRow <= firstRow || endRow > totalHeight)
            throw new ArgumentOutOfRangeException(nameof(endRow), I18n.T("起點與終點必須在已擷取範圍內。"));
        var height = endRow - firstRow;
        if ((long)width * height > MaxOutputPixels)
            throw new InvalidOperationException(I18n.F("長截圖超過 {0:N0} 百萬像素上限，請分段截取。", MaxOutputPixels / 1_000_000));
        var result = CreateOutput(width, height);
        using (var target = result.Lock())
        {
            long documentRow = 0;
            for (var index = 0; index < frames.Count; index++)
            {
                var skipped = index == 0 ? 0 : overlaps[index - 1];
                var available = frames[index].Height - skipped;
                var from = Math.Max(documentRow, firstRow);
                var to = Math.Min(documentRow + available, endRow);
                if (to > from)
                {
                    var sourceRow = checked(skipped + (int)(from - documentRow));
                    CopyRows(frames[index], sourceRow, checked((int)(to - from)), target, checked((int)(from - firstRow)));
                }
                documentRow += available;
                if (documentRow >= endRow) break;
            }
        }
        return result;
    }

    internal static Bitmap CreateJoinPreview(Bitmap previous, Bitmap current, int overlap) =>
        CreateJoinPreview(new FrameRows(previous), new FrameRows(current), overlap);

    internal static Bitmap CreateJoinPreview(FrameRows previous, FrameRows current, int overlap)
    {
        var previousRows = Math.Min(100, previous.Height);
        var currentRows = Math.Min(100, current.Height - overlap);
        var upper = previous with { Top = previous.Top + previous.Height - previousRows, Height = previousRows };
        var lower = current with { Top = current.Top + overlap, Height = currentRows };
        return Stitch(new[] { upper, lower }, new[] { 0 });
    }

    private static FrameRows[] Rows(IReadOnlyList<Bitmap> frames)
    {
        var rows = new FrameRows[frames.Count];
        for (var index = 0; index < rows.Length; index++)
            rows[index] = new FrameRows(frames[index] ?? throw new ArgumentNullException(nameof(frames)));
        return rows;
    }

    private static WriteableBitmap CreateOutput(int width, int height) =>
        new(new PixelSize(width, height), ImageTools.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static void CopyRows(FrameRows source, int sourceRow, int rows, ILockedFramebuffer target, int targetRow)
    {
        if (rows <= 0) return;
        AsBgra32(source.Image).CopyPixels(new PixelRect(0, checked(source.Top + sourceRow), source.Width, rows),
            target.Address + checked(targetRow * target.RowBytes), checked(target.RowBytes * rows), target.RowBytes);
    }

    /// <summary>The bitmap itself when it already stores BGRA premultiplied pixels, otherwise a converted copy.</summary>
    private static Bitmap AsBgra32(Bitmap source)
        => source.Format == PixelFormat.Bgra8888 && source.AlphaFormat is AlphaFormat.Premul or AlphaFormat.Opaque
            ? source : Converted.GetValue(source, static image => PixelBuffer.FromBitmap(image).ToBitmap());

    // A trimmed frame's sample equals the matching rows of the whole frame's sample (same columns),
    // so one cached sample serves every trim.
    private static SampleImage ReadSample(FrameRows image)
    {
        var sample = Samples.GetValue(image.Image, static bitmap => SampleImage.Read(bitmap));
        return image.Top == 0 && image.Height == sample.Height ? sample : sample.Slice(image.Top, image.Height);
    }

    private static bool IsDuplicate(SampleImage first, SampleImage second)
    {
        long error = 0;
        var changed = 0;
        for (var index = 0; index < first.Values.Length; index++)
        {
            var difference = Math.Abs(first.Values[index] - second.Values[index]);
            error += difference;
            if (difference > 12) changed++;
        }
        // A blinking text cursor may change; actual scrolling must change a meaningful area.
        return error <= first.Values.Length * 255d * 0.002
            && changed <= first.Values.Length * 0.005;
    }

    private static MatchScore Compare(SampleImage first, SampleImage second, int advance)
    {
        var overlap = first.Height - Math.Abs(advance);
        var firstStart = Math.Max(advance, 0);
        var secondStart = Math.Max(-advance, 0);
        var margin = Math.Min(8, overlap / 16);
        var rowStep = Math.Max(1, overlap / 96);
        long error = 0;
        var weightCount = 0;
        var sampleCount = 0;
        var detailCount = 0;
        for (var y = margin; y < overlap - margin; y += rowStep)
        {
            var firstOffset = (firstStart + y) * first.Columns;
            var secondOffset = (secondStart + y) * second.Columns;
            for (var x = 1; x < first.Columns - 1; x++)
            {
                var firstPixel = first.Values[firstOffset + x];
                var secondPixel = second.Values[secondOffset + x];
                var detailed = Math.Abs(firstPixel - first.Values[firstOffset + x - 1]) > 10
                    || Math.Abs(secondPixel - second.Values[secondOffset + x - 1]) > 10;
                var weight = detailed ? 6 : 1;
                if (detailed) detailCount++;
                error += Math.Abs(firstPixel - secondPixel) * weight;
                weightCount += weight;
                sampleCount++;
            }
        }
        return sampleCount == 0 ? new MatchScore(1, 0)
            : new MatchScore((double)error / (weightCount * 255d), (double)detailCount / sampleCount);
    }

    private static int FixedRows(SampleImage first, SampleImage second, int maximumRows, bool bottom)
    {
        var unchangedRows = 0;
        var detailedRows = 0;
        for (var index = 0; index < maximumRows; index++)
        {
            var row = bottom ? first.Height - 1 - index : index;
            var offset = row * first.Columns;
            long error = 0;
            var changed = 0;
            var detail = 0;
            for (var x = 1; x < first.Columns - 1; x++)
            {
                var difference = Math.Abs(first.Values[offset + x] - second.Values[offset + x]);
                error += difference;
                if (difference > 12) changed++;
                if (Math.Abs(first.Values[offset + x] - first.Values[offset + x - 1]) > 10) detail++;
            }
            if (error > first.Columns * 255d * 0.008 || changed > first.Columns * 0.04) break;
            unchangedRows++;
            if (detail >= 2) detailedRows++;
        }
        return detailedRows >= 4 ? unchangedRows : 0;
    }

    private readonly record struct MatchScore(double Error, double DetailFraction);

    private sealed record SampleImage(int Height, int Columns, byte[] Values)
    {
        public static SampleImage Read(Bitmap image)
        {
            var source = AsBgra32(image);
            var width = source.PixelSize.Width;
            var height = source.PixelSize.Height;
            var stride = checked(width * 4);
            // Read short strips rather than allocating another full-resolution image.
            var pixels = new byte[checked(stride * Math.Min(32, height))];
            var columns = Math.Min(96, width);
            var values = new byte[checked(columns * height)];
            var left = width / 12;
            var right = width - 1 - left;
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var address = handle.AddrOfPinnedObject();
                for (var stripY = 0; stripY < height; stripY += 32)
                {
                    var rows = Math.Min(32, height - stripY);
                    source.CopyPixels(new PixelRect(0, stripY, width, rows), address, pixels.Length, stride);
                    for (var row = 0; row < rows; row++)
                        for (var x = 0; x < columns; x++)
                        {
                            var pixelX = left + (right - left) * x / Math.Max(1, columns - 1);
                            var offset = row * stride + pixelX * 4;
                            values[(stripY + row) * columns + x] = (byte)((pixels[offset] * 29
                                + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8);
                        }
                }
            }
            finally
            {
                handle.Free();
            }
            return new SampleImage(height, columns, values);
        }

        /// <summary>The sample of rows [top, top + rows), identical to reading those rows on their own.</summary>
        public SampleImage Slice(int top, int rows)
        {
            if (top < 0 || rows <= 0 || top + rows > Height)
                throw new ArgumentOutOfRangeException(nameof(rows));
            var values = new byte[checked(rows * Columns)];
            Buffer.BlockCopy(Values, top * Columns, values, 0, values.Length);
            return new SampleImage(rows, Columns, values);
        }
    }
}

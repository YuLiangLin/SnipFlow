using System.Runtime.CompilerServices;
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

/// <summary>Matches vertical movement; positive offsets move down and negative offsets move up.</summary>
public static class ImageStitcher
{
    public const long MaxOutputPixels = 40_000_000;
    public const long MaxStoredPixels = 60_000_000;
    public const int MaxFrames = 64;
    private static readonly ConditionalWeakTable<BitmapSource, SampleImage> Samples = new();

    public static OverlapMatch FindOverlap(BitmapSource previous, BitmapSource current)
    {
        var match = FindScroll(previous, current);
        var reliable = match.IsReliable && (match.IsDuplicate || match.AdvancePixels > 0);
        return new OverlapMatch(match.OverlapPixels, match.Confidence, match.MeanError,
            match.IsDuplicate, reliable, I18n.T(match.IsDuplicate
                ? "畫面尚未捲動，或已到達頁面底部。"
                : reliable ? "已找到穩定的垂直重疊。" : "無法可靠對齊，請調整重疊高度並確認接縫。"));
    }

    internal static bool AreSameImages(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return false;
        return IsDuplicate(ReadSample(first), ReadSample(second));
    }

    internal static ScrollMatch FindScroll(BitmapSource previous, BitmapSource current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.PixelWidth != current.PixelWidth || previous.PixelHeight != current.PixelHeight)
            throw new ArgumentException(I18n.T("拼接畫面必須來自同一個截圖範圍，且大小一致。"));
        var height = previous.PixelHeight;
        if (height < 48 || previous.PixelWidth < 32)
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
    internal static bool HasFixedEdgeBand(BitmapSource previous, BitmapSource current)
    {
        if (previous.PixelWidth != current.PixelWidth || previous.PixelHeight != current.PixelHeight) return false;
        var first = ReadSample(previous);
        var second = ReadSample(current);
        if (IsDuplicate(first, second)) return false;
        var maximumRows = Math.Min(first.Height / 4, 240);
        return FixedRows(first, second, maximumRows, false) >= 16
            || FixedRows(first, second, maximumRows, true) >= 16;
    }

    public static BitmapSource Stitch(
        IReadOnlyList<BitmapSource> frames,
        IReadOnlyList<int> overlaps,
        long maxPixels = MaxOutputPixels)
    {
        var outputPixels = CalculateOutputPixels(frames, overlaps);
        if (outputPixels > maxPixels)
            throw new InvalidOperationException(I18n.F("長截圖超過 {0:N0} 百萬像素上限，請分段截取。", maxPixels / 1_000_000));
        var width = frames[0].PixelWidth;
        var height = checked((int)(outputPixels / width));
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var rowOffset = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var source = AsBgra32(frames[index]);
            var skippedRows = index == 0 ? 0 : overlaps[index - 1];
            var copiedRows = source.PixelHeight - skippedRows;
            source.CopyPixels(new Int32Rect(0, skippedRows, width, copiedRows), pixels, stride, checked(rowOffset * stride));
            rowOffset += copiedRows;
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public static long CalculateOutputPixels(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> overlaps)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(overlaps);
        if (frames.Count == 0)
            throw new ArgumentException(I18n.T("請先加入至少一個畫面。"), nameof(frames));
        if (overlaps.Count != frames.Count - 1)
            throw new ArgumentException(I18n.T("每兩個連續畫面都必須指定一個重疊高度。"), nameof(overlaps));
        var width = frames[0].PixelWidth;
        long totalHeight = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.PixelWidth != width)
                throw new ArgumentException(I18n.T("每個畫面的寬度必須一致。"), nameof(frames));
            var overlap = index == 0 ? 0 : overlaps[index - 1];
            if (overlap < 0 || overlap >= frame.PixelHeight
                || (index > 0 && overlap > frames[index - 1].PixelHeight))
                throw new ArgumentOutOfRangeException(nameof(overlaps), I18n.T("重疊高度必須小於畫面高度。"));
            totalHeight = checked(totalHeight + frame.PixelHeight - overlap);
        }
        return checked(width * totalHeight);
    }

    /// <summary>Copies only the selected document rows, so a short result does not retain a full long image.</summary>
    internal static BitmapSource StitchRange(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> overlaps,
        int firstRow, int endRow)
    {
        var totalPixels = CalculateOutputPixels(frames, overlaps);
        var width = frames[0].PixelWidth;
        var totalHeight = totalPixels / width;
        if (firstRow < 0 || endRow <= firstRow || endRow > totalHeight)
            throw new ArgumentOutOfRangeException(nameof(endRow), I18n.T("起點與終點必須在已擷取範圍內。"));
        var height = endRow - firstRow;
        if ((long)width * height > MaxOutputPixels)
            throw new InvalidOperationException(I18n.F("長截圖超過 {0:N0} 百萬像素上限，請分段截取。", MaxOutputPixels / 1_000_000));
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        long documentRow = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var skipped = index == 0 ? 0 : overlaps[index - 1];
            var available = frames[index].PixelHeight - skipped;
            var from = Math.Max(documentRow, firstRow);
            var to = Math.Min(documentRow + available, endRow);
            if (to > from)
            {
                var source = AsBgra32(frames[index]);
                var sourceRow = checked(skipped + (int)(from - documentRow));
                var destinationOffset = checked((int)(from - firstRow) * stride);
                source.CopyPixels(new Int32Rect(0, sourceRow, width, checked((int)(to - from))), pixels, stride, destinationOffset);
            }
            documentRow += available;
            if (documentRow >= endRow) break;
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    internal static BitmapSource CreateJoinPreview(BitmapSource previous, BitmapSource current, int overlap)
    {
        var previousRows = Math.Min(100, previous.PixelHeight);
        var currentRows = Math.Min(100, current.PixelHeight - overlap);
        var upper = new CroppedBitmap(previous, new Int32Rect(0, previous.PixelHeight - previousRows, previous.PixelWidth, previousRows));
        var lower = new CroppedBitmap(current, new Int32Rect(0, overlap, current.PixelWidth, currentRows));
        upper.Freeze();
        lower.Freeze();
        return Stitch(new BitmapSource[] { upper, lower }, new[] { 0 });
    }

    private static BitmapSource AsBgra32(BitmapSource source)
        => source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

    private static SampleImage ReadSample(BitmapSource image)
        => image.IsFrozen ? Samples.GetValue(image, SampleImage.Read) : SampleImage.Read(image);

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
        public static SampleImage Read(BitmapSource image)
        {
            var source = AsBgra32(image);
            var stride = checked(source.PixelWidth * 4);
            // Read short strips rather than allocating another full-resolution image.
            var pixels = new byte[checked(stride * Math.Min(32, source.PixelHeight))];
            var columns = Math.Min(96, source.PixelWidth);
            var values = new byte[checked(columns * source.PixelHeight)];
            var left = source.PixelWidth / 12;
            var right = source.PixelWidth - 1 - left;
            for (var stripY = 0; stripY < source.PixelHeight; stripY += 32)
            {
                var rows = Math.Min(32, source.PixelHeight - stripY);
                source.CopyPixels(new Int32Rect(0, stripY, source.PixelWidth, rows), pixels, stride, 0);
                for (var row = 0; row < rows; row++)
                    for (var x = 0; x < columns; x++)
                    {
                        var pixelX = left + (right - left) * x / Math.Max(1, columns - 1);
                        var offset = row * stride + pixelX * 4;
                        values[(stripY + row) * columns + x] = (byte)((pixels[offset] * 29
                            + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8);
                    }
            }
            return new SampleImage(source.PixelHeight, columns, values);
        }
    }
}

namespace SnipFlow.Scroll;

public sealed record OverlapMatch(
    int OverlapPixels,
    double Confidence,
    double MeanError,
    bool IsDuplicate,
    bool IsReliable,
    string Message);

/// <summary>Finds a downward scroll between equally sized, stationary capture regions.</summary>
public static class ImageStitcher
{
    public const long MaxOutputPixels = 40_000_000;
    public const long MaxStoredPixels = 60_000_000;
    public const int MaxFrames = 20;

    public static OverlapMatch FindOverlap(BitmapSource previous, BitmapSource current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.PixelWidth != current.PixelWidth || previous.PixelHeight != current.PixelHeight)
            throw new ArgumentException("拼接畫面必須來自同一個截圖範圍，且大小一致。");
        if (previous.PixelHeight < 48 || previous.PixelWidth < 32)
            return new OverlapMatch(0, 0, 1, false, false, "範圍太小，請手動指定重疊位置。");

        var first = SampleImage.Read(previous);
        var second = SampleImage.Read(current);
        var unchanged = Compare(first, second, 0, dense: true);
        if (unchanged.Error <= 0.0015)
            return new OverlapMatch(previous.PixelHeight, 1, unchanged.Error, true, true, "畫面尚未捲動，或已到達頁面底部。");

        var height = previous.PixelHeight;
        var minimumOverlap = Math.Max(24, height / 8);
        var minimumAdvance = Math.Max(4, height / 100);
        var maximumAdvance = height - minimumOverlap;
        var coarseStep = Math.Max(1, height / 480);
        var scores = new List<(int Advance, MatchScore Score)>();
        var bestAdvance = minimumAdvance;
        var bestScore = new MatchScore(double.MaxValue, 0);

        for (var advance = minimumAdvance; advance <= maximumAdvance; advance += coarseStep)
        {
            var score = Compare(first, second, advance, dense: false);
            scores.Add((advance, score));
            if (score.Error < bestScore.Error)
            {
                bestScore = score;
                bestAdvance = advance;
            }
        }

        var coarseWinner = bestAdvance;
        for (var advance = Math.Max(minimumAdvance, coarseWinner - coarseStep);
             advance <= Math.Min(maximumAdvance, coarseWinner + coarseStep); advance++)
        {
            var score = Compare(first, second, advance, dense: false);
            if (score.Error < bestScore.Error)
            {
                bestScore = score;
                bestAdvance = advance;
            }
        }

        // Reject repeated lines and blank regions whose best position is indistinguishable
        // from another scroll offset. The user must review those joins explicitly.
        var exclusionDistance = Math.Max(5, coarseStep * 3);
        var alternative = scores
            .Where(entry => Math.Abs(entry.Advance - bestAdvance) > exclusionDistance)
            .Select(entry => entry.Score.Error)
            .DefaultIfEmpty(1d)
            .Min();
        var separation = Math.Max(0, alternative - bestScore.Error);
        var accuracy = Math.Clamp(1 - bestScore.Error / 0.07, 0, 1);
        var distinctiveness = Math.Clamp(separation / 0.028, 0, 1);
        var confidence = accuracy * 0.7 + distinctiveness * 0.3;
        var reliable = bestScore.Error <= 0.035
            && separation >= Math.Max(0.008, bestScore.Error * 0.35)
            && bestScore.DetailFraction >= 0.025;

        return new OverlapMatch(
            height - bestAdvance, confidence, bestScore.Error, false, reliable,
            reliable ? "已找到穩定的垂直重疊。" : "無法可靠對齊，請調整重疊高度並確認接縫。");
    }

    public static BitmapSource Stitch(
        IReadOnlyList<BitmapSource> frames,
        IReadOnlyList<int> overlaps,
        long maxPixels = MaxOutputPixels)
    {
        var outputPixels = CalculateOutputPixels(frames, overlaps);
        if (outputPixels > maxPixels)
            throw new InvalidOperationException($"長截圖超過 {maxPixels / 1_000_000:N0} 百萬像素上限，請分段截取。");

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
            throw new ArgumentException("請先加入至少一個畫面。", nameof(frames));
        if (overlaps.Count != frames.Count - 1)
            throw new ArgumentException("每兩個連續畫面都必須指定一個重疊高度。", nameof(overlaps));

        var width = frames[0].PixelWidth;
        long totalHeight = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.PixelWidth != width)
                throw new ArgumentException("每個畫面的寬度必須一致。", nameof(frames));
            var overlap = index == 0 ? 0 : overlaps[index - 1];
            if (overlap < 0 || overlap >= frame.PixelHeight
                || (index > 0 && overlap > frames[index - 1].PixelHeight))
                throw new ArgumentOutOfRangeException(nameof(overlaps), "重疊高度必須小於畫面高度。");
            totalHeight = checked(totalHeight + frame.PixelHeight - overlap);
        }
        return checked(width * totalHeight);
    }

    internal static BitmapSource CreateJoinPreview(BitmapSource previous, BitmapSource current, int overlap)
    {
        var previousRows = Math.Min(140, previous.PixelHeight);
        var currentRows = Math.Min(140, current.PixelHeight - overlap);
        var upper = new CroppedBitmap(previous, new Int32Rect(0, previous.PixelHeight - previousRows, previous.PixelWidth, previousRows));
        var lower = new CroppedBitmap(current, new Int32Rect(0, overlap, current.PixelWidth, currentRows));
        upper.Freeze();
        lower.Freeze();
        return Stitch(new BitmapSource[] { upper, lower }, new[] { 0 });
    }

    private static BitmapSource AsBgra32(BitmapSource source)
        => source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

    private static MatchScore Compare(SampleImage first, SampleImage second, int advance, bool dense)
    {
        var overlap = first.Height - advance;
        var edgeMargin = dense ? 0 : Math.Min(12, overlap / 12);
        var rowStep = dense ? 1 : Math.Max(1, overlap / 80);
        long error = 0;
        var sampleCount = 0;
        var contentSamples = 0;
        var detailCount = 0;
        for (var y = edgeMargin; y < overlap - edgeMargin; y += rowStep)
        {
            var firstOffset = (y + advance) * first.Columns;
            var secondOffset = y * second.Columns;
            for (var x = 1; x < first.Columns - 1; x++)
            {
                var firstPixel = first.Values[firstOffset + x];
                var secondPixel = second.Values[secondOffset + x];
                var detailed = Math.Abs(secondPixel - second.Values[secondOffset + x - 1]) > 12
                    || Math.Abs(firstPixel - first.Values[firstOffset + x - 1]) > 12;
                if (detailed)
                    detailCount++;
                // White page margins must not drown out mismatched text. Match the
                // actual ink and image content, while retaining a separate detail check.
                if (detailed || firstPixel < 240 || secondPixel < 240)
                {
                    error += Math.Abs(firstPixel - secondPixel);
                    contentSamples++;
                }
                sampleCount++;
            }
        }
        return sampleCount == 0 ? new MatchScore(1, 0)
            : new MatchScore(contentSamples == 0 ? 0 : (double)error / (contentSamples * 255d), (double)detailCount / sampleCount);
    }

    private readonly record struct MatchScore(double Error, double DetailFraction);

    private sealed record SampleImage(int Height, int Columns, byte[] Values)
    {
        public static SampleImage Read(BitmapSource image)
        {
            var source = AsBgra32(image);
            var stride = checked(source.PixelWidth * 4);
            var pixels = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(pixels, stride, 0);
            var columns = Math.Min(96, source.PixelWidth);
            var values = new byte[checked(columns * source.PixelHeight)];
            // Leave side gutters and scroll bars out of the comparison.
            var left = source.PixelWidth / 12;
            var right = source.PixelWidth - 1 - left;
            for (var y = 0; y < source.PixelHeight; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    var pixelX = left + (right - left) * x / Math.Max(1, columns - 1);
                    var offset = y * stride + pixelX * 4;
                    values[y * columns + x] = (byte)((pixels[offset] * 29 + pixels[offset + 1] * 150 + pixels[offset + 2] * 77) >> 8);
                }
            }
            return new SampleImage(source.PixelHeight, columns, values);
        }
    }
}

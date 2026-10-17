using System.Buffers.Binary;
using System.Windows.Markup;
using SnipFlow.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinBitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;
using BitmapDecoder = System.Windows.Media.Imaging.BitmapDecoder;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace SnipFlow.Video;

/// <summary>Shares one layout between inline editing, preview and exported overlay pixels.</summary>
internal static class VideoOverlayRenderer
{
    internal static readonly FontFamily TextFont = new("Segoe UI, Microsoft JhengHei UI");

    internal static FrameworkElement CreateVisual(VideoOverlay overlay)
    {
        FrameworkElement visual;
        if (overlay.Kind == VideoOverlayKind.Text)
        {
            var text = new TextBox
            {
                Text = overlay.Text, Width = overlay.Width, Height = overlay.Height,
                FontFamily = TextFont, FontSize = Math.Clamp(overlay.FontSize, 8, 240),
                FontWeight = FontWeights.SemiBold, Language = XmlLanguage.GetLanguage("en-US"),
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Background = Brushes.Transparent, Foreground = BrushFor(overlay.ColorHex),
                BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0),
                Style = null, IsReadOnly = true, IsReadOnlyCaretVisible = false,
                Focusable = false, IsHitTestVisible = false, ClipToBounds = true,
                SpellCheck = { IsEnabled = false }
            };
            text.CaretBrush = text.Foreground;
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(text, TextRenderingMode.Grayscale);
            TextBlock.SetLineStackingStrategy(text, LineStackingStrategy.MaxHeight);
            visual = text;
        }
        else
        {
            visual = new Image
            {
                Source = overlay.ImagePng is { } png ? DecodePng(png) : null,
                Width = overlay.Width, Height = overlay.Height, Stretch = Stretch.Uniform,
                IsHitTestVisible = false, ClipToBounds = true
            };
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        }
        return visual;
    }

    private static Brush BrushFor(string hex)
    {
        var color = VideoEditDocument.IsColor(hex) ? (Color)ColorConverter.ConvertFromString(hex) : Colors.White;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static (int Width, int Height) ValidatePng(byte[] png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 33 || png.Length > VideoEditDocument.MaximumImageBytes
            || !png.AsSpan(0, 8).SequenceEqual(signature)
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidOperationException(I18n.T("圖片物件資料無效或過大。"));
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 16_777_216)
            throw new InvalidOperationException(I18n.T("圖片物件資料無效或過大。"));
        return (width, height);
    }

    internal static BitmapSource DecodePng(byte[] png)
    {
        ValidatePng(png);
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static (byte[] Png, int Width, int Height) LoadImagePng(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.Length > 40L * 1024 * 1024)
            throw new InvalidOperationException(I18n.T("圖片檔案不存在或超過 40 MB。"));
        int width, height;
        using (var header = File.OpenRead(file.FullName))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) throw new FileFormatException(I18n.T("圖片物件資料無效或過大。"));
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
                throw new InvalidOperationException(I18n.T("圖片尺寸過大。"));
        }
        var maximum = 4096;
        for (;;)
        {
            using var stream = File.OpenRead(file.FullName);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, maximum);
            else bitmap.DecodePixelHeight = Math.Min(height, maximum);
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            using var encoded = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, null));
            encoder.Save(encoded);
            if (encoded.Length <= VideoEditDocument.MaximumImageBytes)
                return (encoded.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
            maximum /= 2;
            if (maximum < 512) throw new InvalidOperationException(I18n.T("圖片物件資料無效或過大。"));
        }
    }

    internal static async Task<StorageFile> WriteOverlayAsync(VideoOverlay overlay, string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Layout in source pixels at 96 DPI, independent of the editor's viewbox or screen DPI.
        var width = checked((int)Math.Ceiling(overlay.Width));
        var height = checked((int)Math.Ceiling(overlay.Height));
        var visual = CreateVisual(overlay);
        visual.Measure(new Size(overlay.Width, overlay.Height));
        visual.Arrange(new Rect(0, 0, overlay.Width, overlay.Height));
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, checked(width * 4), 0);
        cancellationToken.ThrowIfCancellationRequested();

        // The export workspace has already created and registered this owned file.
        var file = await VideoNativeOperation.AwaitAsync(StorageFile.GetFileFromPathAsync(VideoFileGuard.ResolveFile(path)), cancellationToken);
        using var stream = await VideoNativeOperation.AwaitAsync(file.OpenAsync(FileAccessMode.ReadWrite), cancellationToken);
        var encoder = await VideoNativeOperation.AwaitAsync(WinBitmapEncoder.CreateAsync(WinBitmapEncoder.PngEncoderId, stream), cancellationToken);
        // Encode the premultiplied WPF buffer explicitly for MediaClip's image path.
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 96, 96, pixels);
        await VideoNativeOperation.AwaitAsync(encoder.FlushAsync(), cancellationToken);
        return file;
    }
}

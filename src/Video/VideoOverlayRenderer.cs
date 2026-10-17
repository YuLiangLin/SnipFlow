using System.Buffers.Binary;
using Avalonia.Media.TextFormatting;
using SnipFlow.Services;

namespace SnipFlow.Video;

/// <summary>Shares one layout between inline editing, preview and exported overlay pixels.</summary>
internal static partial class VideoOverlayRenderer
{
    internal static FontFamily TextFont => UiThemes.AppFont;
    internal static readonly FontWeight TextWeight = FontWeight.SemiBold;

    /// <summary>The preview visual. It draws exactly what <see cref="Render"/> writes into the export.</summary>
    internal static Control CreateVisual(VideoOverlay overlay)
        => new OverlayVisual(overlay, overlay.Kind == VideoOverlayKind.Image && overlay.ImagePng is { } png ? DecodePng(png) : null)
        {
            Width = overlay.Width, Height = overlay.Height, IsHitTestVisible = false, ClipToBounds = true
        };

    /// <summary>An editable text box laid out like the drawn text (no padding, border or background).</summary>
    internal static TextBox CreateTextEditor(VideoOverlay overlay)
    {
        var brush = BrushFor(overlay.ColorHex);
        var editor = new TextBox
        {
            Text = overlay.Text, Width = overlay.Width, Height = overlay.Height, MinHeight = 0, MinWidth = 0,
            FontFamily = TextFont, FontSize = Math.Clamp(overlay.FontSize, 8, 240), FontWeight = TextWeight,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Background = Brushes.Transparent, Foreground = brush,
            CaretBrush = brush, SelectionForegroundBrush = brush, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Margin = new Thickness(0), CornerRadius = new CornerRadius(0), VerticalContentAlignment = VerticalAlignment.Top,
            ClipToBounds = true, MaxLength = 8000
        };
        // The Fluent template paints focus and hover states from these keys; keep the video visible underneath.
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                     "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            editor.Resources[key] = Brushes.Transparent;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            editor.Resources[key] = brush;
        editor.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        editor.Resources["TextControlThemePadding"] = new Thickness(0);
        TextOptions.SetTextRenderingMode(editor, TextRenderingMode.Antialias);
        return editor;
    }

    internal static IBrush BrushFor(string hex)
        => new SolidColorBrush(VideoEditDocument.IsColor(hex) && Color.TryParse(hex, out var color) ? color : Colors.White);

    /// <summary>Draws an overlay into a box of <paramref name="size"/> source pixels at the origin.</summary>
    internal static void Draw(DrawingContext context, VideoOverlay overlay, Bitmap? image, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        using var clip = context.PushClip(new Rect(size));
        // Grayscale antialiasing: subpixel text on a transparent layer would fringe when composited.
        using var text = context.PushTextOptions(new TextOptions { TextRenderingMode = TextRenderingMode.Antialias });
        using var quality = context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality });
        if (overlay.Kind == VideoOverlayKind.Text)
        {
            if (overlay.Text.Length == 0) return;
            using var layout = CreateTextLayout(overlay, size.Width);
            layout.Draw(context, new Point(0, 0));
        }
        else if (image is not null)
        {
            // Stretch.Uniform, centered, as the WPF Image did.
            var pixels = image.PixelSize;
            var scale = Math.Min(size.Width / pixels.Width, size.Height / pixels.Height);
            var width = pixels.Width * scale; var height = pixels.Height * scale;
            context.DrawImage(image, new Rect(0, 0, pixels.Width, pixels.Height),
                new Rect((size.Width - width) / 2, (size.Height - height) / 2, width, height));
        }
    }

    internal static TextLayout CreateTextLayout(VideoOverlay overlay, double width)
        => new(overlay.Text, new Typeface(TextFont, FontStyle.Normal, TextWeight), Math.Clamp(overlay.FontSize, 8, 240),
            BrushFor(overlay.ColorHex), TextAlignment.Left, TextWrapping.Wrap, TextTrimming.None, null,
            FlowDirection.LeftToRight, Math.Max(1, width), double.PositiveInfinity, double.NaN, 0, 0, null, null);

    /// <summary>Renders the overlay in source pixels at 96 DPI, independent of the editor's zoom or screen DPI.</summary>
    internal static Bitmap Render(VideoOverlay overlay)
    {
        var width = checked((int)Math.Ceiling(overlay.Width));
        var height = checked((int)Math.Ceiling(overlay.Height));
        using var image = overlay.Kind == VideoOverlayKind.Image && overlay.ImagePng is { } png ? DecodePng(png) : null;
        return ImageTools.Render(new PixelSize(width, height),
            context => Draw(context, overlay, image, new Size(overlay.Width, overlay.Height)));
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

    internal static Bitmap DecodePng(byte[] png)
    {
        ValidatePng(png);
        return ImageTools.Decode(png);
    }

    internal static (byte[] Png, int Width, int Height) LoadImagePng(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.Length > 40L * 1024 * 1024)
            throw new InvalidOperationException(I18n.T("圖片檔案不存在或超過 40 MB。"));
        int width, height;
        using (var header = File.OpenRead(file.FullName))
        {
            // Read the dimensions from the header before decoding a potentially huge image.
            if (ReadImageSize(header) is not { } size) throw new InvalidDataException(I18n.T("圖片物件資料無效或過大。"));
            (width, height) = size;
            if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
                throw new InvalidOperationException(I18n.T("圖片尺寸過大。"));
        }
        var maximum = 4096;
        for (;;)
        {
            using var stream = File.OpenRead(file.FullName);
            Bitmap bitmap;
            try
            {
                bitmap = width >= height
                    ? Bitmap.DecodeToWidth(stream, Math.Min(width, maximum), BitmapInterpolationMode.HighQuality)
                    : Bitmap.DecodeToHeight(stream, Math.Min(height, maximum), BitmapInterpolationMode.HighQuality);
            }
            catch (Exception error) when (error is not (IOException or OutOfMemoryException))
            {
                throw new InvalidDataException(I18n.T("圖片物件資料無效或過大。"), error);
            }
            using (bitmap)
            {
                var encoded = ImageTools.EncodePng(bitmap);
                if (encoded.Length <= VideoEditDocument.MaximumImageBytes)
                    return (encoded, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            }
            maximum /= 2;
            if (maximum < 512) throw new InvalidOperationException(I18n.T("圖片物件資料無效或過大。"));
        }
    }

    /// <summary>PNG IHDR or JPEG SOFn dimensions, or null for other or damaged data.</summary>
    internal static (int Width, int Height)? ReadImageSize(Stream stream)
    {
        Span<byte> head = stackalloc byte[24];
        if (stream.ReadAtLeast(head, 24, throwOnEndOfStream: false) < 24) return null;
        if (head[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]) && head.Slice(12, 4).SequenceEqual("IHDR"u8))
            return (BinaryPrimitives.ReadInt32BigEndian(head.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(head.Slice(20, 4)));
        if (head[0] != 0xFF || head[1] != 0xD8) return null;
        stream.Position = 2;
        Span<byte> segment = stackalloc byte[9];
        for (var guard = 0; guard < 4096; guard++)
        {
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) return null;
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;
            if (stream.ReadAtLeast(segment[..2], 2, throwOnEndOfStream: false) < 2) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(segment[..2]);
            if (length < 2) return null;
            // SOF0-SOF15 except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (length < 7 || stream.ReadAtLeast(segment[..5], 5, throwOnEndOfStream: false) < 5) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3, 2)), BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1, 2)));
            }
            if (marker is 0xD9 or 0xDA) return null;
            stream.Seek(length - 2, SeekOrigin.Current);
        }
        return null;
    }

    /// <summary>Draws one overlay through the shared renderer; the canvas sizes it in source pixels.</summary>
    private sealed class OverlayVisual(VideoOverlay overlay, Bitmap? image) : Control
    {
        public override void Render(DrawingContext context) => Draw(context, overlay, image, Bounds.Size);
    }
}

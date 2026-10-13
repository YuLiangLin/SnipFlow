using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SnipFlow.Video;

/// <summary>A source-time range. Playback remains a separate accessible Slider.</summary>
internal sealed class VideoEditorRangeBar : UserControl
{
    readonly Canvas surface = new() { Background = Brushes.Transparent };
    readonly Border track = new() { Height = 4, CornerRadius = new CornerRadius(2) };
    readonly Border range = new() { Height = 4, CornerRadius = new CornerRadius(2) };
    readonly Border playhead = new() { Width = 1, Height = 28, IsHitTestVisible = false };
    readonly Thumb startHandle = new() { Width = 10, Height = 24, Focusable = true };
    readonly Thumb endHandle = new() { Width = 10, Height = 24, Focusable = true };
    double duration = 1, start, end = 1, position;

    public event Action<double, double>? RangeChanged;
    public event Action<double>? SeekRequested;

    public VideoEditorRangeBar()
    {
        Height = 32; MinWidth = 96; Content = surface;
        track.Background = BrushResource("Line", "#383838");
        range.Background = BrushResource("Muted", "#A3A3A3");
        playhead.Background = BrushResource("Ink", "#ECECEC");
        foreach (var handle in new[] { startHandle, endHandle })
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, BrushResource("Accent", "#E8E8E8"));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            handle.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            handle.Cursor = Cursors.SizeWE;
            handle.DragDelta += (_, args) =>
            {
                var seconds = args.HorizontalChange / UsableWidth * duration;
                MoveHandle(handle == startHandle, (handle == startHandle ? start : end) + seconds);
            };
            handle.PreviewKeyDown += (_, args) =>
            {
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1d : .1d;
                var value = handle == startHandle ? start : end;
                if (args.Key == Key.Left) value -= step;
                else if (args.Key == Key.Right) value += step;
                else if (args.Key == Key.Home) value = 0;
                else if (args.Key == Key.End) value = duration;
                else return;
                MoveHandle(handle == startHandle, value); args.Handled = true;
            };
        }
        surface.Children.Add(track); surface.Children.Add(range); surface.Children.Add(playhead);
        surface.Children.Add(startHandle); surface.Children.Add(endHandle);
        surface.SizeChanged += (_, _) => ArrangeRange();
        surface.MouseLeftButtonDown += (_, args) =>
        {
            if (!IsEnabled || args.OriginalSource is Thumb) return;
            var seconds = (args.GetPosition(surface).X - 8) / UsableWidth * duration;
            SeekRequested?.Invoke(Math.Clamp(seconds, start, end));
            args.Handled = true;
        };
    }

    public void SetLabels(string startLabel, string endLabel)
    {
        startHandle.ToolTip = startLabel; endHandle.ToolTip = endLabel;
        AutomationProperties.SetName(startHandle, startLabel);
        AutomationProperties.SetName(endHandle, endLabel);
    }

    public void Update(double sourceDuration, double trimStart, double trimEnd, double playPosition)
    {
        duration = Math.Max(.001, sourceDuration);
        start = Math.Clamp(trimStart, 0, duration);
        end = Math.Clamp(trimEnd, start, duration);
        position = Math.Clamp(playPosition, 0, duration);
        ArrangeRange();
    }

    double UsableWidth => Math.Max(1, surface.ActualWidth - 16);

    void MoveHandle(bool isStart, double value)
    {
        if (!IsEnabled) return;
        var minimumSpan = Math.Min(.1, duration);
        if (isStart) start = Math.Clamp(value, 0, Math.Max(0, end - minimumSpan));
        else end = Math.Clamp(value, Math.Min(duration, start + minimumSpan), duration);
        ArrangeRange(); RangeChanged?.Invoke(start, end);
    }

    void ArrangeRange()
    {
        var left = 8 + start / duration * UsableWidth;
        var right = 8 + end / duration * UsableWidth;
        track.Width = UsableWidth; Canvas.SetLeft(track, 8); Canvas.SetTop(track, 14);
        range.Width = Math.Max(0, right - left); Canvas.SetLeft(range, left); Canvas.SetTop(range, 14);
        Canvas.SetLeft(startHandle, left - 5); Canvas.SetTop(startHandle, 4);
        Canvas.SetLeft(endHandle, right - 5); Canvas.SetTop(endHandle, 4);
        Canvas.SetLeft(playhead, 8 + position / duration * UsableWidth); Canvas.SetTop(playhead, 2);
    }

    static Brush BrushResource(string key, string fallback)
        => Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
}

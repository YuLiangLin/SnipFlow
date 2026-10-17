using Avalonia.Automation;
using Avalonia.Input;

namespace SnipFlow.Video;

/// <summary>A source-time range. Playback remains a separate accessible Slider.</summary>
internal sealed class VideoEditorRangeBar : UserControl
{
    readonly Canvas surface = new() { Background = Brushes.Transparent };
    readonly Border track = new() { Height = 4, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
    readonly Border range = new() { Height = 4, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
    readonly Border playhead = new() { Width = 1, Height = 28, IsHitTestVisible = false };
    readonly Border startHandle = new() { Width = 10, Height = 24, Focusable = true };
    readonly Border endHandle = new() { Width = 10, Height = 24, Focusable = true };
    double duration = 1, start, end = 1, position, grabOffset;
    Border? dragging;

    public event Action<double, double>? RangeChanged;
    public event Action<double>? SeekRequested;

    public VideoEditorRangeBar()
    {
        Height = 32; MinWidth = 96; Content = surface;
        track.Background = ThemeService.Brush("Line");
        range.Background = ThemeService.Brush("Muted");
        playhead.Background = ThemeService.Brush("Ink");
        foreach (var handle in new[] { startHandle, endHandle })
        {
            handle.Background = ThemeService.Brush("Accent");
            handle.CornerRadius = new CornerRadius(3);
            handle.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            // The handle follows the pointer; the grab offset keeps it from jumping under the cursor.
            handle.PointerPressed += (_, args) =>
            {
                if (!IsEnabled || !args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                dragging = handle; handle.Focus();
                grabOffset = args.GetPosition(surface).X - (Canvas.GetLeft(handle) + handle.Width / 2);
                args.Pointer.Capture(handle); args.Handled = true;
            };
            handle.PointerMoved += (_, args) =>
            {
                if (dragging != handle) return;
                var seconds = (args.GetPosition(surface).X - grabOffset - 8) / UsableWidth * duration;
                MoveHandle(handle == startHandle, seconds); args.Handled = true;
            };
            handle.PointerReleased += (_, args) =>
            {
                if (dragging != handle) return;
                dragging = null; args.Pointer.Capture(null); args.Handled = true;
            };
            handle.PointerCaptureLost += (_, _) => { if (dragging == handle) dragging = null; };
            handle.AddHandler(KeyDownEvent, (_, args) =>
            {
                var step = args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1d : .1d;
                var value = handle == startHandle ? start : end;
                if (args.Key == Key.Left) value -= step;
                else if (args.Key == Key.Right) value += step;
                else if (args.Key == Key.Home) value = 0;
                else if (args.Key == Key.End) value = duration;
                else return;
                MoveHandle(handle == startHandle, value); args.Handled = true;
            }, RoutingStrategies.Tunnel);
            handle.GotFocus += (_, _) => handle.BorderThickness = new Thickness(1);
            handle.LostFocus += (_, _) => handle.BorderThickness = new Thickness(0);
            handle.BorderBrush = ThemeService.Brush("Panel");
        }
        surface.Children.Add(track); surface.Children.Add(range); surface.Children.Add(playhead);
        surface.Children.Add(startHandle); surface.Children.Add(endHandle);
        surface.SizeChanged += (_, _) => ArrangeRange();
        surface.PointerPressed += (_, args) =>
        {
            if (!IsEnabled || args.Handled || !args.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
            var seconds = (args.GetPosition(surface).X - 8) / UsableWidth * duration;
            SeekRequested?.Invoke(Math.Clamp(seconds, start, end));
            args.Handled = true;
        };
    }

    public void SetLabels(string startLabel, string endLabel)
    {
        ToolTip.SetTip(startHandle, startLabel); ToolTip.SetTip(endHandle, endLabel);
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

    double UsableWidth => Math.Max(1, surface.Bounds.Width - 16);

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
}

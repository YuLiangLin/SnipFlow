using SnipFlow.Capture;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Scroll;

public sealed partial class ScrollCaptureWindow
{
    private Button _startButton = null!;
    private Button _endButton = null!;
    private TextBlock _rangeSummary = null!;
    private int? _rangeStart;
    private int? _rangeEnd;
    private bool _pickingBoundary;
    private BoundaryPickWindow? _activePicker;
    private BitmapSource? _pendingAlignedRaw;
    private int _pendingAlignedPosition;

    private StackPanel CreateRangeControls()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new WrapPanel();
        _startButton = MakeButton("首頁選起點");
        _endButton = MakeButton("末頁選終點");
        _startButton.Click += async (_, _) => await PickBoundaryAsync(isStart: true);
        _endButton.Click += async (_, _) => await PickBoundaryAsync(isStart: false);
        actions.Children.Add(_startButton);
        actions.Children.Add(_endButton);
        panel.Children.Add(actions);
        _rangeSummary = new TextBlock
        {
            Foreground = Brush("#A3A3A3"), FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        };
        panel.Children.Add(_rangeSummary);
        return panel;
    }

    private async Task PickBoundaryAsync(bool isStart)
    {
        if (_finishing || _closed || _pickingBoundary || _manualPanel.Visibility == Visibility.Visible) return;
        if (!isStart && NeedsStart) return;
        bool wasPaused = _userPaused;
        bool startWasRequired = NeedsStart;
        bool timerWasRunning = _timer.IsEnabled;
        bool committed = false;
        bool ownsBusy = false;
        _pickingBoundary = true;
        _timer.Stop();
        UpdateButtons();
        try
        {
            while (_busy) await Task.Delay(30, _lifetime.Token);
            if (_closed) return;
            _busy = ownsBusy = true;
            if (_frames.Count == 0)
            {
                if (!CanInspectTarget()) return;
                var first = ScreenshotService.CaptureRectangle(_targetBounds);
                if (!CanInspectTarget()) return;
                AddFirstFrame(first);
            }
            // Retained snapshots remain available while paused, covered, or at the memory cap.
            // Changing a boundary never clears frames or recaptures an unrelated foreground window.
            if (!isStart && _rangeEnd is null) TryAcceptPendingTail();
            var index = isStart ? 0 : _frames.Count - 1;
            var position = _positions[index];
            var raw = _frames[index];
            var oldBoundary = isStart ? _rangeStart : _rangeEnd;
            var initialPixel = oldBoundary is not null
                ? Math.Clamp(oldBoundary.Value - position - _topTrim, 0, isStart ? BodyHeight - 1 : BodyHeight)
                : isStart ? 0 : BodyHeight;
            var visibleBounds = new DrawingRectangle(_targetBounds.X, _targetBounds.Y + _topTrim, _targetBounds.Width, BodyHeight);
            _activePicker = new BoundaryPickWindow(visibleBounds, Crop(raw), isStart,
                initialPixel, fromCollectedFrame: true) { Owner = this };
            bool confirmed = _activePicker.ShowDialog() == true;
            var pixel = _activePicker.SelectedPixel;
            _activePicker = null;
            if (_closed || !confirmed || pixel is null) return;
            var boundary = checked(position + _topTrim + pixel.Value);
            if (isStart)
            {
                if (_rangeEnd is not null && boundary >= _rangeEnd.Value)
                {
                    SetStatus("起點須在終點之前，請重新選取。", warning: true);
                    return;
                }
                _rangeStart = boundary;
                _userPaused = startWasRequired ? false : wasPaused;
                if (!_userPaused && _rangeEnd is null) _timer.Start();
                SetStatus("起點已調整；已收集內容會保留。");
            }
            else
            {
                var start = _rangeStart ?? checked(_positions[0] + _topTrim);
                if (boundary <= start)
                {
                    SetStatus("終點須在起點之後，請選取最後一則訊息的下緣。", warning: true);
                    return;
                }
                _rangeEnd = boundary;
                _userPaused = true;
                ResetObservation();
                SetStatus("終點已選；只保留已收集內容到此處。按「完成」產生長圖。");
            }
            HideFinishNotice();
            committed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            SettingsStore.Log(exception);
            if (!_closed) SetStatus(I18n.F("選取範圍未完成：{0}", exception.Message), warning: true);
        }
        finally
        {
            _activePicker = null;
            _pickingBoundary = false;
            if (ownsBusy) _busy = false;
            if (!_closed)
            {
                if (!committed)
                {
                    // A memory-limit pause discovered while flushing the tail must stay paused.
                    _userPaused = wasPaused || _limitReached;
                    if (timerWasRunning && !NeedsStart && _rangeEnd is null && !_userPaused) _timer.Start();
                }
                RefreshPreview(); UpdateDimensions(); UpdateButtons();
            }
        }
    }

    private bool CanInspectTarget()
    {
        if (_closed || _finishing) return false;
        if (_target.CanObserve(_handle, _toolbarExcluded, out var reason, allowToolbarForeground: true)) return true;
        SetStatus(reason, warning: true);
        return false;
    }

    private bool CanTrimRange(int top, int bottom)
    {
        if (_frames.Count == 0) return true;
        var first = checked(_positions[0] + top);
        var end = checked(_positions[^1] + _targetBounds.Height - bottom);
        if ((_rangeStart is null || (_rangeStart >= first && _rangeStart < end))
            && (_rangeEnd is null || (_rangeEnd > (_rangeStart ?? first) && _rangeEnd <= end))) return true;
        SetStatus("裁切會排除已選起點或終點，請減少裁切或重新選取範圍。", warning: true);
        return false;
    }

    private void InvalidateRangeAfterUndo()
    {
        _rangeEnd = null;
        if (_rangeStart is not null && _frames.Count > 0
            && (_rangeStart < _positions[0] + _topTrim || _rangeStart >= _positions[^1] + _targetBounds.Height - _bottomTrim))
            _rangeStart = null;
    }

    private void UpdateRangeControls()
    {
        if (_startButton is null) return;
        bool idle = !_finishing && !_closed && !_pickingBoundary && _manualPanel.Visibility != Visibility.Visible;
        _startButton.IsEnabled = idle;
        _endButton.IsEnabled = idle && _frames.Count > 0 && !NeedsStart;
        _rangeSummary.Text = I18n.T(_rangeEnd is not null ? "只保留已收集內容到選定終點。"
            : _rangeStart is not null ? "起點已調整；終點隨收集內容延伸。" : "未調整範圍；保留所有已收集內容。");
        _instruction.Text = I18n.T(_workflow == ScrollCaptureWorkflow.Precise
            ? NeedsStart ? "從首頁選起點，再捲動對話。終點可從末頁選取。"
                : _rangeEnd is null ? "捲動對話後，從已收集末頁選終點。" : "範圍已選，按「完成」產生長圖。"
            : _rangeEnd is not null ? "範圍已選，按「完成」產生長圖。"
                : _userPaused ? "已暫停；可繼續捲動或完成已收集內容。"
                : "在原視窗慢慢捲動，完成後按「完成」。");
    }
}
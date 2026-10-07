using SnipFlow.Capture;
using SnipFlow.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Scroll;

public sealed partial class ScrollCaptureWindow
{
    Button _startButton = null!;
    Button _endButton = null!;
    TextBlock _rangeSummary = null!;
    int? _rangeStart;
    int? _rangeEnd;
    bool _pickingBoundary;
    BoundaryPickWindow? _activePicker;
    BitmapSource? _pendingAlignedRaw;
    int _pendingAlignedPosition;

    StackPanel CreateRangeControls()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _startButton = MakeButton("選起點", accent: true);
        _endButton = MakeButton("選終點", accent: true);
        _startButton.Click += async (_, _) => await PickBoundaryAsync(isStart: true);
        _endButton.Click += async (_, _) => await PickBoundaryAsync(isStart: false);
        actions.Children.Add(_startButton);
        actions.Children.Add(_endButton);
        panel.Children.Add(actions);
        _rangeSummary = new TextBlock { Foreground = Brush("#A3A3A3"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(_rangeSummary);
        return panel;
    }

    async Task PickBoundaryAsync(bool isStart)
    {
        if (_finishing || _closed || _pickingBoundary || _manualPanel.Visibility == Visibility.Visible) return;
        if (!isStart && _rangeStart is null) return;
        bool wasPaused = _userPaused;
        bool timerWasRunning = _timer.IsEnabled;
        bool committed = false;
        bool ownsBusy = false;
        _pickingBoundary = true;
        _timer.Stop();
        UpdateButtons();
        try
        {
            // Stop new observation immediately; let any running matcher release its snapshot.
            while (_busy)
                await Task.Delay(30, _lifetime.Token);
            if (_closed) return;
            _busy = ownsBusy = true;
            BitmapSource raw;
            bool fromCollectedFrame = false;
            if (CanInspectTarget())
            {
                raw = ScreenshotService.CaptureRectangle(_targetBounds);
                if (!CanInspectTarget())
                {
                    if (isStart) return;
                    raw = _frames[^1];
                    fromCollectedFrame = true;
                }
            }
            else
            {
                if (isStart) return;
                raw = _frames[^1];
                fromCollectedFrame = true;
            }
            int position = fromCollectedFrame ? _positions[^1] : 0;
            FrameAnalysis? analysis = null;
            if (!isStart && !fromCollectedFrame)
            {
                var anchor = Crop(_anchorRaw ?? _frames[^1]);
                var anchorPosition = _anchorPosition;
                var body = Crop(raw);
                var bodies = _frames.Select(Crop).ToArray();
                var positions = _positions.ToArray();
                analysis = await Task.Run(() => AnalyzeFrame(anchor, anchorPosition, body, bodies, positions, _lifetime.Token), _lifetime.Token);
                if (!CanInspectTarget()) return;
                if (analysis.Kind == FrameKind.Unreliable && _pendingAlignedRaw is not null)
                {
                    var intermediate = _pendingAlignedRaw;
                    var intermediatePosition = _pendingAlignedPosition;
                    if (TryAcceptFrame(intermediate, intermediatePosition, intermediatePosition < _positions[0]))
                    {
                        bodies = _frames.Select(Crop).ToArray(); positions = _positions.ToArray();
                        anchor = Crop(_anchorRaw!); anchorPosition = _anchorPosition;
                        analysis = await Task.Run(() => AnalyzeFrame(anchor, anchorPosition, body, bodies, positions, _lifetime.Token), _lifetime.Token);
                        if (!CanInspectTarget()) return;
                    }
                    else { _userPaused = true; committed = true; return; }
                }
                if (analysis.Kind == FrameKind.Unreliable)
                {
                    _unmatchedRaw = raw;
                    _userPaused = true;
                    committed = true;
                    SetStatus("終點畫面尚未對齊，請回捲一點或先手動接合。", warning: true);
                    return;
                }
                position = analysis.Position;
            }
            var visibleBounds = new DrawingRectangle(_targetBounds.X, _targetBounds.Y + _topTrim, _targetBounds.Width, BodyHeight);
            var bodySnapshot = Crop(raw);
            _activePicker = new BoundaryPickWindow(visibleBounds, bodySnapshot, isStart,
                fromCollectedFrame: fromCollectedFrame) { Owner = this };
            bool confirmed = _activePicker.ShowDialog() == true;
            var pixel = _activePicker.SelectedPixel;
            _activePicker = null;
            // The picker displays a frozen, validated snapshot and never reads the desktop.
            if (_closed || !confirmed || pixel is null) return;

            if (isStart)
            {
                _frames.Clear(); _positions.Clear(); _addedAtTop.Clear();
                _anchorRaw = null; _anchorPosition = 0; _unmatchedRaw = null;
                _rangeStart = checked(_topTrim + pixel.Value);
                _rangeEnd = null;
                ResetObservation();
                AddFirstFrame(raw);
                _userPaused = false;
                _timer.Start();
                SetStatus("起點已選，捲動到最後一則訊息後選終點。");
            }
            else
            {
                var end = checked(position + _topTrim + pixel.Value);
                if (end <= _rangeStart!.Value)
                {
                    SetStatus("終點須在起點之後，請選取最後一則訊息的下緣。", warning: true);
                    return;
                }
                // Selecting a row within stored content must still work at the memory limit.
                // Extend the frontier only when the chosen end actually needs new rows.
                var storedEnd = checked(_positions[^1] + _targetBounds.Height - _bottomTrim);
                if (end > storedEnd && analysis is { Kind: FrameKind.New }
                    && !TryAcceptFrame(raw, position, analysis.AtTop))
                {
                    _userPaused = true; committed = true; return;
                }
                if (end > _positions[^1] + _targetBounds.Height - _bottomTrim)
                {
                    SetStatus("終點尚未收集，請回捲一點或先手動接合。", warning: true);
                    return;
                }
                if (position >= _positions[0] && position <= _positions[^1])
                {
                    _anchorRaw = raw; _anchorPosition = position;
                }
                else
                {
                    // A selected end can lie in stored rows of an uncommitted viewport.
                    // Keep the observer anchor within stored coverage when capture resumes.
                    _anchorRaw = _frames[^1]; _anchorPosition = _positions[^1];
                }
                _unmatchedRaw = null;
                _rangeEnd = end;
                _userPaused = true;
                ResetObservation();
                SetStatus("範圍已選，可產生長圖或重新選擇終點。");
            }
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
                    _userPaused = wasPaused;
                    if (timerWasRunning && _rangeStart is not null && _rangeEnd is null && !_userPaused) _timer.Start();
                }
                RefreshPreview(); UpdateDimensions(); UpdateButtons();
            }
        }
    }

    bool CanInspectTarget()
    {
        if (_closed || _finishing) return false;
        if (_target.CanObserve(_handle, _toolbarExcluded, out var reason, allowToolbarForeground: true)) return true;
        SetStatus(reason, warning: true); return false;
    }

    bool CanTrimRange(int top, int bottom)
    {
        if (_rangeStart is null || _frames.Count == 0) return true;
        int first = checked(_positions[0] + top);
        int end = checked(_positions[^1] + _targetBounds.Height - bottom);
        if (_rangeStart >= first && _rangeStart < end && (_rangeEnd is null || (_rangeEnd > _rangeStart && _rangeEnd <= end))) return true;
        SetStatus("裁切會排除已選起點或終點，請減少裁切或重新選起點。", warning: true);
        return false;
    }

    void InvalidateRangeAfterUndo()
    {
        _rangeEnd = null;
        if (_rangeStart is not null && _frames.Count > 0
            && (_rangeStart < _positions[0] + _topTrim || _rangeStart >= _positions[^1] + _targetBounds.Height - _bottomTrim))
            _rangeStart = null;
    }

    void UpdateRangeControls()
    {
        if (_startButton is null) return;
        bool idle = !_finishing && !_closed && !_pickingBoundary && _manualPanel.Visibility != Visibility.Visible;
        _startButton.IsEnabled = idle;
        _endButton.IsEnabled = idle && _rangeStart is not null;
        _startButton.Content = new TextBlock { Text = I18n.T(_rangeStart is null ? "選起點" : "重新選起點") };
        _endButton.Content = new TextBlock { Text = I18n.T(_rangeEnd is null ? "選終點" : "重新選終點") };
        _instruction.Text = I18n.T(_rangeStart is null ? "先捲到第一則訊息，再選起點。"
            : _rangeEnd is null ? "捲動到最後一則訊息，再選終點。" : "只保留起點到終點之間的內容。");
        _rangeSummary.Text = I18n.T(_rangeStart is null ? "尚未選起點" : _rangeEnd is null ? "起點已選 · 等待終點" : "起點與終點已選");
    }
}

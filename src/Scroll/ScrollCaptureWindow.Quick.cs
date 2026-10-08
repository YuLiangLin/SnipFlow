using System.Diagnostics;
using SnipFlow.Capture;
using SnipFlow.Services;

namespace SnipFlow.Scroll;

public sealed partial class ScrollCaptureWindow
{
    private TextBlock _finishNoticeText = null!;
    private Button _resolveFinishButton = null!;

    private bool NeedsStart => _workflow == ScrollCaptureWorkflow.Precise && _rangeStart is null;
    private bool HasFinishRange => _workflow != ScrollCaptureWorkflow.Precise
        || (_rangeStart is not null && _rangeEnd is not null);

    private StackPanel CreateFinishNotice()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        _finishNoticeText = new TextBlock
        {
            Foreground = Brush("#EAC88D"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8), LineHeight = 17
        };
        panel.Children.Add(_finishNoticeText);
        var actions = new WrapPanel();
        _resolveFinishButton = MakeButton("處理接縫");
        _resolveFinishButton.Click += (_, _) =>
        {
            HideFinishNotice();
            if (_unmatchedRaw is not null) BeginManualJoin();
            else
            {
                _rangeExpander.IsExpanded = true;
                SetStatus("新畫面超過上限，未加入；可微調範圍，或只完成已收集內容。", warning: true);
            }
        };
        var finishCollected = MakeButton("只完成已收集內容", accent: true);
        finishCollected.Click += async (_, _) => await FinishAsync(allowIncompleteTail: true);
        actions.Children.Add(_resolveFinishButton);
        actions.Children.Add(finishCollected);
        panel.Children.Add(actions);
        return panel;
    }

    private void HideFinishNotice() => _finishNotice.Visibility = Visibility.Collapsed;

    private void ShowFinishNotice()
    {
        _userPaused = true;
        _finishNoticeText.Text = I18n.T(_unmatchedRaw is not null
            ? "有畫面尚未對齊，不會加入長圖。請處理接縫，或只完成已收集內容。"
            : "已達上限，新畫面未加入。可微調範圍，或只完成已收集內容。");
        _resolveFinishButton.Content = new TextBlock { Text = I18n.T(_unmatchedRaw is not null ? "處理接縫" : "微調範圍") };
        _finishNotice.Visibility = Visibility.Visible;
        SetStatus("部分畫面尚未加入；請確認後完成。", warning: true);
    }

    private bool TryAcceptPendingTail()
    {
        if (_pendingAlignedRaw is null || _frames.Count == 0) return true;
        var raw = _pendingAlignedRaw;
        var position = _pendingAlignedPosition;
        if (position >= _positions[0] && position <= _positions[^1])
        {
            _pendingAlignedRaw = null;
            return true;
        }
        // This frame already passed AnalyzeFrame and the visibility checks when observed.
        // It can be committed while the target is inactive, without reading another window.
        if (TryAcceptFrame(raw, position, position < _positions[0])) return true;
        if (!_limitReached)
        {
            _unmatchedRaw = raw;
            _pendingAlignedRaw = null;
        }
        return false;
    }

    private bool CanInspectFinalTarget()
    {
        if (_closed) return false;
        return _target.CanObserve(_handle, _toolbarExcluded, out _, allowToolbarForeground: true);
    }

    private async Task CollectFinalViewportAsync()
    {
        if (!CanInspectFinalTarget()) return;
        var raw = ScreenshotService.CaptureRectangle(_targetBounds);
        if (!CanInspectFinalTarget()) return;
        var body = Crop(raw);
        var anchor = Crop(_anchorRaw ?? _frames[^1]);
        var anchorPosition = _anchorPosition;
        var bodies = _frames.Select(Crop).ToArray();
        var positions = _positions.ToArray();
        var analysis = await Task.Run(() => AnalyzeFrame(anchor, anchorPosition, body, bodies, positions, _lifetime.Token), _lifetime.Token);
        if (!CanInspectFinalTarget()) return;
        if (analysis.Kind == FrameKind.New)
            TryAcceptFrame(raw, analysis.Position, analysis.AtTop);
        else if (analysis.Kind == FrameKind.Known)
        {
            _anchorRaw = raw;
            _anchorPosition = analysis.Position;
            _unmatchedRaw = null;
        }
        else
        {
            _unmatchedRaw = raw;
            SetStatus(analysis.FixedEdge
                ? "固定列影響對齊；請在「微調範圍」略過頂部／底部。此畫面未加入。"
                : "最後畫面尚未對齊，未加入；請回捲一點或處理接縫。", warning: true);
        }
    }

    private async Task FinishAsync(bool allowIncompleteTail = false)
    {
        if (_finishing || _pickingBoundary || _closed || _frames.Count == 0 || !HasFinishRange
            || _manualPanel.Visibility == Visibility.Visible) return;
        var wasCollecting = !_userPaused && _rangeEnd is null;
        _finishing = true;
        _timer.Stop();
        HideFinishNotice();
        UpdateButtons();
        bool ownsBusy = false;
        try
        {
            // Mark finishing before awaiting: the observer drops any in-flight result,
            // then releases its snapshot without racing the final frame or range selection.
            while (_busy) await Task.Delay(30, _lifetime.Token);
            if (_closed) return;
            _busy = ownsBusy = true;
            if (_rangeEnd is null)
            {
                var pendingAccepted = TryAcceptPendingTail();
                if (pendingAccepted && wasCollecting && !_limitReached)
                    await CollectFinalViewportAsync();
                if (_closed) return;
                if ((_unmatchedRaw is not null || _limitReached) && !allowIncompleteTail)
                {
                    ShowFinishNotice();
                    return;
                }
            }
            SetStatus("正在產生長截圖…");
            var firstDocumentRow = checked(_positions[0] + _topTrim);
            var lastDocumentRow = checked(_positions[^1] + _targetBounds.Height - _bottomTrim);
            var firstRow = checked((_rangeStart ?? firstDocumentRow) - firstDocumentRow);
            var endRow = checked((_rangeEnd ?? lastDocumentRow) - firstDocumentRow);
            var frames = _frames.Select(Crop).ToArray();
            var overlaps = GetOverlaps(BodyHeight);
            var result = await Task.Run(() => ImageStitcher.StitchRange(frames, overlaps, firstRow, endRow), _lifetime.Token);
            if (_closed) return;
            Result = result;
            DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            SettingsStore.Log(exception);
            if (!_closed)
            {
                _userPaused = true;
                SetStatus(I18n.F("產生長截圖失敗，請重新截取：{0}", exception.Message), warning: true);
            }
        }
        finally
        {
            if (ownsBusy) _busy = false;
            _finishing = false;
            if (!_closed) { UpdateDimensions(); UpdateButtons(); }
        }
    }
}

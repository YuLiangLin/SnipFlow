using System.Diagnostics;
using SnipFlow.Capture;
using SnipFlow.Services;

namespace SnipFlow.Scroll;

/// <summary>
/// Toolbar placement and the live screen stream.
/// </summary>
/// <remarks>
/// Windows excludes the toolbar from capture (display affinity), so it may stay anywhere, as before.
/// Without exclusion (always on Linux) the toolbar must never be inside the captured region: it is
/// placed beside the region on the same screen, or on another screen when the region fills its
/// screen (the nearest free spot to the region's top-right corner wins). When the full panel fits
/// nowhere while frames are collected, it shrinks to a compact bar (title, status, Pause, Done, More,
/// Cancel) along a screen edge outside the region. "More" opens the full panel, which may then cover
/// the region, so collection pauses until the user resumes.
/// </remarks>
public sealed partial class ScrollCaptureWindow
{
    private Border _shell = null!;
    private StackPanel _layout = null!;
    private WrapPanel _actions = null!;
    private Button _cancelButton = null!;
    private StackPanel _headerActions = null!;
    private StackPanel _compactBar = null!;
    private TextBlock _compactStatus = null!;
    private Button _moreButton = null!;
    private int _actionsIndex = -1;
    private bool _compact;
    private bool _needsCompact;
    private bool _regionOnlyNoteShown;
    private IDisposable? _liveCapture;
    private bool _startingLiveCapture;
    private DateTime _settleUntil;

    /// <summary>True while the observer may capture the region (the toolbar must then be outside it).</summary>
    private bool IsCollecting => !_userPaused && !_finishing && !_pickingBoundary && !_manualPanel.IsVisible
        && _rangeEnd is null && !NeedsStart;

    private void CreateCompactBar(ScrollCaptureWorkflow workflow)
    {
        _compactBar = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock
        {
            Text = I18n.T(workflow == ScrollCaptureWorkflow.Code ? "程式碼長截圖" : "對話長截圖"),
            FontWeight = FontWeight.SemiBold, FontSize = 13, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0)
        };
        WindowChrome.EnableTitleBarDrag(this, title, allowMaximize: false);
        _compactStatus = new TextBlock
        {
            // A fixed width keeps the bar from resizing (and moving) with every status message.
            Width = 250, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        _compactBar.Children.Add(title);
        _compactBar.Children.Add(_compactStatus);
        _moreButton = MakeButton("更多");
        _moreButton.IsVisible = false;
        _moreButton.Click += (_, _) =>
        {
            if (_compact) ExpandToolbar(pauseIfCollecting: true);
            else CollapseToolbar();
        };
    }

    /// <summary>A first position from an estimated size, so the window manager maps it outside the region.</summary>
    private void PlaceBeforeShow()
    {
        var screen = Screens.ScreenFromBounds(_targetBounds) ?? Screens.Primary;
        var scaling = screen?.Scaling is > 0 and var value ? value : 1;
        var estimate = new PixelSize((int)Math.Ceiling(410 * scaling), (int)Math.Ceiling(300 * scaling));
        if (ScrollCaptureTarget.FindToolbarLocation(Screens.All, _targetBounds, estimate) is { } location)
            Position = location.Position;
        else if (screen is not null)
            Position = new PixelPoint(screen.WorkingArea.Right - estimate.Width - 12, screen.WorkingArea.Y + 12);
    }

    /// <param name="collecting">Whether frames are about to be captured; defaults to <see cref="IsCollecting"/>.</param>
    private void PlaceToolbar(bool? collecting = null)
    {
        if (_handle == 0 || _closed) return;
        if (_toolbarExcluded)
        {
            ScrollCaptureTarget.PlaceToolbar(this, _targetBounds);
            return;
        }
        if (ScrollCaptureTarget.FindToolbarLocation(Screens.All, _targetBounds, ScrollCaptureTarget.ToolbarSize(this)) is { } location)
        {
            if (Position != location.Position) Position = location.Position;
            return;
        }
        if (!_compact && (collecting ?? IsCollecting))
        {
            // The resize places the compact bar again (SizeChanged).
            _needsCompact = true;
            SetCompact(true);
            return;
        }
        if (_compact)
            // No screen edge has room even for the compact bar: the observer asks to move it away.
            ScrollCaptureTarget.PlaceToolbar(this, _targetBounds);
        else
            // The full panel while paused may cover the region; it only has to stay on the screen.
            ScrollCaptureTarget.KeepToolbarVisible(this);
    }

    /// <summary>After a size change: keep the toolbar on its screen and, where there is room, out of the region.</summary>
    private void KeepToolbarClear()
    {
        ScrollCaptureTarget.KeepToolbarVisible(this);
        if (!_toolbarExcluded && ToolbarCoversRegion()) PlaceToolbar();
    }

    /// <summary>
    /// Skips observations briefly after one of SnipFlow's own windows covered the region: a screen
    /// stream (Wayland) or a compositor's closing animation can still show it for a moment.
    /// </summary>
    private void DelayObservation() => _settleUntil = DateTime.UtcNow.AddMilliseconds(400);

    private bool ToolbarCoversRegion() =>
        IsVisible && CaptureGeometry.IntersectsWith(ScrollCaptureTarget.ToolbarBounds(this), _targetBounds);

    private void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        Detach(_actions);
        Detach(_cancelButton);
        Detach(_moreButton);
        if (compact)
        {
            _actions.VerticalAlignment = VerticalAlignment.Center;
            _compactBar.Children.Add(_actions);
            _compactBar.Children.Add(_moreButton);
            _compactBar.Children.Add(_cancelButton);
            _shell.Child = _compactBar;
            _shell.Padding = new Thickness(10, 6, 10, 6);
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = double.NaN;
        }
        else
        {
            _layout.Children.Insert(Math.Clamp(_actionsIndex, 0, _layout.Children.Count), _actions);
            _headerActions.Children.Add(_moreButton);
            _headerActions.Children.Add(_cancelButton);
            _shell.Child = _layout;
            _shell.Padding = new Thickness(14);
            Width = 410;
            SizeToContent = SizeToContent.Height;
        }
        UpdateCompactControls();
    }

    private void Detach(Control control)
    {
        if (control.Parent is Panel panel)
        {
            if (ReferenceEquals(control, _actions) && ReferenceEquals(panel, _layout))
                _actionsIndex = panel.Children.IndexOf(control);
            panel.Children.Remove(control);
        }
    }

    /// <summary>Shows the full panel. Without room beside the region it can cover the region, so collection pauses.</summary>
    private void ExpandToolbar(bool pauseIfCollecting = false)
    {
        if (!_compact) return;
        if (pauseIfCollecting && !_userPaused)
        {
            _userPaused = true;
            _timer.Stop();
            ResetObservation(discardPending: false);
            SetStatus("已暫停：完整操作面板會遮住截圖範圍。按「繼續」收合並繼續收集。", warning: true);
        }
        SetCompact(false);
        UpdateButtons();
    }

    private void CollapseToolbar()
    {
        if (_compact || _manualPanel.IsVisible || _finishNotice.IsVisible) return;
        SetCompact(true);
        UpdateButtons();
    }

    private void UpdateCompactControls()
    {
        if (_moreButton is null) return;
        _moreButton.IsVisible = _needsCompact && !_toolbarExcluded;
        _moreButton.Content = new TextBlock { Text = I18n.T(_compact ? "更多" : "收合") };
        ToolTip.SetTip(_moreButton, I18n.T(_compact ? "展開完整操作面板；畫面不夠時會暫停收集。" : "收合成精簡操作條，避開截圖範圍。"));
        _moreButton.IsEnabled = !_finishing && !_pickingBoundary
            && (_compact || (!_manualPanel.IsVisible && !_finishNotice.IsVisible));
    }

    /// <summary>Starts the observer once the toolbar is clear of the region and the live capture runs.</summary>
    private void StartTimer()
    {
        if (_closed) return;
        // A toolbar the user left beside the region stays where it is.
        if (!_toolbarExcluded && _handle != 0 && ToolbarCoversRegion())
            PlaceToolbar(collecting: true);
        if (IsLiveCaptureRunning())
        {
            _timer.Start();
            return;
        }
        _ = ResumeLiveCaptureAsync();
    }

    private async Task ResumeLiveCaptureAsync()
    {
        if (!await EnsureLiveCaptureAsync(initial: false) || _closed) return;
        if (IsCollecting) _timer.Start();
        UpdateButtons();
    }

    private bool IsLiveCaptureRunning() => _liveCapture is not null && IsLeaseRunning(_liveCapture);

    /// <summary>False when the platform's live capture stopped (Wayland: the user ended the screen share).</summary>
    private static partial bool IsLeaseRunning(IDisposable lease);

    /// <summary>
    /// Acquires the live capture lease (<see cref="ScreenshotService.StartLiveCaptureAsync"/>). In a Wayland
    /// session the desktop may ask which screen to share; cancelling it at the start cancels the capture,
    /// later it only keeps collection paused. Elsewhere the lease is ready immediately.
    /// </summary>
    private async Task<bool> EnsureLiveCaptureAsync(bool initial)
    {
        if (IsLiveCaptureRunning()) return true;
        if (_startingLiveCapture || _closed) return false;
        _startingLiveCapture = true;
        _liveCapture?.Dispose();
        _liveCapture = null;
        var previousStatus = _status.Text;
        var previousBrush = _status.Foreground;
        UpdateButtons();
        try
        {
            if (PlatformInfo.IsWayland) SetStatus("正在準備螢幕擷取；請在系統對話框中選擇要分享的螢幕。");
            var lease = await ScreenshotService.StartLiveCaptureAsync(_lifetime.Token);
            if (_closed)
            {
                lease?.Dispose();
                return false;
            }
            if (lease is null)
            {
                if (initial)
                {
                    Close();
                    return false;
                }
                _userPaused = true;
                _timer.Stop();
                SetStatus("已取消螢幕分享；已保留現有內容。", warning: true);
                return false;
            }
            _liveCapture = lease;
            if (PlatformInfo.IsWayland)
            {
                _status.Text = previousStatus;
                _status.Foreground = previousBrush;
                _compactStatus.Text = previousStatus;
                _compactStatus.Foreground = previousBrush;
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
            SettingsStore.Log(exception);
            if (!_closed)
            {
                _userPaused = true;
                _timer.Stop();
                SetStatus(I18n.F("擷取失敗，現有內容已保留：{0}", exception.Message), warning: true);
            }
            return false;
        }
        finally
        {
            _startingLiveCapture = false;
            if (!_closed) UpdateButtons();
        }
    }

    private void PauseForStoppedStream()
    {
        _userPaused = true;
        _timer.Stop();
        ResetObservation(discardPending: false);
        _liveCapture?.Dispose();
        _liveCapture = null;
        SetStatus("螢幕分享已停止，已保留現有內容；按「繼續」重新分享。", warning: true);
        UpdateButtons();
    }

    /// <summary>
    /// Captured frames hold native pixel memory (up to 60 MP); free them once no matcher, stitcher or
    /// picker still reads them. The caller's initial frame stays theirs.
    /// </summary>
    private async Task ReleaseFramesAsync(Bitmap? keep)
    {
        while (_busy) await Task.Delay(30);
        var bitmaps = new HashSet<Bitmap>(ReferenceEqualityComparer.Instance);
        bitmaps.UnionWith(_frames);
        foreach (var bitmap in new[] { _anchorRaw, _lastProcessedRaw, _unmatchedRaw, _pendingAlignedRaw })
            if (bitmap is not null) bitmaps.Add(bitmap);
        if (keep is not null) bitmaps.Remove(keep);
        if (Result is not null) bitmaps.Remove(Result);
        _frames.Clear();
        _positions.Clear();
        _anchorRaw = _lastProcessedRaw = _unmatchedRaw = _pendingAlignedRaw = null;
        _preview.Source = null;
        _joinPreview.Source = null;
        foreach (var bitmap in bitmaps)
            bitmap.Dispose();
    }

    /// <summary>Says once that only the region's pixels guard the collection (no window tracking).</summary>
    private void ShowRegionOnlyNote()
    {
        if (_regionOnlyNoteShown || !_target.IsRegionOnly) return;
        _regionOnlyNoteShown = true;
        _regionOnlyNote.Text = I18n.T(PlatformInfo.IsWayland
            ? "Wayland 無法追蹤目標視窗，只比對框選範圍的畫面；請讓範圍保持可見、不被其他視窗遮住。"
            : "範圍下方找不到視窗，只比對框選範圍的畫面；請讓範圍保持可見、不被其他視窗遮住。");
        _regionOnlyNote.IsVisible = true;
    }
}

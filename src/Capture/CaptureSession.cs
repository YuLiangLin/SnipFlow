using Avalonia.Input;

namespace SnipFlow.Capture;

internal sealed class CaptureSession : IDisposable
{
    // Activation is asynchronous on X11 and may be asynchronous through Avalonia on Windows.
    // Windows keeps the original rule (cancel when no overlay can take the foreground) after this
    // grace period; X11 window managers that never report focus keep the session open instead.
    private static readonly TimeSpan ActivationGrace = TimeSpan.FromSeconds(1);
    // Focus can move between two of the overlays (one per monitor) through a brief unfocused state.
    private static readonly TimeSpan DeactivationGrace = PlatformInfo.IsWindows ? TimeSpan.Zero : TimeSpan.FromMilliseconds(400);

    private readonly IReadOnlyList<CaptureMonitor> _monitors;
    private readonly PixelRect _desktopBounds;
    private readonly Bitmap _desktopImage;
    private readonly IReadOnlyList<CaptureWindow> _desktopWindows;
    private readonly WindowCaptureTarget? _restrictedTarget;
    private readonly List<SelectionOverlayWindow> _windows = new();
    private readonly TaskCompletionSource<PixelRect?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _cursorTimer;
    private SelectionOverlayWindow? _captureWindow;
    private IPointer? _capturePointer;
    private PixelPoint _origin;
    private DateTime _shownAt;
    private DateTime? _inactiveSince;
    private bool _closed;
    private bool _wasActive;
    private bool _clickSelecting;

    internal PixelPoint Cursor { get; private set; }
    internal bool IsSelecting { get; private set; }
    internal PixelRect? Selection { get; private set; }
    internal CaptureMode Mode { get; private set; }
    internal bool AllowModeSwitch { get; }
    internal bool SelectWindowOnly { get; }
    internal bool RestrictsToWindow => _restrictedTarget is not null;
    internal CaptureWindow? HoveredWindow { get; private set; }
    internal CaptureWindow? SelectedWindow { get; private set; }
    internal string? StatusHint { get; private set; }

    /// <summary>Wayland: the user chose window mode, which only the desktop's own picker can serve.</summary>
    internal bool UseSystemWindowPicker { get; private set; }

    /// <summary>Image pixels per desktop pixel (1 except for scaled XWayland desktops).</summary>
    internal Vector ImageScale { get; }
    internal PixelRect DesktopBounds => _desktopBounds;

    internal CaptureSession(IReadOnlyList<CaptureMonitor> monitors, PixelRect desktopBounds,
        Bitmap desktopImage, IReadOnlyList<CaptureWindow> desktopWindows, CaptureMode mode,
        bool allowModeSwitch, bool selectWindowOnly = false, WindowCaptureTarget? restrictedTarget = null,
        string? initialHint = null)
    {
        _monitors = monitors;
        _desktopBounds = desktopBounds;
        _desktopImage = desktopImage;
        _desktopWindows = desktopWindows;
        _restrictedTarget = restrictedTarget;
        ImageScale = new Vector(desktopImage.PixelSize.Width / (double)desktopBounds.Width,
            desktopImage.PixelSize.Height / (double)desktopBounds.Height);
        Selection = restrictedTarget?.ClientBounds;
        Mode = mode;
        AllowModeSwitch = allowModeSwitch;
        SelectWindowOnly = selectWindowOnly;
        StatusHint = initialHint;
        Cursor = CaptureNative.CursorPosition();
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _cursorTimer.Tick += OnCursorTick;
    }

    internal Task<PixelRect?> SelectAsync()
    {
        try
        {
            foreach (var monitor in _monitors)
                _windows.Add(new SelectionOverlayWindow(this, monitor, _desktopImage));

            foreach (var window in _windows)
                window.Show();

            var activeWindow = _windows.FirstOrDefault(window => CaptureGeometry.Contains(window.MonitorBounds, Cursor))
                ?? _windows.First();
            activeWindow.Activate();
            activeWindow.Focus();
            _wasActive = _windows.Any(window => window.IsActive);
            _shownAt = DateTime.UtcNow;
            RefreshSelection();
            _cursorTimer.Start();
        }
        catch
        {
            Dispose();
            throw;
        }

        return _completion.Task;
    }

    /// <param name="position">The event's physical position where the platform reports it exactly
    /// (Linux); null polls the cursor like the original Windows implementation.</param>
    internal void BeginSelection(SelectionOverlayWindow window, IPointer pointer, PixelPoint? position)
    {
        if (_closed || IsSelecting || _clickSelecting)
            return;

        try
        {
            Cursor = position ?? CaptureNative.CursorPosition();
            if (Mode != CaptureMode.Region)
            {
                RefreshSelection();
                _captureWindow = window;
                _clickSelecting = true;
                window.Activate();
                window.Focus();
                if (!CapturePointer(window, pointer))
                    Cancel();
                return;
            }
            if (_restrictedTarget is not null && !CaptureGeometry.Contains(_restrictedTarget.ClientBounds, Cursor))
            {
                StatusHint = I18n.T("請在選定視窗的內容區內框選。");
                RefreshSelection();
                return;
            }
            StatusHint = null;
            _origin = Clamp(Cursor);
            _captureWindow = window;
            IsSelecting = true;
            window.Activate();
            window.Focus();
            if (!CapturePointer(window, pointer))
            {
                Cancel();
                return;
            }
            RefreshSelection();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal void EndSelection(PixelPoint? position)
    {
        if (_closed || (!IsSelecting && !_clickSelecting))
            return;

        try
        {
            Cursor = position ?? CaptureNative.CursorPosition();
            RefreshSelection();
            if (_clickSelecting)
            {
                _clickSelecting = false;
                ReleasePointer();
                ConfirmChoice();
                return;
            }
            if (_restrictedTarget is not null && Selection is { } region)
            {
                var overlays = _windows.Select(window => window.Handle).Where(handle => handle != 0).ToArray();
                var reason = "";
                if (region.Width < 32 || region.Height < 48)
                    reason = "請選取至少 32 × 48 像素的捲動內容範圍。";
                else if (CaptureNative.CanCaptureWindowRegion(_restrictedTarget, region, _monitors,
                             overlays, out reason))
                {
                    Complete(region);
                    return;
                }
                IsSelecting = false;
                ReleasePointer();
                StatusHint = I18n.T(reason);
                RefreshSelection();
                return;
            }
            Complete(Selection);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal void Cancel() => Complete(null);

    internal void SwitchMode(CaptureMode mode)
    {
        if (_closed || !AllowModeSwitch || Mode == mode)
            return;
        try
        {
            IsSelecting = false;
            _clickSelecting = false;
            ReleasePointer();
            Mode = mode;
            Selection = null;
            HoveredWindow = null;
            SelectedWindow = null;
            StatusHint = null;
            Cursor = CaptureNative.CursorPosition();
            RefreshSelection();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal void ConfirmChoice()
    {
        if (_closed || _clickSelecting || Mode == CaptureMode.Region)
            return;
        try
        {
            if (Mode == CaptureMode.Window && PlatformInfo.IsWayland && !SelectWindowOnly)
            {
                // Other applications' windows are invisible to XWayland clients; the portal's
                // interactive screenshot lets the desktop itself pick a window.
                UseSystemWindowPicker = true;
                Complete(_desktopBounds);
                return;
            }
            if (Selection is not { } bounds)
                return;
            if (Mode == CaptureMode.Window)
            {
                if (!SelectWindowOnly && !WindowScreenshotService.IsSupported)
                {
                    StatusHint = I18n.T("目前不支援視窗擷取，請按 R 改用框選。");
                    RefreshSelection();
                    return;
                }
                if (HoveredWindow is not { } target || !CaptureNative.IsCurrentWindow(target))
                {
                    StatusHint = I18n.T(AllowModeSwitch
                        ? "視窗已改變，請選取其他視窗或按 R 框選。"
                        : "視窗已改變，請選取其他視窗。");
                    RefreshSelection();
                    return;
                }
                if (SelectWindowOnly && CaptureNative.IsCaptureProtected(target.Handle))
                {
                    StatusHint = I18n.T("此視窗禁止擷取，請選取其他視窗。");
                    RefreshSelection();
                    return;
                }
                SelectedWindow = target;
            }
            Complete(bounds);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal void CaptureLost()
    {
        if ((IsSelecting || _clickSelecting) && !_closed)
            Cancel();
    }

    /// <summary>Pointer movement over an overlay; the timer also polls when no events arrive.</summary>
    internal void PointerMoved(PixelPoint? position)
    {
        if (_closed)
            return;
        if (position is { } current && current != Cursor)
        {
            Cursor = current;
            RefreshSelection();
            return;
        }
        OnCursorTick(null, EventArgs.Empty);
    }

    private bool CapturePointer(SelectionOverlayWindow window, IPointer pointer)
    {
        _capturePointer = pointer;
        pointer.Capture(window.Surface);
        return pointer.Captured == window.Surface;
    }

    private void ReleasePointer()
    {
        var pointer = _capturePointer;
        _capturePointer = null;
        _captureWindow = null;
        if (pointer?.Captured is not null)
            pointer.Capture(null);
    }

    private void OnCursorTick(object? sender, EventArgs args)
    {
        if (_closed)
            return;

        try
        {
            if (_windows.Any(window => window.IsActive))
            {
                _wasActive = true;
                _inactiveSince = null;
            }
            else if (_wasActive)
            {
                var now = DateTime.UtcNow;
                _inactiveSince ??= now;
                if (now - _inactiveSince.Value >= DeactivationGrace)
                {
                    Cancel();
                    return;
                }
            }
            else if (PlatformInfo.IsWindows && DateTime.UtcNow - _shownAt > ActivationGrace)
            {
                Cancel();
                return;
            }

            var current = CaptureNative.CursorPosition();
            if (_restrictedTarget is not null && !CaptureNative.IsCurrentTarget(_restrictedTarget))
            {
                Cancel();
                return;
            }
            if (Cursor != current)
            {
                Cursor = current;
                RefreshSelection();
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void RefreshSelection()
    {
        if (Mode == CaptureMode.Region && IsSelecting)
        {
            var end = Clamp(Cursor);
            var selection = CaptureGeometry.FromLTRB(Math.Min(_origin.X, end.X),
                Math.Min(_origin.Y, end.Y), Math.Max(_origin.X, end.X), Math.Max(_origin.Y, end.Y));
            Selection = selection.Width > 0 && selection.Height > 0 ? selection : null;
        }
        else if (Mode == CaptureMode.Window)
        {
            var previous = HoveredWindow;
            var topWindow = _desktopWindows.FirstOrDefault(window => CaptureGeometry.Contains(window.Bounds, Cursor));
            HoveredWindow = topWindow?.CanSelect == true ? topWindow : null;
            Selection = HoveredWindow?.Bounds;
            if (previous?.Handle != HoveredWindow?.Handle)
                StatusHint = null;
        }
        else if (Mode == CaptureMode.Monitor)
        {
            Selection = _monitors.FirstOrDefault(monitor => CaptureGeometry.Contains(monitor.Bounds, Cursor))?.Bounds;
        }
        else if (Mode == CaptureMode.AllMonitors)
        {
            Selection = _desktopBounds;
        }
        foreach (var window in _windows)
            window.RefreshSelection();
    }

    private PixelPoint Clamp(PixelPoint point)
    {
        var bounds = _restrictedTarget?.ClientBounds ?? _desktopBounds;
        return new PixelPoint(Math.Clamp(point.X, bounds.X, bounds.Right),
            Math.Clamp(point.Y, bounds.Y, bounds.Bottom));
    }

    private void Complete(PixelRect? selection)
    {
        if (_closed)
            return;
        CloseWindows();
        _completion.TrySetResult(selection);
    }

    private void Fail(Exception exception)
    {
        if (_closed)
            return;
        CloseWindows();
        _completion.TrySetException(exception);
    }

    private void CloseWindows()
    {
        _closed = true;
        IsSelecting = false;
        _clickSelecting = false;
        _cursorTimer.Stop();
        _cursorTimer.Tick -= OnCursorTick;
        ReleasePointer();
        foreach (var window in _windows)
            window.CloseOverlay();
        _windows.Clear();
    }

    public void Dispose()
    {
        if (!_closed)
            Complete(null);
    }
}

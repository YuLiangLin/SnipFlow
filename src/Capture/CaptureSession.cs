using System.Windows.Input;
using System.Windows.Threading;
using SnipFlow.Services;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Capture;

internal sealed class CaptureSession : IDisposable
{
    private readonly IReadOnlyList<CaptureMonitor> _monitors;
    private readonly DrawingRectangle _desktopBounds;
    private readonly BitmapSource _desktopImage;
    private readonly IReadOnlyList<CaptureWindow> _desktopWindows;
    private readonly List<SelectionOverlayWindow> _windows = new();
    private readonly TaskCompletionSource<DrawingRectangle?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _cursorTimer;
    private SelectionOverlayWindow? _captureWindow;
    private DrawingPoint _origin;
    private bool _closed;
    private bool _wasActive;
    private bool _clickSelecting;

    internal DrawingPoint Cursor { get; private set; }
    internal bool IsSelecting { get; private set; }
    internal DrawingRectangle? Selection { get; private set; }
    internal CaptureMode Mode { get; private set; }
    internal bool AllowModeSwitch { get; }
    internal CaptureWindow? HoveredWindow { get; private set; }
    internal CaptureWindow? SelectedWindow { get; private set; }
    internal string? StatusHint { get; private set; }

    internal CaptureSession(IReadOnlyList<CaptureMonitor> monitors, DrawingRectangle desktopBounds,
        BitmapSource desktopImage, IReadOnlyList<CaptureWindow> desktopWindows, CaptureMode mode,
        bool allowModeSwitch)
    {
        _monitors = monitors;
        _desktopBounds = desktopBounds;
        _desktopImage = desktopImage;
        _desktopWindows = desktopWindows;
        Mode = mode;
        AllowModeSwitch = allowModeSwitch;
        Cursor = CaptureNative.CursorPosition();
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _cursorTimer.Tick += OnCursorTick;
    }

    internal Task<DrawingRectangle?> SelectAsync()
    {
        try
        {
            foreach (var monitor in _monitors)
            {
                var pixels = new Int32Rect(monitor.Bounds.X - _desktopBounds.X,
                    monitor.Bounds.Y - _desktopBounds.Y, monitor.Bounds.Width, monitor.Bounds.Height);
                var image = new CroppedBitmap(_desktopImage, pixels);
                image.Freeze();
                _windows.Add(new SelectionOverlayWindow(this, monitor, image));
            }

            foreach (var window in _windows)
                window.Show();

            var activeWindow = _windows.FirstOrDefault(window => window.MonitorBounds.Contains(Cursor))
                ?? _windows.First();
            _wasActive = activeWindow.Activate() || activeWindow.IsActive;
            activeWindow.Focus();
            if (!_wasActive && !_windows.Any(window => window.IsActive))
            {
                Cancel();
                return _completion.Task;
            }
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

    internal void BeginSelection(SelectionOverlayWindow window)
    {
        if (_closed || IsSelecting || _clickSelecting)
            return;

        try
        {
            Cursor = CaptureNative.CursorPosition();
            if (Mode != CaptureMode.Region)
            {
                RefreshSelection();
                _captureWindow = window;
                _clickSelecting = true;
                window.Activate();
                window.Focus();
                if (!window.CaptureMouse())
                    Cancel();
                return;
            }
            _origin = Clamp(Cursor);
            _captureWindow = window;
            IsSelecting = true;
            window.Activate();
            window.Focus();
            if (!window.CaptureMouse())
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

    internal void EndSelection()
    {
        if (_closed || (!IsSelecting && !_clickSelecting))
            return;

        try
        {
            Cursor = CaptureNative.CursorPosition();
            RefreshSelection();
            if (_clickSelecting)
            {
                _clickSelecting = false;
                if (_captureWindow?.IsMouseCaptured == true)
                    _captureWindow.ReleaseMouseCapture();
                _captureWindow = null;
                ConfirmChoice();
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
            if (_captureWindow?.IsMouseCaptured == true)
                _captureWindow.ReleaseMouseCapture();
            _captureWindow = null;
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
        if (_closed || _clickSelecting || Mode == CaptureMode.Region || Selection is not { } bounds)
            return;
        try
        {
            if (Mode == CaptureMode.Window)
            {
                if (!WindowScreenshotService.IsSupported)
                {
                    StatusHint = I18n.T("目前不支援視窗擷取，請按 R 改用框選。");
                    RefreshSelection();
                    return;
                }
                if (HoveredWindow is not { } target || !CaptureNative.IsCurrentWindow(target))
                {
                    StatusHint = I18n.T("視窗已改變，請選取其他視窗或按 R 框選。");
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

    private void OnCursorTick(object? sender, EventArgs args)
    {
        if (_closed)
            return;

        try
        {
            if (_windows.Any(window => window.IsActive))
                _wasActive = true;
            else if (_wasActive)
            {
                Cancel();
                return;
            }

            var current = CaptureNative.CursorPosition();
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
            var selection = DrawingRectangle.FromLTRB(Math.Min(_origin.X, end.X),
                Math.Min(_origin.Y, end.Y), Math.Max(_origin.X, end.X), Math.Max(_origin.Y, end.Y));
            Selection = selection.Width > 0 && selection.Height > 0 ? selection : null;
        }
        else if (Mode == CaptureMode.Window)
        {
            var previous = HoveredWindow;
            var topWindow = _desktopWindows.FirstOrDefault(window => window.Bounds.Contains(Cursor));
            HoveredWindow = topWindow?.CanSelect == true ? topWindow : null;
            Selection = HoveredWindow?.Bounds;
            if (previous?.Handle != HoveredWindow?.Handle)
                StatusHint = null;
        }
        else if (Mode == CaptureMode.Monitor)
        {
            Selection = _monitors.FirstOrDefault(monitor => monitor.Bounds.Contains(Cursor))?.Bounds;
        }
        else if (Mode == CaptureMode.AllMonitors)
        {
            Selection = _desktopBounds;
        }
        foreach (var window in _windows)
            window.RefreshSelection();
    }

    private DrawingPoint Clamp(DrawingPoint point) => new(
        Math.Clamp(point.X, _desktopBounds.Left, _desktopBounds.Right),
        Math.Clamp(point.Y, _desktopBounds.Top, _desktopBounds.Bottom));

    private void Complete(DrawingRectangle? selection)
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
        if (_captureWindow?.IsMouseCaptured == true)
            _captureWindow.ReleaseMouseCapture();
        _captureWindow = null;
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

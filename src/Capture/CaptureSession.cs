using System.Windows.Input;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipFlow.Capture;

internal sealed class CaptureSession : IDisposable
{
    private readonly IReadOnlyList<CaptureMonitor> _monitors;
    private readonly DrawingRectangle _desktopBounds;
    private readonly BitmapSource _desktopImage;
    private readonly List<SelectionOverlayWindow> _windows = new();
    private readonly TaskCompletionSource<DrawingRectangle?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _cursorTimer;
    private SelectionOverlayWindow? _captureWindow;
    private DrawingPoint _origin;
    private bool _closed;
    private bool _wasActive;

    internal DrawingPoint Cursor { get; private set; }
    internal bool IsSelecting { get; private set; }
    internal DrawingRectangle? Selection { get; private set; }

    internal CaptureSession(IReadOnlyList<CaptureMonitor> monitors, DrawingRectangle desktopBounds,
        BitmapSource desktopImage)
    {
        _monitors = monitors;
        _desktopBounds = desktopBounds;
        _desktopImage = desktopImage;
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
            activeWindow.Activate();
            activeWindow.Focus();
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
        if (_closed || IsSelecting)
            return;

        try
        {
            Cursor = CaptureNative.CursorPosition();
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
        if (_closed || !IsSelecting)
            return;

        try
        {
            Cursor = CaptureNative.CursorPosition();
            RefreshSelection();
            Complete(Selection);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal void Cancel() => Complete(null);

    internal void CaptureLost()
    {
        if (IsSelecting && !_closed)
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
        if (IsSelecting)
        {
            var end = Clamp(Cursor);
            var selection = DrawingRectangle.FromLTRB(Math.Min(_origin.X, end.X),
                Math.Min(_origin.Y, end.Y), Math.Max(_origin.X, end.X), Math.Max(_origin.Y, end.Y));
            Selection = selection.Width > 0 && selection.Height > 0 ? selection : null;
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

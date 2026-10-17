using SnipFlow.Capture;

namespace SnipFlow.Recording;

/// <summary>A physical-pixel recording border. All members are used on the owner's UI dispatcher.</summary>
/// <remarks>
/// Windows draws the border on the inner edge of the recorded rectangle with windows that are excluded from
/// capture (WDA_EXCLUDEFROMCAPTURE, <c>RecordingRegionHighlight.Windows.cs</c>). Linux has no capture exclusion,
/// so the border is drawn just outside the rectangle with click-through X11 windows
/// (<c>RecordingRegionHighlight.Linux.cs</c>); there <see cref="IsExcludedFromCapture"/> means that every border
/// window is shown where it belongs, outside the recorded pixels. Each platform part provides
/// <c>AllowsEmptyBorder</c>, <c>CanShowBorder</c>, <c>IsCompositionEnabled</c>, <c>BorderPieces</c>,
/// <c>KeepOnTop</c>, <c>ReleasePlatform</c> and the <c>BorderWindow</c> class.
/// </remarks>
internal sealed partial class RecordingRegionHighlight : IDisposable
{
    /// <summary>Border thickness in physical pixels.</summary>
    internal const int Thickness = 2;
    private static readonly Color ReadyColor = Color.FromRgb(96, 213, 196);
    private static readonly Color RecordingColor = Color.FromRgb(255, 92, 104);
    private readonly PixelRect _bounds;
    private readonly DispatcherTimer _verificationTimer;
    private readonly List<BorderWindow> _windows = new();
    private Window? _owner;
    private PixelRect[] _monitorBounds = Array.Empty<PixelRect>();
    private bool _shown;
    private bool _recording;
    private bool _invalid;
    private bool _invalidationRaised;
    private bool _disposed;
    private int _generation;

    internal event EventHandler? Invalidated;

    internal RecordingRegionHighlight(PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > int.MaxValue
            || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        _bounds = bounds;
        _verificationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _verificationTimer.Tick += VerifyOverlays;
    }

    /// <summary>The rectangle the border marks, in physical desktop pixels.</summary>
    internal PixelRect Bounds => _bounds;

    internal bool IsExcludedFromCapture
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_disposed || !_shown || _invalid || (_windows.Count == 0 && !AllowsEmptyBorder))
                return false;
            try
            {
                if (_monitorBounds.SequenceEqual(GetMonitorBounds())
                    && IsCompositionEnabled() && _windows.All(window => window.IsVerified))
                    return true;
                Invalidate();
            }
            catch (Exception exception) { Invalidate(exception); }
            return false;
        }
    }

    internal void Show(Window owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.TryGetPlatformHandle() is null)
            throw new InvalidOperationException("The recording owner must have an initialized window on this dispatcher.");
        Dispatcher.UIThread.ShutdownStarted -= OnDispatcherShutdown;
        Dispatcher.UIThread.ShutdownStarted += OnDispatcherShutdown;
        if (_shown && ReferenceEquals(_owner, owner) && IsExcludedFromCapture)
            return;

        Hide();
        if (_owner is not null)
            _owner.Closed -= OnOwnerClosed;
        _owner = owner;
        _owner.Closed += OnOwnerClosed;
        _invalid = false;
        _invalidationRaised = false;
        try
        {
            // Windows: older versions treat 0x11 as WDA_MONITOR, which could leave a black border in video.
            if (!CanShowBorder())
                return;
            _monitorBounds = GetMonitorBounds();
            foreach (var monitor in _monitorBounds)
            {
                foreach (var piece in BorderPieces(monitor))
                {
                    var overlay = new BorderWindow(this, owner, piece);
                    _windows.Add(overlay);
                    // Create and verify every window while hidden, before any border is displayed.
                    overlay.Prepare();
                }
            }
            if (_windows.Count == 0 && !AllowsEmptyBorder)
                return;
            foreach (var overlay in _windows)
                overlay.ShowPrepared();
            if (_invalid || !_monitorBounds.SequenceEqual(GetMonitorBounds())
                || !IsCompositionEnabled() || !_windows.All(window => window.IsVerified))
                throw new InvalidOperationException("The recording border could not be excluded from capture or positioned correctly.");
            _shown = true;
            _verificationTimer.Start();
        }
        catch (Exception exception)
        {
            LogSafely(exception);
            _invalid = true;
            CloseOverlays();
        }
    }

    internal void Hide()
    {
        Dispatcher.UIThread.VerifyAccess();
        _generation++;
        _shown = false;
        _verificationTimer.Stop();
        CloseOverlays();
    }

    internal void SetRecording(bool recording)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _recording == recording)
            return;
        _recording = recording;
        foreach (var overlay in _windows)
            overlay.Refresh();
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        _generation++;
        _shown = false;
        _verificationTimer.Stop();
        _verificationTimer.Tick -= VerifyOverlays;
        Dispatcher.UIThread.ShutdownStarted -= OnDispatcherShutdown;
        if (_owner is not null)
            _owner.Closed -= OnOwnerClosed;
        _owner = null;
        CloseOverlays();
        ReleasePlatform();
    }

    /// <summary>The inner edges of the recorded rectangle (Windows draws its excluded border there).</summary>
    private IEnumerable<PixelRect> Edges()
    {
        var horizontal = Math.Min(Thickness, _bounds.Height);
        var vertical = Math.Min(Thickness, _bounds.Width);
        yield return new(_bounds.X, _bounds.Y, _bounds.Width, horizontal);
        yield return new(_bounds.X, _bounds.Bottom - horizontal, _bounds.Width, horizontal);
        yield return new(_bounds.X, _bounds.Y, vertical, _bounds.Height);
        yield return new(_bounds.Right - vertical, _bounds.Y, vertical, _bounds.Height);
    }

    /// <summary>The edges just outside the recorded rectangle (Linux draws its border there).</summary>
    private IEnumerable<PixelRect> OuterEdges()
    {
        yield return new(_bounds.X - Thickness, _bounds.Y - Thickness, _bounds.Width + 2 * Thickness, Thickness);
        yield return new(_bounds.X - Thickness, _bounds.Bottom, _bounds.Width + 2 * Thickness, Thickness);
        yield return new(_bounds.X - Thickness, _bounds.Y, Thickness, _bounds.Height);
        yield return new(_bounds.Right, _bounds.Y, Thickness, _bounds.Height);
    }

    private static PixelRect[] GetMonitorBounds() => CaptureNative.GetMonitors()
        .Select(monitor => monitor.Bounds).Distinct().OrderBy(bounds => bounds.X).ThenBy(bounds => bounds.Y)
        .ThenBy(bounds => bounds.Width).ThenBy(bounds => bounds.Height).ToArray();

    private void OnOwnerClosed(object? sender, EventArgs args) => Dispose();
    private void OnDispatcherShutdown(object? sender, EventArgs args) => Dispose();

    private void VerifyOverlays(object? sender, EventArgs args)
    {
        if (IsExcludedFromCapture)
            KeepOnTop();
    }

    private void Invalidate(Exception? exception = null)
    {
        if (exception is not null)
            LogSafely(exception);
        if (_disposed || _invalid)
            return;
        _invalid = true;
        _verificationTimer.Stop();
        var notify = _shown;
        _shown = false;
        var generation = _generation;
        // Native hooks must return before closing their windows or notifying recording code.
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || generation != _generation)
                    return;
                CloseOverlays();
                if (!notify || _disposed || _invalidationRaised)
                    return;
                _invalidationRaised = true;
                if (Invalidated is { } handlers)
                    foreach (EventHandler handler in handlers.GetInvocationList())
                        try { handler(this, EventArgs.Empty); }
                        catch (Exception error) { LogSafely(error); }
            }, DispatcherPriority.Send);
        }
        catch (Exception error) { LogSafely(error); }
    }

    private void CloseOverlays()
    {
        var windows = _windows.ToArray();
        _windows.Clear();
        foreach (var overlay in windows)
            overlay.CloseOverlay();
    }

    private static void LogSafely(Exception exception)
    {
        try { SettingsStore.Log(exception); }
        catch (Exception) { } // Logging must not escape a native window hook or prevent cleanup.
    }
}

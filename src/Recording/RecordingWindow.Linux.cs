using SnipFlow.Capture;

namespace SnipFlow.Recording;

/// <summary>
/// Linux: nothing can be excluded from capture, so the toolbar is placed outside the recorded rectangle and
/// its border: the top-right corner of the work area like on Windows when that is free, otherwise another
/// corner, beside the rectangle, or another monitor. When the rectangle leaves no room (a whole single
/// screen), the toolbar stays at the usual corner and the status says it will appear in the video.
/// </summary>
public sealed partial class RecordingWindow
{
    private const int EdgeMargin = 16;
    private const int BorderGap = 8;
    private const double EstimatedHeight = 270;
    private bool _toolbarInRecording;
    private bool _placementHooked;
    private bool _placementWarningShown;
    private bool _userMoved;
    private int _placementCorrections;

    /// <summary>The recorded rectangle with its outside border and a small gap, in physical pixels.</summary>
    private PixelRect KeepOutArea
    {
        get
        {
            var inset = RecordingRegionHighlight.Thickness + BorderGap;
            return new PixelRect(_bounds.X - inset, _bounds.Y - inset, _bounds.Width + 2 * inset, _bounds.Height + 2 * inset);
        }
    }

    partial void ConfigureToolbarWindow()
    {
        PlaceOutsideRecording();
        if (!_placementHooked)
        {
            _placementHooked = true;
            // The height follows the content (SizeToContent): a longer status can grow the toolbar into the
            // recording, and the user can drag it there.
            Resized += (_, _) =>
            {
                if (!_toolbarInRecording && !_userMoved && OverlapsRecording())
                    PlaceOutsideRecording();
            };
            // Some window managers place new windows themselves (centred, tiled); move the toolbar back out of
            // the recording a few times, but never undo the user's own drag.
            PositionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!_toolbarInRecording && !_userMoved && _placementCorrections < 3 && OverlapsRecording())
                {
                    _placementCorrections++;
                    PlaceOutsideRecording();
                }
                CheckToolbarPlacement(started: false);
            }, DispatcherPriority.Background);
        }
        // Linux has no capture exclusion; the toolbar is ready once it is placed outside the recording.
        _excludedFromCapture = true;
        SetBusy(_busy);
    }

    partial void OnUserMove() => _userMoved = true;

    partial void CheckToolbarPlacement(bool started)
    {
        if (_closingRequested || _allowClose || _savePending || !_loaded)
            return;
        if (!OverlapsRecording())
        {
            _placementWarningShown = false;
            return;
        }
        if (_placementWarningShown && !started)
            return;
        _placementWarningShown = true;
        var warning = _toolbarInRecording
            ? T("錄影範圍周圍沒有空間放置工具列，工具列會出現在影片中。")
            : T("工具列位於錄影範圍內，會被錄進影片。");
        SetStatus(started ? warning + " " + _status.Text : warning, error: true);
    }

    private PixelSize ToolbarPixelSize()
    {
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        // When the window opens its bounds may still have the default size; the measured content is the
        // height SizeToContent applies. Before the first layout, use an estimate.
        var desired = (Content as Control)?.DesiredSize ?? default;
        var width = Width > 0 ? Width : desired.Width; // Fixed width; only the height follows the content.
        var height = desired.Height > 0 ? desired.Height : Bounds.Height > 0 ? Bounds.Height : EstimatedHeight;
        return new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
    }

    private bool OverlapsRecording() =>
        CaptureGeometry.IntersectsWith(new PixelRect(Position, ToolbarPixelSize()), KeepOutArea);

    private void PlaceOutsideRecording()
    {
        var size = ToolbarPixelSize();
        var keepOut = KeepOutArea;
        var screens = Screens.All;
        var home = Screens.ScreenFromBounds(_bounds) ?? Screens.Primary ?? screens.FirstOrDefault();
        var candidates = new List<PixelPoint>();
        void AddCorners(PixelRect area)
        {
            candidates.Add(new PixelPoint(area.Right - size.Width - EdgeMargin, area.Y + EdgeMargin));
            candidates.Add(new PixelPoint(area.X + EdgeMargin, area.Y + EdgeMargin));
            candidates.Add(new PixelPoint(area.Right - size.Width - EdgeMargin, area.Bottom - size.Height - EdgeMargin));
            candidates.Add(new PixelPoint(area.X + EdgeMargin, area.Bottom - size.Height - EdgeMargin));
        }
        if (home is not null)
        {
            var area = home.WorkingArea;
            AddCorners(area);
            int ClampX(int x) => Math.Clamp(x, area.X, Math.Max(area.X, area.Right - size.Width));
            int ClampY(int y) => Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
            // Beside the recording: right, left, below, above.
            candidates.Add(new PixelPoint(keepOut.Right, ClampY(_bounds.Y)));
            candidates.Add(new PixelPoint(keepOut.X - size.Width, ClampY(_bounds.Y)));
            candidates.Add(new PixelPoint(ClampX(_bounds.Right - size.Width), keepOut.Bottom));
            candidates.Add(new PixelPoint(ClampX(_bounds.Right - size.Width), keepOut.Y - size.Height));
        }
        foreach (var screen in screens.Where(screen => home is null || screen.Bounds != home.Bounds))
            AddCorners(screen.WorkingArea);
        foreach (var point in candidates)
        {
            var toolbar = new PixelRect(point, size);
            if (!CaptureGeometry.IntersectsWith(toolbar, keepOut)
                && screens.Any(screen => CaptureGeometry.Contains(screen.WorkingArea, toolbar)))
            {
                _toolbarInRecording = false;
                Position = point;
                return;
            }
        }
        _toolbarInRecording = true;
        if (candidates.Count > 0)
            Position = candidates[0];
    }
}

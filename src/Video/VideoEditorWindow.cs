using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Input;
using SnipFlow.Services;

namespace SnipFlow.Video;

/// <summary>
/// Trims a video and places timed text and image overlays. Shown modally with
/// <see cref="DialogWindow.ShowModal"/>; <see cref="ExportedPaths"/> lists the MP4s written meanwhile.
/// </summary>
public sealed partial class VideoEditorWindow : DialogWindow
{
    readonly DispatcherTimer playbackTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    readonly CancellationTokenSource lifetime = new();
    readonly List<string> exportedPaths = [];
    VideoEditDocument document;
    VideoPreviewPlayer? player;
    VideoPreviewPlayer.PreviewError? playerError;
    CancellationTokenSource? operationCancellation, posterCancellation;
    string? lastExportedPath, activeOperationKey;
    TimeSpan position;
    bool playing, mediaReady, busy, synchronizing, opened, closed, pendingClose, allowClose, savingBeforeClose,
        promptingClose, exportingOperation;
    string? statusKey;
    object?[] statusValues = [];
    bool statusError;

    public event Action<string>? Exported;
    public string? ExportedPath => lastExportedPath;
    public IReadOnlyList<string> ExportedPaths => exportedPaths.AsReadOnly();

    public static async Task<VideoEditorWindow> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = await VideoProjectStore.OpenAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new VideoEditorWindow(document);
    }

    VideoEditorWindow(VideoEditDocument value)
    {
        document = value; document.Validate();
        Title = I18n.T("SnipFlow · 影片編輯");
        Width = 1240; Height = 820; MinWidth = 980; MinHeight = 660;
        WindowDecorations = WindowDecorations.None; CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        if (Application.Current?.TryGetResource("SnipFlowIcon", null, out var icon) == true && icon is WindowIcon windowIcon)
            Icon = windowIcon;
        WindowChrome.EnableResizeBorder(this);
        WindowBoundsService.Attach(this);
        WindowAppearance.Apply(this);
        BuildLayout(); AttachHandlers();
        player = VideoPreviewPlayer.TryCreate(frameView, out playerError);
        AttachPlayer();
        try { BindDocument(value); }
        catch
        {
            player?.Dispose(); lifetime.Dispose(); throw;
        }
        Opened += (_, _) =>
        {
            opened = true;
            FitWorkArea();
            OpenPreview(); playbackTimer.Start(); UpdatePreviewScale();
        };
        PositionChanged += (_, _) => { if (opened && !closed) FitWorkArea(); };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            closed = true; playing = false; playbackTimer.Stop();
            posterCancellation?.Cancel();
            player?.Dispose(); player = null;
            document.PropertyChanged -= DocumentChanged;
            document.Overlays.CollectionChanged -= OverlaysChanged;
            I18n.Changed -= LanguageChanged;
            lifetime.Cancel(); lifetime.Dispose();
        };
        I18n.Changed += LanguageChanged;
        ApplyLanguage();
        if (playerError is not null) ShowStatus(playerError.Key, true, playerError.Detail is null ? [] : [playerError.Detail]);
        else ShowStatus("匯出會保留原影片的音訊。");
    }

    void AttachPlayer()
    {
        if (player is null)
        {
            // Without libvlc the editor keeps working; ffmpeg stills follow the playhead instead.
            previewNotice.IsVisible = true;
            RefreshPreviewNotice();
            return;
        }
        player.Opened += () =>
        {
            if (closed || mediaReady) return;
            mediaReady = true; SetPosition(document.TrimStart); RefreshActions();
        };
        player.Ended += () => { if (!closed) { PausePlayback(); SetPosition(document.TrimStart); } };
        player.Failed += message =>
        {
            if (closed) return;
            PausePlayback(); mediaReady = false; RefreshActions();
            ShowStatus("影片預覽失敗：{0}", true, message);
            RequestPoster(position);
        };
    }

    void RefreshPreviewNotice()
    {
        if (playerError is null) return;
        previewNoticeText.Text = playerError.Text
            + (VideoPosterFrame.IsAvailable ? "" : "\n" + I18n.T("預覽畫面需要 ffmpeg。請安裝：sudo apt install ffmpeg"));
    }

    void AttachHandlers()
    {
        playbackTimer.Tick += (_, _) =>
        {
            if (!playing || !mediaReady || closed || player is null) return;
            var current = player.Time;
            if (current >= document.TrimEnd) { PausePlayback(); SetPosition(document.TrimStart); return; }
            position = current < document.TrimStart ? document.TrimStart : current;
            RefreshTimeline(); overlayCanvas.SetPlaybackPosition(position, false);
        };
        seek.ValueChanged += (_, _) =>
        {
            if (synchronizing || busy) return;
            PausePlayback(); SetPosition(TimeSpan.FromSeconds(seek.Value));
        };
        trimRange.SeekRequested += seconds => { if (!busy) { PausePlayback(); SetPosition(TimeSpan.FromSeconds(seconds)); } };
        trimRange.RangeChanged += (start, end) =>
        {
            if (busy || synchronizing) return;
            ApplyTrim(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));
        };
        AttachField(trimStart, () => CommitTrimField(true));
        AttachField(trimEnd, () => CommitTrimField(false));
        AttachField(x, () => CommitNumber(x, "X"));
        AttachField(y, () => CommitNumber(y, "Y"));
        AttachField(objectWidth, () => CommitNumber(objectWidth, "Width"));
        AttachField(objectHeight, () => CommitNumber(objectHeight, "Height"));
        AttachField(fontSize, () => CommitNumber(fontSize, "FontSize"));
        AttachField(color, CommitColor);
        AttachField(objectStart, () => CommitObjectTime(true));
        AttachField(objectEnd, () => CommitObjectTime(false));
        entireVideo.IsCheckedChanged += (_, _) => SetEntireVideo(entireVideo.IsChecked == true);
        objectList.SelectionChanged += (_, _) =>
        {
            if (synchronizing || busy) return;
            PausePlayback();
            var overlay = (objectList.SelectedItem as ListBoxItem)?.Tag as VideoOverlay;
            overlayCanvas.Select(overlay);
            if (overlay is not null) SeekSelectionIntoView(overlay);
        };
        overlayCanvas.SelectionChanged += _ => RefreshInspector();
        overlayCanvas.OverlayEdited += () =>
        {
            RefreshInspector(); RefreshObjectList(); RefreshTitle();
        };
        previewHost.SizeChanged += (_, _) => UpdatePreviewScale();
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
    }

    void BindDocument(VideoEditDocument value)
    {
        PausePlayback();
        // Prepare the new visuals before releasing the old source or document subscriptions.
        overlayCanvas.SetDocument(value);
        mediaReady = false; player?.Close(); frameView.Clear();
        document.PropertyChanged -= DocumentChanged;
        document.Overlays.CollectionChanged -= OverlaysChanged;
        document = value;
        document.PropertyChanged += DocumentChanged;
        document.Overlays.CollectionChanged += OverlaysChanged;
        position = document.TrimStart;
        sourceSurface.Width = document.Source.Width; sourceSurface.Height = document.Source.Height;
        frameView.Width = document.Source.Width; frameView.Height = document.Source.Height;
        overlayCanvas.SetPlaybackPosition(position, !busy);
        RefreshObjectList(); RefreshInspector(); RefreshTimeline(); RefreshTitle(); RefreshActions();
        UpdatePreviewScale();
        if (opened) OpenPreview();
    }

    void OpenPreview()
    {
        if (player is null) { RequestPoster(position); return; }
        // Loads the user's selected media paused, without autoplay or an initial audio burst.
        try { player.Open(document.Source.SourcePath, document.Source.Width, document.Source.Height); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SettingsStore.Log(exception);
            ShowStatus("影片預覽失敗：{0}", true, exception.Message);
            RequestPoster(position);
        }
    }

    /// <summary>
    /// Shows the exact frame at <paramref name="time"/> from ffmpeg: as the only preview when libvlc is
    /// unavailable, and after a paused seek, because libvlc 3 can stop up to three frames early in
    /// videos with B-frames.
    /// </summary>
    void RequestPoster(TimeSpan time)
    {
        posterCancellation?.Cancel();
        if (closed || playing || !VideoPosterFrame.IsAvailable) return;
        var cancellation = posterCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var source = document.Source;
        var refine = player is not null && mediaReady;
        _ = Task.Run(async () =>
        {
            try
            {
                // Coalesce scrubbing into one decode per pause in pointer movement.
                await Task.Delay(refine ? 250 : 120, cancellation.Token);
                var frame = await VideoPosterFrame.ExtractAsync(source, time, cancellation.Token);
                if (frame is null) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!closed && !cancellation.IsCancellationRequested && ReferenceEquals(source, document.Source)
                        && !playing && position == time) frameView.ShowStill(frame);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { SettingsStore.Log(exception); }
        });
    }

    void FitWorkArea()
    {
        var area = WindowBoundsService.GetWorkArea(this);
        MinWidth = Math.Min(980, area.Width); MinHeight = Math.Min(660, area.Height);
        Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height);
    }

    bool SeekSelectionIntoView(VideoOverlay overlay)
    {
        var start = overlay.ShowEntireVideo ? document.TrimStart : Max(document.TrimStart, overlay.Start);
        var end = overlay.ShowEntireVideo ? document.TrimEnd : Min(document.TrimEnd, overlay.End);
        if (end <= start)
        {
            ShowStatus("這個物件不在保留片段內，請調整顯示時間。", true); return false;
        }
        if (position < start || position >= end) SetPosition(start);
        return true;
    }

    void DocumentChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(() => DocumentChanged(sender, args)); return; }
        if (closed || sender != document) return;
        RefreshTitle();
        if (args.PropertyName is nameof(VideoEditDocument.TrimStart) or nameof(VideoEditDocument.TrimEnd))
            RefreshTimeline();
    }

    void OverlaysChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(() => OverlaysChanged(sender, args)); return; }
        if (closed) return;
        overlayCanvas.Refresh(); RefreshObjectList(); RefreshInspector(); RefreshActions();
    }

    void TogglePlayback()
    {
        if (busy || !mediaReady || player is null) return;
        if (playing) { PausePlayback(); return; }
        overlayCanvas.EndTextEditing(true);
        if (position < document.TrimStart || position >= document.TrimEnd) SetPosition(document.TrimStart);
        posterCancellation?.Cancel(); frameView.ReleaseStill();
        playing = true; player.Play(); overlayCanvas.SetPlaybackPosition(position, false); RefreshActions();
    }

    void PausePlayback()
    {
        // Settle the picture on the playhead the editor shows; libvlc's own clock lags by up to 250 ms.
        var wasPlaying = playing;
        playing = false;
        if (mediaReady) player?.Pause(wasPlaying ? position : null);
        if (wasPlaying) RequestPoster(position);
        if (!closed) { overlayCanvas.SetPlaybackPosition(position, !busy); RefreshActions(); }
    }

    void SetPosition(TimeSpan requested)
    {
        var last = document.TrimEnd - TimeSpan.FromTicks(1);
        position = Min(Max(requested, document.TrimStart), last);
        if (mediaReady && player is not null) { frameView.ReleaseStill(); player.Seek(position); }
        RequestPoster(position);
        overlayCanvas.SetPlaybackPosition(position, !playing && !busy);
        RefreshTimeline();
    }

    void ApplyTrim(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end > document.Source.Duration || end - start < TimeSpan.FromMilliseconds(100))
        { ShowStatus("保留範圍至少需要 0.1 秒，且必須在影片內。", true); RefreshTimeline(); return; }
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        document.TrimStart = start; document.TrimEnd = end;
        SetPosition(position);
        if (overlayCanvas.Selected is { } selected) SeekSelectionIntoView(selected);
        overlayCanvas.Refresh(); RefreshInspector();
    }

    void SetTrimAtPosition(bool start) => ApplyTrim(start ? position : document.TrimStart, start ? document.TrimEnd : position);
    void ResetTrim() => ApplyTrim(TimeSpan.Zero, document.Source.Duration);

    void CommitTrimField(bool start)
    {
        if (synchronizing || busy) return;
        if (!TryTime((start ? trimStart : trimEnd).Text ?? "", out var value))
        { ShowStatus("請輸入秒數或 分:秒.毫秒。", true); RefreshTimeline(); return; }
        ApplyTrim(start ? value : document.TrimStart, start ? document.TrimEnd : value);
    }

    void AddText()
    {
        if (busy || !CanAddOverlay()) return;
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        var width = Math.Min(600, document.Source.Width * .6);
        var font = Math.Clamp(document.Source.Height / 24d, 8, 64);
        var height = Math.Min(document.Source.Height * .4, font * 3);
        var overlay = new VideoOverlay
        {
            Kind = VideoOverlayKind.Text, Text = I18n.T("文字"), FontSize = font, ColorHex = "#FFFFFF",
            X = (document.Source.Width - width) / 2, Y = (document.Source.Height - height) / 2,
            Width = Math.Max(1, width), Height = Math.Max(1, height),
            Start = document.TrimStart, End = document.TrimEnd, ShowEntireVideo = true
        };
        if (!ValidateCandidate(overlay)) return;
        document.Overlays.Add(overlay); overlayCanvas.Select(overlay); overlayCanvas.BeginTextEditing();
    }

    void AddImage()
    {
        if (busy || !CanAddOverlay()) return;
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        var file = FileDialogs.OpenFile(this, I18n.T("加入圖片"),
            I18n.T("PNG／JPEG 圖片") + " (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");
        if (file is null || closed) return;
        try
        {
            var image = VideoOverlayRenderer.LoadImagePng(file);
            var existingBytes = document.Overlays.Sum(item => (long)(item.ImagePng?.Length ?? 0));
            if (image.Png.Length > VideoEditDocument.MaximumImageBytes
                || existingBytes + image.Png.Length > VideoEditDocument.MaximumTotalImageBytes)
            { ShowStatus("影片專案的圖片合計不可超過 64 MB。", true); return; }
            var scale = Math.Min(1, Math.Min(document.Source.Width * .5 / image.Width, document.Source.Height * .5 / image.Height));
            var width = Math.Max(1, image.Width * scale); var height = Math.Max(1, image.Height * scale);
            var overlay = new VideoOverlay
            {
                Kind = VideoOverlayKind.Image, ImagePng = image.Png, Width = width, Height = height,
                X = (document.Source.Width - width) / 2, Y = (document.Source.Height - height) / 2,
                Start = document.TrimStart, End = document.TrimEnd, ShowEntireVideo = true
            };
            if (!ValidateCandidate(overlay)) return;
            document.Overlays.Add(overlay); overlayCanvas.Select(overlay);
        }
        catch (Exception exception) { ShowStatus("無法加入圖片：{0}", true, exception.Message); }
    }

    bool CanAddOverlay()
    {
        if (document.Overlays.Count < VideoEditDocument.MaximumOverlays) return true;
        ShowStatus("每部影片最多可加入 32 個文字或圖片物件。", true); return false;
    }

    bool ValidateCandidate(VideoOverlay overlay)
    {
        try
        {
            // Validate counts, byte/pixel budgets and geometry before the live Canvas decodes a new PNG.
            var candidate = document.CreateSnapshot();
            candidate.Overlays.Add(overlay.Clone()); candidate.Validate(); return true;
        }
        catch (Exception exception) { ShowStatus("無法加入物件：{0}", true, exception.Message); return false; }
    }

    void BeginInlineText()
    {
        if (busy) return;
        PausePlayback();
        if (overlayCanvas.Selected is { } selected && SeekSelectionIntoView(selected)) overlayCanvas.BeginTextEditing();
    }

    void DeleteSelection()
    {
        if (busy) return;
        PausePlayback(); overlayCanvas.DeleteSelected(); RefreshActions();
    }

    void CommitNumber(TextBox field, string property)
    {
        if (synchronizing || busy || overlayCanvas.Selected is not { } overlay) return;
        if (!TryNumber(field.Text ?? "", out var value))
        { ShowStatus("請輸入有效數值。", true); RefreshInspector(); return; }
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        switch (property)
        {
            case "X": overlay.X = Math.Clamp(value, 0, document.Source.Width - overlay.Width); break;
            case "Y": overlay.Y = Math.Clamp(value, 0, document.Source.Height - overlay.Height); break;
            case "Width":
                overlay.X = Math.Min(overlay.X, document.Source.Width - 1);
                overlay.Width = Math.Clamp(value, 1, document.Source.Width - overlay.X); break;
            case "Height":
                overlay.Y = Math.Min(overlay.Y, document.Source.Height - 1);
                overlay.Height = Math.Clamp(value, 1, document.Source.Height - overlay.Y); break;
            case "FontSize": overlay.FontSize = Math.Clamp(value, 8, 240); break;
        }
        overlayCanvas.Refresh(true); RefreshInspector();
    }

    void SetTextColor(string value)
    {
        if (busy || overlayCanvas.Selected is not { Kind: VideoOverlayKind.Text } overlay) return;
        PausePlayback(); overlayCanvas.EndTextEditing(true); overlay.ColorHex = value;
        overlayCanvas.Refresh(true); RefreshInspector();
    }

    void CommitColor()
    {
        if (synchronizing || busy) return;
        var value = (color.Text ?? "").Trim().ToUpperInvariant();
        if (!VideoEditDocument.IsColor(value))
        { ShowStatus("顏色請使用 #RRGGBB 或 #AARRGGBB。", true); RefreshInspector(); return; }
        SetTextColor(value);
    }

    void SetEntireVideo(bool value)
    {
        if (synchronizing || busy || overlayCanvas.Selected is not { } overlay) return;
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        if (!value && (overlay.Start < TimeSpan.Zero || overlay.End > document.Source.Duration || overlay.End <= overlay.Start))
        { overlay.Start = document.TrimStart; overlay.End = document.TrimEnd; }
        overlay.ShowEntireVideo = value; overlayCanvas.Refresh(); RefreshInspector();
        SeekSelectionIntoView(overlay);
    }

    void CommitObjectTime(bool start)
    {
        if (synchronizing || busy || overlayCanvas.Selected is not { } overlay) return;
        if (!TryTime((start ? objectStart : objectEnd).Text ?? "", out var value))
        { ShowStatus("請輸入秒數或 分:秒.毫秒。", true); RefreshInspector(); return; }
        var from = start ? value : overlay.Start; var to = start ? overlay.End : value;
        if (from < TimeSpan.Zero || to > document.Source.Duration || to <= from)
        { ShowStatus("物件的消失時間必須晚於出現時間，且在影片內。", true); RefreshInspector(); return; }
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        overlay.Start = from; overlay.End = to;
        SeekSelectionIntoView(overlay); overlayCanvas.Refresh(); RefreshInspector();
    }

    async Task OpenDocumentAsync()
    {
        if (busy) return;
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        if (!await ConfirmReplacementAsync()) return;
        var file = FileDialogs.OpenFile(this, I18n.T("開啟影片／專案"),
            I18n.T("影片／專案") + " (*.mp4;*.snipvideo)|*.mp4;*.snipvideo|" +
            I18n.T("MP4 影片") + " (*.mp4)|*.mp4|" + I18n.T("SnipFlow 影片專案") + " (*.snipvideo)|*.snipvideo");
        if (file is null || closed) return;
        await RunOperationAsync("正在開啟影片…", false, async token =>
        {
            var next = await VideoProjectStore.OpenAsync(file, token);
            token.ThrowIfCancellationRequested(); BindDocument(next); ShowStatus("已開啟 {0}", false, Path.GetFileName(file));
        });
    }

    async Task<bool> ConfirmReplacementAsync()
    {
        if (!document.IsDirty) return true;
        var answer = MessageBox.Show(this, I18n.T("尚有未儲存的影片專案變更。是否儲存？"), I18n.T("影片編輯"), MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) return false;
        return answer != MessageBoxResult.Yes || await SaveProjectAsync();
    }

    async Task<bool> SaveProjectAsync()
    {
        if (busy) return false;
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        var suggested = document.ProjectPath is { } project ? Path.GetFileName(project)
            : Path.GetFileNameWithoutExtension(document.Source.SourcePath) + ".snipvideo";
        var initial = document.ProjectPath is { } existing ? Path.GetDirectoryName(existing)
            : Path.GetDirectoryName(document.Source.SourcePath);
        if (ChooseDestination("儲存專案", I18n.T("SnipFlow 影片專案") + " (*.snipvideo)|*.snipvideo", suggested,
                ".snipvideo", initial) is not { } target) return false;
        var (path, overwrite) = target;
        return await RunOperationAsync("正在儲存專案…", false, async token =>
        {
            var saved = await VideoProjectStore.SaveAsync(document, path, overwrite, token);
            ShowStatus("專案已儲存：{0}", false, Path.GetFileName(saved));
        });
    }

    async Task ExportVideoAsync()
    {
        if (busy) return;
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        if (ChooseDestination("匯出 MP4", I18n.T("MP4 影片") + " (*.mp4)|*.mp4",
                Path.GetFileNameWithoutExtension(document.Source.SourcePath) + "-edited.mp4", ".mp4",
                Path.GetDirectoryName(document.Source.SourcePath)) is not { } target) return;
        var (destination, overwrite) = target;
        await RunOperationAsync("正在匯出影片…", true, async token =>
        {
            var report = new Progress<double>(value =>
            {
                if (closed || operationCancellation is null || operationCancellation.Token != token
                    || operationCancellation.IsCancellationRequested) return;
                progress.IsIndeterminate = false; progress.Value = Math.Clamp(value, 0, 100);
                progressLabel.Text = I18n.F("匯出 {0:0}%", progress.Value);
            });
            // Keep this await on the UI dispatcher: preview and export share the overlay renderer.
            var path = await VideoExportService.ExportAsync(document, destination, overwrite, report, token);
            if (!exportedPaths.Contains(path, PlatformInfo.PathComparer)) exportedPaths.Add(path);
            lastExportedPath = path;
            ShowStatus("影片已匯出：{0}", false, Path.GetFileName(path));
            // A subscriber cannot turn an already-published MP4 into an export failure.
            try { Exported?.Invoke(path); }
            catch (Exception exception) { SettingsStore.Log(exception); }
        });
    }

    /// <summary>Asks for a destination; null when cancelled, refused, or the source itself was chosen.</summary>
    (string Path, bool Overwrite)? ChooseDestination(string caption, string filter, string suggested, string extension,
        string? initialDirectory)
    {
        var chosen = FileDialogs.SaveFile(this, I18n.T(caption), filter, suggested, defaultExtension: null, initialDirectory);
        if (chosen is null || closed) return null;
        // The picker confirmed overwriting the name it returned; an extension added here was not shown to it.
        var appended = string.IsNullOrEmpty(Path.GetExtension(chosen));
        var path = appended ? chosen + extension : chosen;
        if (extension == ".mp4" && Path.GetFullPath(path).Equals(document.Source.SourcePath, PlatformInfo.PathComparison))
        { ShowStatus("請另存新檔，原始影片不會被覆寫。", true); return null; }
        if (!ConfirmOverwrite(path, caption, confirmedByPicker: !appended, out var overwrite)) return null;
        return (path, overwrite);
    }

    bool ConfirmOverwrite(string path, string caption, bool confirmedByPicker, out bool overwrite)
    {
        overwrite = File.Exists(path);
        if (!overwrite || confirmedByPicker) return true;
        // Authorization is tied to this displayed destination, not a later File.Exists check.
        return MessageBox.Show(this, I18n.F("檔案「{0}」已存在。是否覆寫？", Path.GetFileName(path)),
            I18n.T(caption), MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }

    async Task<bool> RunOperationAsync(string key, bool exporting, Func<CancellationToken, Task> action)
    {
        if (busy || closed) return false;
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = operationCancellation; bool success = false;
        exportingOperation = exporting; activeOperationKey = key;
        PausePlayback(); overlayCanvas.EndTextEditing(true); SetBusy(true);
        progress.IsIndeterminate = !exporting; progress.Value = 0;
        progressLabel.Text = I18n.T(exporting ? "正在匯出影片…" : key);
        cancelOperation.Content = I18n.T(exporting ? "取消匯出" : "取消"); ShowStatus(key);
        try { await action(cancellation.Token); success = true; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { ShowStatus("操作已取消。"); }
        catch (Exception exception) { ShowStatus("操作失敗：{0}", true, exception.Message); }
        finally
        {
            operationCancellation = null; cancellation.Dispose();
            exportingOperation = false; activeOperationKey = null;
            if (!closed) SetBusy(false);
            if (pendingClose && !closed)
            {
                pendingClose = false;
                // Leave the current await/Closing callback before entering another close sequence.
                Dispatcher.UIThread.Post(() => { if (!closed) Close(); });
            }
        }
        return success;
    }

    void CancelCurrentOperation()
    {
        operationCancellation?.Cancel(); cancelOperation.IsEnabled = false;
        ShowStatus("正在取消，請等待清理完成…");
    }

    void SetBusy(bool value)
    {
        busy = value;
        foreach (var control in busyControls) control.IsEnabled = !value;
        progressHost.IsVisible = value;
        cancelOperation.IsVisible = value;
        cancelOperation.IsEnabled = value;
        overlayCanvas.SetPlaybackPosition(position, !value && !playing); RefreshActions();
    }

    void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (allowClose) return;
        if (busy)
        {
            args.Cancel = true; pendingClose = true; CancelCurrentOperation(); return;
        }
        if (savingBeforeClose || promptingClose) { args.Cancel = true; return; }
        PausePlayback(); overlayCanvas.EndTextEditing(true);
        if (!document.IsDirty) return;
        // Ask after the close request returns; a modal prompt must not run inside the platform's close callback.
        args.Cancel = true; promptingClose = true;
        Dispatcher.UIThread.Post(async () =>
        {
            var answer = MessageBox.Show(this, I18n.T("尚有未儲存的影片專案變更。是否儲存？"), I18n.T("影片編輯"), MessageBoxButton.YesNoCancel);
            promptingClose = false;
            if (closed) return;
            if (answer == MessageBoxResult.No) { allowClose = true; Close(); return; }
            if (answer != MessageBoxResult.Yes) return;
            savingBeforeClose = true;
            var saved = await SaveProjectAsync(); savingBeforeClose = false;
            if (saved && !closed) { allowClose = true; Close(); }
        });
    }

    void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.S)
        { args.Handled = true; if (!busy) _ = SaveProjectAsync(); return; }
        if (args.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox) return;
        if (args.Key == Key.Space) { TogglePlayback(); args.Handled = true; }
        else if (args.Key == Key.Delete && !busy) { DeleteSelection(); args.Handled = true; }
    }

    void RefreshActions()
    {
        play.IsEnabled = mediaReady && player is not null && !busy;
        var label = I18n.T(playing ? "暫停" : "播放");
        play.Content = label; ToolTip.SetTip(play, label); AutomationProperties.SetName(play, label);
        addText.IsEnabled = !busy && document.Overlays.Count < VideoEditDocument.MaximumOverlays;
        addImage.IsEnabled = addText.IsEnabled;
    }

    void RefreshTitle()
    {
        var name = Path.GetFileName(document.ProjectPath ?? document.Source.SourcePath);
        fileCaption.Text = name + (document.IsDirty ? " •" : "");
        Title = I18n.T("SnipFlow · 影片編輯") + " — " + name;
    }

    void RefreshTimeline()
    {
        var previous = synchronizing; synchronizing = true;
        try
        {
            seek.Maximum = document.Source.Duration.TotalSeconds; seek.Value = position.TotalSeconds;
            trimStart.Text = FormatTime(document.TrimStart); trimEnd.Text = FormatTime(document.TrimEnd);
            positionLabel.Text = FormatTime(position) + " / " + FormatTime(document.Source.Duration);
            sourceLabel.Text = document.Source.Width.ToString(CultureInfo.InvariantCulture) + " × " + document.Source.Height.ToString(CultureInfo.InvariantCulture);
            rangeLabel.Text = I18n.F("保留片長 {0}", FormatTime(document.ExportDuration));
            trimRange.Update(document.Source.Duration.TotalSeconds, document.TrimStart.TotalSeconds, document.TrimEnd.TotalSeconds, position.TotalSeconds);
        }
        finally { synchronizing = previous; }
    }

    void RefreshInspector()
    {
        var previous = synchronizing; synchronizing = true;
        try
        {
            var overlay = overlayCanvas.Selected;
            properties.IsVisible = overlay is not null;
            emptyProperties.IsVisible = overlay is null;
            if (overlay is null) { objectList.SelectedItem = null; return; }
            textProperties.IsVisible = overlay.Kind == VideoOverlayKind.Text;
            editText.IsVisible = overlay.Kind == VideoOverlayKind.Text;
            x.Text = Number(overlay.X); y.Text = Number(overlay.Y);
            objectWidth.Text = Number(overlay.Width); objectHeight.Text = Number(overlay.Height);
            fontSize.Text = Number(overlay.FontSize); color.Text = overlay.ColorHex;
            entireVideo.IsChecked = overlay.ShowEntireVideo;
            objectStart.Text = FormatTime(overlay.Start); objectEnd.Text = FormatTime(overlay.End);
            objectStart.IsEnabled = !overlay.ShowEntireVideo; objectEnd.IsEnabled = !overlay.ShowEntireVideo;
            objectList.SelectedItem = objectList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag == overlay);
        }
        finally { synchronizing = previous; }
    }

    void RefreshObjectList()
    {
        var previous = synchronizing; synchronizing = true;
        try
        {
            var selected = overlayCanvas.Selected; objectList.Items.Clear();
            foreach (var overlay in document.Overlays)
            {
                var caption = overlay.Kind == VideoOverlayKind.Text ? overlay.Text.Replace("\r", "").Replace("\n", " ") : I18n.T("圖片");
                if (string.IsNullOrWhiteSpace(caption)) caption = I18n.T("文字");
                var row = new DockPanel();
                var glyph = new Border { Width = 24, Child = Icons.Create(overlay.Kind == VideoOverlayKind.Text ? IconKind.TextT : IconKind.Image,
                    14, ThemeService.Brush("Muted")), HorizontalAlignment = HorizontalAlignment.Left };
                DockPanel.SetDock(glyph, Dock.Left);
                row.Children.Add(glyph);
                row.Children.Add(new TextBlock { Text = caption, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var item = CreateListItem(row); item.Tag = overlay; ToolTip.SetTip(item, caption);
                objectList.Items.Add(item); if (overlay == selected) objectList.SelectedItem = item;
            }
        }
        finally { synchronizing = previous; }
    }

    void UpdatePreviewScale()
    {
        var size = previewHost.Bounds.Size;
        var scale = Math.Min(size.Width / document.Source.Width, size.Height / document.Source.Height);
        overlayCanvas.SetScreenScale(double.IsFinite(scale) && scale > 0 ? scale : 1);
    }

    void ShowStatus(string key, bool error = false, params object?[] values)
    {
        statusKey = key; statusValues = values; statusError = error;
        status.Text = values.Length == 0 ? I18n.T(key) : I18n.F(key, values);
        status.Foreground = error ? ThemeService.Brush("Error") : ThemeService.Brush("Muted");
    }

    void LanguageChanged(object? sender, EventArgs args)
    {
        if (closed) return;
        if (Dispatcher.UIThread.CheckAccess()) ApplyLanguage();
        else Dispatcher.UIThread.Post(ApplyLanguage);
    }

    void ApplyLanguage()
    {
        if (closed) return;
        foreach (var (control, key) in localizedText) control.Text = I18n.T(key);
        foreach (var (control, key) in localizedContent) control.Content = I18n.T(key);
        foreach (var (control, key) in localizedNames) { ToolTip.SetTip(control, I18n.T(key)); AutomationProperties.SetName(control, I18n.T(key)); }
        trimRange.SetLabels(I18n.T("裁切片頭"), I18n.T("裁切片尾"));
        RefreshPreviewNotice();
        RefreshTitle(); RefreshTimeline(); RefreshObjectList(); RefreshInspector(); RefreshActions();
        if (busy)
        {
            cancelOperation.Content = I18n.T(exportingOperation ? "取消匯出" : "取消");
            progressLabel.Text = exportingOperation ? I18n.F("匯出 {0:0}%", progress.Value) : I18n.T(activeOperationKey ?? "取消");
        }
        if (statusKey is not null) ShowStatus(statusKey, statusError, statusValues);
    }

    void AttachField(TextBox field, Action commit)
    {
        field.GotFocus += (_, _) => { if (!synchronizing && !busy) PausePlayback(); };
        field.LostFocus += (_, _) => commit();
        field.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter) { commit(); args.Handled = true; }
            else if (args.Key == Key.Escape) { RefreshTimeline(); RefreshInspector(); args.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }

    static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    static bool TryNumber(string text, out double value)
        => (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);

    static bool TryTime(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero; var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        var seconds = 0d;
        foreach (var part in parts)
        {
            if (!TryNumber(part, out var amount) || amount < 0) return false;
            seconds = seconds * 60 + amount;
        }
        if (!double.IsFinite(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds) return false;
        try { value = TimeSpan.FromSeconds(seconds); return true; }
        catch (OverflowException) { return false; }
    }

    static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", (int)value.TotalHours, value.Minutes, value.Seconds, value.Milliseconds)
        : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:000}", (int)value.TotalMinutes, value.Seconds, value.Milliseconds);
    static TimeSpan Min(TimeSpan first, TimeSpan second) => first <= second ? first : second;
    static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;
}

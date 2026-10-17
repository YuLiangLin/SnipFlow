using SnipFlow.Services;
using System.Windows.Input;

namespace SnipFlow;

public partial class MainWindow
{
    readonly CancellationTokenSource historyLifetime = new();
    readonly SemaphoreSlim historyRefreshGate = new(1, 1);
    CancellationTokenSource? historyRefreshCancellation;
    long historyRefreshGeneration;
    bool CanChangeHistory => !isClosed && !busy && !capturePending && !historyDragInProgress && CanStartCapture;

    void InitializeHistoryLifecycle()
    {
        Closed += (_, _) =>
        {
            historyLifetime.Cancel();
            historyRefreshCancellation?.Cancel();
        };
    }

    void RefreshHistory() => _ = RefreshHistoryAsync();

    async Task RefreshHistoryAsync()
    {
        if (isClosed) return;
        var generation = ++historyRefreshGeneration;
        historyRefreshCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(historyLifetime.Token);
        historyRefreshCancellation = cancellation;
        var token = cancellation.Token;
        var acquired = false;
        try
        {
            await historyRefreshGate.WaitAsync(token);
            acquired = true;
            var screenshotsTask = Task.Run(HistoryStore.Load, token);
            var recordingsTask = RecordingHistoryStore.LoadAsync(token);
            IReadOnlyList<HistoryItem> screenshots = Array.Empty<HistoryItem>(), recordings = Array.Empty<HistoryItem>();
            var failed = false;
            try { screenshots = await screenshotsTask; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { failed = true; SettingsStore.Log(exception); }
            try { recordings = await recordingsTask; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { failed = true; SettingsStore.Log(exception); }
            if (isClosed || token.IsCancellationRequested || generation != historyRefreshGeneration) return;
            var items = screenshots.Concat(recordings).OrderByDescending(item => item.RecordedAtUtc).Take(200).ToArray();
            selectingHistory = true;
            try
            {
                HistoryList.ItemsSource = items;
                HistoryCount.Text = items.Length.ToString();
                HistoryEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                HistoryClearButton.IsEnabled = !busy && !capturePending && !historyDragInProgress && items.Length > 0;
            }
            finally { selectingHistory = false; }
            if (failed && !busy) SetStatus("部分擷取歷史無法讀取。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SettingsStore.Log(exception);
            if (!isClosed && generation == historyRefreshGeneration && !busy) SetStatus("部分擷取歷史無法讀取。");
        }
        finally
        {
            if (acquired) historyRefreshGate.Release();
            if (ReferenceEquals(historyRefreshCancellation, cancellation)) historyRefreshCancellation = null;
        }
    }

    async Task AddHistoryAsync(BitmapSource image)
    {
        try
        {
            await Task.Run(() => HistoryStore.Add(image), historyLifetime.Token);
            await RefreshHistoryAsync();
            if (!isClosed) RefreshDocumentState();
        }
        catch (OperationCanceledException) when (isClosed) { }
        catch (Exception exception) { if (!isClosed) ReportError(exception, "截圖已完成，但歷史儲存失敗"); }
    }

    async Task AddRecordingHistoryAsync(string path)
    {
        try
        {
            await RecordingHistoryStore.AddAsync(path, historyLifetime.Token);
            await RefreshHistoryAsync();
        }
        catch (OperationCanceledException) when (isClosed) { }
        catch (Exception exception)
        {
            SettingsStore.Log(exception);
            if (!isClosed) SetStatus("影片已儲存，但歷史紀錄無法新增。");
        }
    }

    void HistoryPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep opening the delete menu from selecting and loading the underlying card.
        if (sender is FrameworkElement { DataContext: HistoryItem }) e.Handled = true;
    }

    async void DeleteHistoryClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!CanChangeHistory || sender is not FrameworkElement { Tag: HistoryItem item }) return;
        busy = true;
        try
        {
            RefreshDocumentState(); SetStatus("正在移除擷取紀錄…");
            var deleted = item.IsRecording
                ? await RecordingHistoryStore.DeleteAsync(item, historyLifetime.Token)
                : await Task.Run(() => HistoryStore.Delete(item), historyLifetime.Token);
            if (isClosed) return;
            await RefreshHistoryAsync();
            SetStatus(item.IsRecording
                ? deleted ? "已移除錄影紀錄，原影片保留。" : "這筆錄影紀錄已不存在。"
                : deleted ? "已刪除這張截圖的歷史副本。" : "這張截圖的歷史副本已不存在。");
        }
        catch (OperationCanceledException) when (isClosed) { }
        catch (Exception ex) { if (!isClosed) ReportError(ex, "歷史刪除失敗"); }
        finally
        {
            busy = false;
            if (!isClosed) { RefreshDocumentState(); QueueAutoSave(); }
        }
    }

    async void ClearHistoryClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!CanChangeHistory) return;
        var answer = MessageBox.Show(this,
            I18n.T("清空最近擷取？截圖歷史副本會刪除，錄影僅移除紀錄與縮圖。原影片、已儲存圖片、專案與目前編輯內容都會保留。"),
            I18n.T("清空最近擷取"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes || !CanChangeHistory) return;
        busy = true;
        try
        {
            RefreshDocumentState(); SetStatus("正在移除擷取紀錄…");
            HistoryClearResult screenshots = new(0, 0), recordings = new(0, 0);
            try { screenshots = await Task.Run(HistoryStore.Clear, historyLifetime.Token); }
            catch (Exception exception) { screenshots = new(0, 1); SettingsStore.Log(exception); }
            try { recordings = await RecordingHistoryStore.ClearAsync(historyLifetime.Token); }
            catch (Exception exception) { recordings = new(0, 1); SettingsStore.Log(exception); }
            var result = new HistoryClearResult(screenshots.DeletedCount + recordings.DeletedCount,
                screenshots.FailedCount + recordings.FailedCount);
            if (isClosed) return;
            await RefreshHistoryAsync();
            if (result.FailedCount == 0) SetStatus("已清空最近擷取（{0} 筆），原影片保留。", result.DeletedCount);
            else
            {
                SetStatus("已移除 {0} 筆；{1} 項未能清理。原影片保留。", result.DeletedCount, result.FailedCount);
                MessageBox.Show(this, I18n.F("已移除 {0} 筆；{1} 項未能清理。原影片保留。", result.DeletedCount, result.FailedCount),
                    I18n.T("歷史清理未完成"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { if (!isClosed) ReportError(ex, "歷史清理失敗"); }
        finally
        {
            busy = false;
            if (!isClosed) { RefreshDocumentState(); QueueAutoSave(); }
        }
    }
}

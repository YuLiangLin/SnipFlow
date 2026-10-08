using SnipFlow.Services;
using System.Windows.Input;

namespace SnipFlow;

public partial class MainWindow
{
    bool CanChangeHistory => !isClosed && !busy && !capturePending && !historyDragInProgress && CanStartCapture;

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
            RefreshDocumentState(); SetStatus("正在刪除截圖歷史…");
            var deleted = await Task.Run(() => HistoryStore.Delete(item));
            if (isClosed) return;
            RefreshHistory();
            SetStatus(deleted ? "已刪除這張截圖的歷史副本。" : "這張截圖的歷史副本已不存在。");
        }
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
            I18n.T("清空所有截圖歷史？只會刪除最近截圖的歷史副本；已匯出／自動儲存的圖片、專案與目前編輯內容都會保留。"),
            I18n.T("清空最近截圖"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes || !CanChangeHistory) return;
        busy = true;
        try
        {
            RefreshDocumentState(); SetStatus("正在刪除截圖歷史…");
            var result = await Task.Run(HistoryStore.Clear);
            if (isClosed) return;
            RefreshHistory();
            if (result.FailedCount == 0) SetStatus("已清空截圖歷史（{0} 張）。", result.DeletedCount);
            else
            {
                SetStatus("已刪除 {0} 張；{1} 張無法刪除。請稍後再試。", result.DeletedCount, result.FailedCount);
                MessageBox.Show(this, I18n.F("已刪除 {0} 張；{1} 張無法刪除。請稍後再試。", result.DeletedCount, result.FailedCount),
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

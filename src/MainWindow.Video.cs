using SnipFlow.Services;
using SnipFlow.Video;

namespace SnipFlow;

public partial class MainWindow
{
    static bool IsVideoPath(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".snipvideo", StringComparison.OrdinalIgnoreCase));
    }

    Task OpenFileAsync(string path)
    {
        if (IsVideoPath(path)) return OpenVideoEditorAsync(path);
        OpenImage(path);
        return Task.CompletedTask;
    }

    public async Task OpenVideoEditorAsync(string path)
    {
        if (!CanChangeHistory) return;
        busy = true;
        try
        {
            Editor.CommitTextEdit();
            autoSaveTimer.Stop();
            FinishAutoSaveWrite();
            RefreshDocumentState();
            SetStatus("正在開啟影片…");
            var window = await VideoEditorWindow.LoadAsync(Path.GetFullPath(path), historyLifetime.Token);
            if (isClosed) { window.Close(); return; }
            window.Owner = this;
            window.ShowDialog();
            var exported = window.ExportedPaths.Count == 0 && window.ExportedPath is { } last
                ? new[] { last }
                : window.ExportedPaths.ToArray();
            exported = exported.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var exportedPath in exported)
                await AddRecordingHistoryAsync(exportedPath);
            if (!isClosed)
            {
                await RefreshHistoryAsync();
                if (exported.Length > 0) SetStatus("已匯出 {0} 部影片。", exported.Length);
                else SetStatus("影片編輯已關閉。");
            }
        }
        catch (OperationCanceledException) when (isClosed) { }
        catch (Exception exception) { if (!isClosed) ReportError(exception, "影片無法開啟"); }
        finally
        {
            busy = false;
            if (!isClosed) { RefreshDocumentState(); QueueAutoSave(); }
        }
    }

    async void OpenVideoClick(object sender, RoutedEventArgs e)
    {
        if (!CanChangeHistory) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = I18n.T("影片與 SnipFlow 影片專案|*.mp4;*.snipvideo|所有檔案|*.*")
        };
        if (dialog.ShowDialog(this) == true) await OpenVideoEditorAsync(dialog.FileName);
    }
}

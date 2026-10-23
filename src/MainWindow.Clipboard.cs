using System.Runtime.InteropServices;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    async void OpenClipboardClick(object? sender, RoutedEventArgs e) => await OpenClipboardImageAsync();

    async void PasteImagesClick(object? sender, RoutedEventArgs e) => await PasteClipboardImagesAsync();

    public Task OpenClipboardImageAsync() => PasteClipboardImagesAsync(openAsNewDocument: true);

    async Task PasteClipboardImagesAsync(bool openAsNewDocument = false, bool allowTextFallback = false)
    {
        if (!CanChangeHistory) return;
        int generation = documentGeneration;
        bool revealWindow = openAsNewDocument && !IsActive;
        ClipboardImageContent content;
        busy = true;
        try
        {
            RefreshDocumentState();
            var clipboard = Clipboard ?? throw new InvalidOperationException(I18n.T("剪貼簿目前無法使用。"));
            // Reading happens only after a click or Ctrl + V, on the UI thread.
            for (int attempt = 0; ; attempt++)
            {
                historyLifetime.Token.ThrowIfCancellationRequested();
                try { content = await ClipboardImageService.ReadAsync(clipboard); break; }
                catch (ExternalException) when (attempt < 4)
                { await Task.Delay(100, historyLifetime.Token); }
            }
        }
        catch (OperationCanceledException) when (isClosed) { return; }
        catch (Exception error)
        {
            if (!isClosed) { if (revealWindow) ShowMainWindow(); ReportError(error, "貼上失敗"); }
            return;
        }
        finally
        {
            busy = false;
            if (!isClosed) { RefreshDocumentState(); QueueAutoSave(); }
        }

        if (!CanChangeHistory || generation != documentGeneration) return;
        if (content.Images.Count == 0)
        {
            if (allowTextFallback && content.HasText) { await StartCodeImageAsync(); return; }
            if (revealWindow) ShowMainWindow();
            SetStatus("剪貼簿沒有圖片，請先複製圖片或圖片檔案。");
            return;
        }

        try
        {
            // The decoded snapshot is ready before asking to replace or save a document.
            if (Editor.IsCollage && !openAsNewDocument)
            {
                Editor.AddImages(content.Images);
                ToolClick(new Button { Tag = "Select" }, new());
                SetStatus("已貼上 {0} 張圖片。", content.Images.Count);
            }
            else
            {
                if (!PrepareToDiscard() || !CanChangeHistory) return;
                if (content.Images.Count == 1)
                {
                    LoadDocument(content.Images[0]);
                    SetStatus("剪貼簿圖片已開啟。");
                }
                else
                {
                    ResetDocumentStorage();
                    Editor.NewCollage();
                    Editor.AddImages(content.Images);
                    activeColor = Color.FromRgb(17, 24, 39);
                    Editor.SetColor(activeColor);
                    ToolClick(new Button { Tag = "Select" }, new());
                    SetStatus("已貼上 {0} 張圖片。", content.Images.Count);
                }
            }
            ApplyWindowMode(false);
            if (revealWindow) ShowMainWindow();
            RefreshDocumentState();
            QueueAutoSave();
            if (IsActive) Editor.Focus();
        }
        catch (Exception error) { if (!isClosed) ReportError(error, "貼上失敗"); }
    }

    void ShowMainWindow()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }
}

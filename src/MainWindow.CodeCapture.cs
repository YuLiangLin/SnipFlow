using System.Runtime.InteropServices;
using SnipFlow.CodeCapture;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    async void CodeClick(object sender, RoutedEventArgs e) => await StartCodeImageAsync();

    public async Task StartCodeImageAsync()
    {
        if (busy || capturePending || !CanStartCapture) return;
        try
        {
            // Read plain text only after the user chooses this action or presses Ctrl + V.
            string? initialCode = null;
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) initialCode = Clipboard.GetText(TextDataFormat.UnicodeText);
                else if (Clipboard.ContainsText(TextDataFormat.Text)) initialCode = Clipboard.GetText(TextDataFormat.Text);
            }
            catch (ExternalException)
            {
                SetStatus("剪貼簿暫時無法讀取，請在視窗內貼上程式碼。");
            }
            if (initialCode?.Length > CodeImageLimits.MaxCharacters)
            {
                SetStatus("剪貼簿的文字太長，請先複製較短的程式碼。");
                MessageBox.Show(this, I18n.T("剪貼簿的文字太長，請先複製較短的程式碼。"), I18n.T("程式碼長圖"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            CloseColorPalette();
            bool appendToCollage = Editor.IsCollage;
            if (appendToCollage)
            {
                if (Editor.ImageCount >= 24) { SetStatus("每份合圖最多可放入 24 張圖片。"); return; }
                Editor.CaptureSession();
            }
            else if (!PrepareToDiscard()) return;

            int expectedGeneration = documentGeneration;
            capturePending = busy = true;
            RefreshDocumentState();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            var composer = new CodeImageWindow(initialCode) { Owner = this };
            if (composer.ShowDialog() != true || composer.Result is not { } image) return;
            if (isClosed || expectedGeneration != documentGeneration || appendToCollage != Editor.IsCollage)
                throw new InvalidOperationException(I18n.T("目前文件已變更，請重新加入程式碼長圖。"));
            if (!image.IsFrozen) image.Freeze();

            if (appendToCollage)
            {
                Editor.AddImages(new[] { image });
                ToolClick(new Button { Tag = "Select" }, new());
            }
            else LoadDocument(image);
            ApplyWindowMode(false);
            Show(); Activate();
            await AddHistoryAsync(image);
            bool copied = false;
            if (SettingsStore.Current.CopyAfterCapture)
            {
                try { await CopyImageAsync(image); copied = true; }
                catch (ExternalException ex)
                {
                    SettingsStore.Log(ex);
                    SetStatus("程式碼長圖已開啟，但複製失敗。");
                    return;
                }
            }
            SetStatus(appendToCollage ? "程式碼已加入合圖。" : copied ? "程式碼長圖已複製，可繼續標註。" : "程式碼長圖已開啟，可繼續標註。");
        }
        catch (Exception ex) { ReportError(ex, "程式碼長圖未完成"); }
        finally
        {
            busy = capturePending = false;
            RefreshDocumentState();
            QueueAutoSave();
        }
    }
}

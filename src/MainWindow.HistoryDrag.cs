using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    private const string HistoryImageDragFormat = "SnipFlow.HistoryImage.v1";
    private HistoryItem? historyDragCandidate;
    private Point historyDragStart;
    private long historyDragGeneration;
    private HistoryImageDrag? activeHistoryDrag;
    private bool historyDragInProgress;

    void InitializeHistoryDrag()
    {
        Editor.AllowDrop = true;
        Editor.PreviewDragEnter += HistoryImageDragOver;
        Editor.PreviewDragOver += HistoryImageDragOver;
        Editor.PreviewDragLeave += HistoryImageDragLeave;
        Editor.PreviewDrop += HistoryImageDrop;
        HistoryList.PreviewQueryContinueDrag += HistoryQueryContinueDrag;
        HistoryList.MouseLeave += (_, _) => historyDragCandidate = null;
        PreviewMouseLeftButtonUp += (_, _) => historyDragCandidate = null;
        PreviewDragEnter += HistoryOutsideEditorDrag;
        PreviewDragOver += HistoryOutsideEditorDrag;
        PreviewDrop += HistoryOutsideEditorDrag;
        Closed += (_, _) =>
        {
            historyDragCandidate = null;
            activeHistoryDrag = null;
            Editor.ClearImageDropPreview();
        };
    }

    void HistoryOutsideEditorDrag(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(HistoryImageDragFormat, autoConvert: false)) return;
        for (DependencyObject? current = e.OriginalSource as DependencyObject; current is not null; current = HistoryDragParent(current))
            if (ReferenceEquals(current, Editor)) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        Editor.ClearImageDropPreview();
    }

    void HistoryDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        historyDragCandidate = null;
        if (!Editor.IsCollage || busy || capturePending || historyDragInProgress || e.ClickCount != 1) return;
        if (e.OriginalSource is not DependencyObject source || IsHistoryDragControl(source)) return;
        if (ItemsControl.ContainerFromElement(HistoryList, source) is not ListBoxItem { Content: HistoryItem item }) return;
        historyDragCandidate = item;
        historyDragStart = e.GetPosition(HistoryList);
        historyDragGeneration = documentGeneration;
        // Let the ListBox select the card normally. A click never inserts an image.
    }

    void HistoryDragMouseMove(object sender, MouseEventArgs e)
    {
        if (historyDragCandidate is null) return;
        if (e.LeftButton != MouseButtonState.Pressed || !Editor.IsCollage || busy || capturePending ||
            historyDragInProgress || historyDragGeneration != documentGeneration)
        {
            historyDragCandidate = null;
            return;
        }
        Point position = e.GetPosition(HistoryList);
        if (Math.Abs(position.X - historyDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - historyDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        HistoryItem item = historyDragCandidate;
        historyDragCandidate = null;
        e.Handled = true;
        historyDragInProgress = true;
        try
        {
            BitmapSource image = HistoryStore.Read(item.Path);
            Editor.ValidateImageInsertion(image);
            activeHistoryDrag = new(image, Guid.NewGuid().ToString("N"), documentGeneration, Editor.Revision);
            var data = new DataObject();
            // Transfer an opaque marker only; never advertise FileDrop or remove a history file.
            data.SetData(HistoryImageDragFormat, activeHistoryDrag.Token, autoConvert: false);
            RefreshDocumentState();
            DragDrop.DoDragDrop(HistoryList, data, DragDropEffects.Copy);
        }
        catch (Exception error) { ReportError(error, "無法加入圖片"); }
        finally
        {
            activeHistoryDrag = null;
            historyDragInProgress = false;
            Editor.ClearImageDropPreview();
            if (!isClosed) { RefreshDocumentState(); QueueAutoSave(); }
        }
    }

    void HistoryImageDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(HistoryImageDragFormat, autoConvert: false)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (TryGetHistoryDrag(e, out HistoryImageDrag drag) &&
            Editor.ShowImageDropPreview(drag.Image, e.GetPosition(Editor)))
            e.Effects = DragDropEffects.Copy;
        else Editor.ClearImageDropPreview();
    }

    void HistoryImageDragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(HistoryImageDragFormat, autoConvert: false)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        Editor.ClearImageDropPreview();
    }

    void HistoryImageDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(HistoryImageDragFormat, autoConvert: false)) return;
        // Consume the custom format before the Window's general FileDrop handler.
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        Editor.ClearImageDropPreview();
        if (!TryGetHistoryDrag(e, out HistoryImageDrag drag)) return;
        try
        {
            if (!Editor.AddDroppedImage(drag.Image, e.GetPosition(Editor))) return;
            e.Effects = DragDropEffects.Copy;
            ToolClick(new Button { Tag = "Select" }, new());
            RefreshDocumentState();
            SetStatus("已加入 {0} 張圖片。", 1);
        }
        catch (Exception error) { ReportError(error, "無法加入圖片"); }
    }

    bool TryGetHistoryDrag(DragEventArgs e, out HistoryImageDrag drag)
    {
        drag = null!;
        if (!historyDragInProgress || activeHistoryDrag is not { } current || busy || capturePending || isClosed ||
            !Editor.IsCollage || current.Generation != documentGeneration || current.Revision != Editor.Revision ||
            (e.AllowedEffects & DragDropEffects.Copy) == 0)
            return false;
        if (e.Data.GetData(HistoryImageDragFormat, autoConvert: false) is not string token ||
            !string.Equals(token, current.Token, StringComparison.Ordinal)) return false;
        drag = current;
        return true;
    }

    void HistoryQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (!historyDragInProgress) return;
        if (e.EscapePressed || isClosed || busy || capturePending || !Editor.IsCollage ||
            activeHistoryDrag?.Generation != documentGeneration)
        {
            e.Action = DragAction.Cancel;
            e.Handled = true;
            Editor.ClearImageDropPreview();
        }
    }

    static bool IsHistoryDragControl(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = HistoryDragParent(current))
        {
            if (current is ButtonBase or ScrollBar or MenuItem or System.Windows.Controls.ContextMenu) return true;
            if (current is ListBoxItem or ListBox) break;
        }
        return false;
    }

    static DependencyObject? HistoryDragParent(DependencyObject item) => item is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);

    private sealed record HistoryImageDrag(BitmapSource Image, string Token, long Generation, long Revision);
}

using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    private const string HistoryImageDragFormat = "SnipFlow.HistoryImage.v1";
    // An in-process format: the opaque token never leaves SnipFlow, and no file list is advertised.
    private static readonly DataFormat<string> HistoryImageFormat = DataFormat.CreateInProcessFormat<string>(HistoryImageDragFormat);
    private const double MinimumDragDistance = 4;
    private HistoryItem? historyDragCandidate;
    private PointerPressedEventArgs? historyDragPress;
    private Point historyDragStart;
    private long historyDragGeneration;
    private HistoryImageDrag? activeHistoryDrag;
    private bool historyDragInProgress;

    void InitializeHistoryDrag()
    {
        DragDrop.SetAllowDrop(Editor, true);
        // Avalonia drag events only bubble. Editor handlers (including for already handled events)
        // run before the window's handlers, which WPF achieved with Preview* handlers on the editor.
        Editor.AddHandler(DragDrop.DragEnterEvent, HistoryImageDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.AddHandler(DragDrop.DragOverEvent, HistoryImageDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.AddHandler(DragDrop.DragLeaveEvent, HistoryImageDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.AddHandler(DragDrop.DropEvent, HistoryImageDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        HistoryList.AddHandler(PointerPressedEvent, HistoryDragMouseDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        HistoryList.AddHandler(PointerMovedEvent, HistoryDragMouseMove, RoutingStrategies.Tunnel);
        HistoryList.PointerExited += (_, _) => { if (!historyDragInProgress) ClearHistoryDragCandidate(); };
        AddHandler(PointerReleasedEvent, (_, _) => { if (!historyDragInProgress) ClearHistoryDragCandidate(); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Registered before the file-drop handlers, so a history drag outside the editor never reaches them.
        AddHandler(DragDrop.DragEnterEvent, HistoryOutsideEditorDrag);
        AddHandler(DragDrop.DragOverEvent, HistoryOutsideEditorDrag);
        AddHandler(DragDrop.DropEvent, HistoryOutsideEditorDrag);
        Closed += (_, _) =>
        {
            ClearHistoryDragCandidate();
            activeHistoryDrag = null;
            Editor.ClearImageDropPreview();
        };
    }

    void ClearHistoryDragCandidate()
    {
        historyDragCandidate = null;
        historyDragPress = null;
    }

    void HistoryOutsideEditorDrag(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(HistoryImageFormat)) return;
        if (e.Source is StyledElement source && HistoryDragAncestors(source).Any(current => ReferenceEquals(current, Editor))) return;
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
        Editor.ClearImageDropPreview();
    }

    void HistoryDragMouseDown(object? sender, PointerPressedEventArgs e)
    {
        ClearHistoryDragCandidate();
        if (!e.GetCurrentPoint(HistoryList).Properties.IsLeftButtonPressed) return;
        if (!Editor.IsCollage || busy || capturePending || historyDragInProgress || e.ClickCount != 1) return;
        if (e.Source is not StyledElement source || IsHistoryDragControl(source)) return;
        if (HistoryItemContainer(source) is not { DataContext: HistoryItem item }) return;
        if (item.IsRecording) return;
        historyDragCandidate = item;
        historyDragPress = e;
        historyDragStart = e.GetPosition(HistoryList);
        historyDragGeneration = documentGeneration;
        // Let the ListBox select the card normally. A click never inserts an image.
    }

    async void HistoryDragMouseMove(object? sender, PointerEventArgs e)
    {
        if (historyDragCandidate is null || historyDragPress is null) return;
        if (!e.GetCurrentPoint(HistoryList).Properties.IsLeftButtonPressed || !Editor.IsCollage || busy || capturePending ||
            historyDragInProgress || historyDragGeneration != documentGeneration)
        {
            ClearHistoryDragCandidate();
            return;
        }
        Point position = e.GetPosition(HistoryList);
        if (Math.Abs(position.X - historyDragStart.X) < MinimumDragDistance &&
            Math.Abs(position.Y - historyDragStart.Y) < MinimumDragDistance) return;

        HistoryItem item = historyDragCandidate;
        var press = historyDragPress;
        ClearHistoryDragCandidate();
        if (item.IsRecording) return;
        e.Handled = true;
        historyDragInProgress = true;
        try
        {
            Bitmap image = HistoryStore.Read(item.Path);
            Editor.ValidateImageInsertion(image);
            activeHistoryDrag = new(image, Guid.NewGuid().ToString("N"), documentGeneration, Editor.Revision);
            var data = new DataTransfer();
            // Transfer an opaque marker only; never advertise a file list or remove a history file.
            data.Add(DataTransferItem.Create(HistoryImageFormat, activeHistoryDrag.Token));
            RefreshDocumentState();
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
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

    void HistoryImageDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(HistoryImageFormat)) return;
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (TryGetHistoryDrag(e, out HistoryImageDrag drag) &&
            Editor.ShowImageDropPreview(drag.Image, e.GetPosition(Editor)))
            e.DragEffects = DragDropEffects.Copy;
        else Editor.ClearImageDropPreview();
    }

    void HistoryImageDragLeave(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(HistoryImageFormat)) return;
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        Editor.ClearImageDropPreview();
    }

    void HistoryImageDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(HistoryImageFormat)) return;
        // Consume the custom format before the window's general file drop handler.
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        Editor.ClearImageDropPreview();
        if (!TryGetHistoryDrag(e, out HistoryImageDrag drag)) return;
        try
        {
            if (!Editor.AddDroppedImage(drag.Image, e.GetPosition(Editor))) return;
            e.DragEffects = DragDropEffects.Copy;
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
            !Editor.IsCollage || current.Generation != documentGeneration || current.Revision != Editor.Revision)
            return false;
        if (e.DataTransfer.TryGetValue(HistoryImageFormat) is not string token ||
            !string.Equals(token, current.Token, StringComparison.Ordinal)) return false;
        drag = current;
        return true;
    }

    static ListBoxItem? HistoryItemContainer(StyledElement source)
    {
        foreach (var current in HistoryDragAncestors(source))
        {
            if (current is ListBoxItem container) return container;
            if (current is ListBox) break;
        }
        return null;
    }

    static bool IsHistoryDragControl(StyledElement source)
    {
        foreach (var current in HistoryDragAncestors(source))
        {
            if (current is Button or ScrollBar or MenuItem or Avalonia.Controls.ContextMenu) return true;
            if (current is ListBoxItem or ListBox) break;
        }
        return false;
    }

    /// <summary>The element and its visual (or, outside the visual tree, logical) ancestors. The top level's
    /// parents can lead back into the tree, so each element is visited once.</summary>
    static IEnumerable<StyledElement> HistoryDragAncestors(StyledElement start)
    {
        var visited = new HashSet<StyledElement>(ReferenceEqualityComparer.Instance);
        for (StyledElement? current = start; current is not null && visited.Add(current); current = HistoryDragParent(current))
            yield return current;
    }

    static StyledElement? HistoryDragParent(StyledElement item) => item is Visual visual && visual.GetVisualParent() is StyledElement parent
        ? parent : (item as ILogical)?.LogicalParent as StyledElement;

    private sealed record HistoryImageDrag(Bitmap Image, string Token, long Generation, long Revision);
}

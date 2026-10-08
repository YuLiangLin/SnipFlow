using System.Windows.Controls.Primitives;
using SnipFlow.Editor;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    string? projectPath;

    void CollageClick(object sender, RoutedEventArgs e)
    {
        if (busy || capturePending || historyDragInProgress) return;
        try
        {
            if (!Editor.IsCollage)
            {
                FinishAutoSaveWrite();
                Editor.CreateCollage();
                ResetDocumentStorage();
                activeColor = Color.FromRgb(17, 24, 39);
                Editor.SetColor(activeColor);
            }
            ToolClick(new Button { Tag = "Select" }, new());
            ApplyWindowMode(false);
            SetStatus("合圖已開啟，可拖入最近截圖或加入圖片。");
            RefreshDocumentState();
            QueueAutoSave();
            Editor.Focus();
        }
        catch (Exception ex) { ReportError(ex, "無法建立合圖"); }
    }

    void ResetDocumentStorage()
    {
        autoSaveTimer.Stop();
        documentGeneration++;
        autoSavePath = projectPath = null;
        autoSavedRevision = -1;
    }

    void AddImagesClick(object sender, RoutedEventArgs e)
    {
        if (busy || capturePending || historyDragInProgress) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = I18n.T("圖片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有檔案|*.*"), Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) AddImageFiles(dialog.FileNames);
    }

    void AddImageFiles(IReadOnlyList<string> paths)
    {
        if (busy || capturePending || historyDragInProgress || paths.Count == 0) return;
        try
        {
            if (paths.Count + Editor.ImageCount + (Editor.IsCollage || !Editor.HasImage ? 0 : 1) > 24)
                throw new InvalidOperationException(I18n.T("每份合圖最多可放入 24 張圖片。"));
            // Decode everything first. A bad file cannot replace the current document.
            var images = paths.Select(HistoryStore.Read).ToList();
            if (!Editor.IsCollage) CollageClick(this, new());
            Editor.AddImages(images);
            ToolClick(new Button { Tag = "Select" }, new());
            ApplyWindowMode(false);
            RefreshDocumentState();
            SetStatus("已加入 {0} 張圖片。", images.Count);
            Editor.Focus();
        }
        catch (Exception ex) { ReportError(ex, "無法加入圖片"); }
    }

    void OpenProject(string path)
    {
        if (busy || capturePending || historyDragInProgress || !PrepareToDiscard()) return;
        try
        {
            EditorSessionStore.RestoreProject(Editor, path);
            ResetDocumentStorage();
            projectPath = Path.GetFullPath(path);
            ApplyWindowMode(false);
            ToolClick(new Button { Tag = "Select" }, new());
            SetStatus("已開啟 {0}", Path.GetFileName(path));
            RefreshDocumentState();
            QueueAutoSave();
        }
        catch (Exception ex) { ReportError(ex, "專案無法開啟"); }
    }

    bool SaveProjectCurrent(bool saveAs = false)
    {
        if (!Editor.HasImage || busy || capturePending || historyDragInProgress) return false;
        Editor.CommitTextEdit();
        string? path = saveAs ? null : projectPath;
        if (path is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = I18n.T("SnipFlow 專案 (*.snipflow)|*.snipflow"),
                FileName = $"SnipFlow_{DateTime.Now:yyyyMMdd_HHmmss}.snipflow",
                DefaultExt = ".snipflow", AddExtension = true, OverwritePrompt = true
            };
            if (Directory.Exists(autoSaveDirectory)) dialog.InitialDirectory = autoSaveDirectory;
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            FinishAutoSaveWrite();
            autoSaveSequence++;
            if (autoSavePath is not null && string.Equals(Path.GetFullPath(path), Path.ChangeExtension(autoSavePath, ".snipflow"), StringComparison.OrdinalIgnoreCase))
                autoSavedRevision = -1;
            EditorSessionStore.SaveProject(Editor, path);
            projectPath = Path.GetFullPath(path);
            Editor.MarkSaved();
            SetStatus("已儲存可編輯專案：{0}", path);
            return true;
        }
        catch (Exception ex) { ReportError(ex, "專案儲存失敗"); return false; }
    }

    void SaveProjectClick(object sender, RoutedEventArgs e) => SaveProjectCurrent();
    bool SaveDocument() => Editor.IsCollage ? SaveProjectCurrent() : SaveCurrent();

    void NewCollageDocument()
    {
        if (busy || capturePending || historyDragInProgress) return;
        if (!PrepareToDiscard()) return;
        ResetDocumentStorage();
        Editor.NewCollage();
        activeColor = Color.FromRgb(17, 24, 39);
        ToolClick(new Button { Tag = "Select" }, new());
        RefreshDocumentState();
        SetStatus("合圖已開啟，可拖入最近截圖或加入圖片。");
    }

    void RunObjectCommand(Action action)
    {
        if (busy || capturePending || historyDragInProgress) return;
        try { action(); RefreshDocumentState(); Editor.Focus(); }
        catch (Exception ex) { ReportError(ex, "無法修改物件"); }
    }

    void OpenActionMenu(object sender, IEnumerable<(string Label, Action Action, bool Enabled)> actions)
    {
        if (busy || capturePending || historyDragInProgress || sender is not Button button) return;
        Editor.CommitTextEdit();
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var action in actions)
        {
            var entry = new MenuItem { Header = I18n.T(action.Label), IsEnabled = action.Enabled };
            entry.Click += (_, _) => RunObjectCommand(action.Action);
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    void ArrangeMenuClick(object sender, RoutedEventArgs e) => OpenActionMenu(sender, new[]
    {
        ("網格排列", (Action)(() => Editor.ArrangeImages(CollageLayout.Grid)), Editor.ImageCount > 0),
        ("直向排列", (Action)(() => Editor.ArrangeImages(CollageLayout.Vertical)), Editor.ImageCount > 0),
        ("橫向排列", (Action)(() => Editor.ArrangeImages(CollageLayout.Horizontal)), Editor.ImageCount > 0)
    });

    void AlignmentMenuClick(object sender, RoutedEventArgs e) => OpenActionMenu(sender, new[]
    {
        ("靠左對齊", (Action)(() => Editor.AlignSelection(ObjectAlignment.Left)), Editor.SelectionCount > 1),
        ("水平置中", (Action)(() => Editor.AlignSelection(ObjectAlignment.Center)), Editor.SelectionCount > 1),
        ("靠右對齊", (Action)(() => Editor.AlignSelection(ObjectAlignment.Right)), Editor.SelectionCount > 1),
        ("靠上對齊", (Action)(() => Editor.AlignSelection(ObjectAlignment.Top)), Editor.SelectionCount > 1),
        ("垂直置中", (Action)(() => Editor.AlignSelection(ObjectAlignment.Middle)), Editor.SelectionCount > 1),
        ("靠下對齊", (Action)(() => Editor.AlignSelection(ObjectAlignment.Bottom)), Editor.SelectionCount > 1),
        ("水平平均分布", (Action)(() => Editor.DistributeSelection(true)), Editor.SelectionCount > 2),
        ("垂直平均分布", (Action)(() => Editor.DistributeSelection(false)), Editor.SelectionCount > 2)
    });

    void OrderMenuClick(object sender, RoutedEventArgs e) => OpenActionMenu(sender, new[]
    {
        ("移到最上層", (Action)(() => Editor.ChangeOrder(ObjectOrder.Front)), Editor.SelectionCount > 0),
        ("上移一層", (Action)(() => Editor.ChangeOrder(ObjectOrder.Forward)), Editor.SelectionCount > 0),
        ("下移一層", (Action)(() => Editor.ChangeOrder(ObjectOrder.Backward)), Editor.SelectionCount > 0),
        ("移到最下層", (Action)(() => Editor.ChangeOrder(ObjectOrder.Back)), Editor.SelectionCount > 0),
        ("複製物件", (Action)Editor.DuplicateSelection, Editor.SelectionCount > 0),
        ("刪除物件", (Action)Editor.DeleteSelection, Editor.SelectionCount > 0)
    });

    void CanvasMenuClick(object sender, RoutedEventArgs e) => OpenActionMenu(sender, new[]
    {
        ("簡報 16:9 · 1920 × 1080", (Action)(() => Editor.ResizeCanvas(1920, 1080)), true),
        ("A4 直向 · 1240 × 1754", (Action)(() => Editor.ResizeCanvas(1240, 1754)), true),
        ("依內容縮合畫布", (Action)Editor.FitCanvasToContent, Editor.HasImage),
        ("新合圖", (Action)NewCollageDocument, true)
    });
}

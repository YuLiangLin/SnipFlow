using SnipFlow.Editor;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    void RefreshEditingTools(AnnotationTool shownTool)
    {
        var available = !busy && !capturePending && !historyDragInProgress && !Editor.IsImageCropping;
        ArrowPresetsBorder.IsVisible = shownTool == AnnotationTool.Arrow && !Editor.IsImageCropping;
        ArrowPresets.IsEnabled = available;
        ArrowPresets.Value = Editor.SelectedArrowStyle ?? Editor.ArrowStyle;
        ArrowPresets.PreviewColor = Editor.SelectedColor ?? Editor.DrawingColor;
        ArrowPresets.PreviewWidth = Editor.SelectedStrokeWidth ?? Editor.StrokeWidth;
        ShapePickerBorder.IsVisible = shownTool == AnnotationTool.Shape && !Editor.IsImageCropping;
        ShapeSelector.IsEnabled = available;
        ShapeSelector.Value = Editor.SelectedShapeKind ?? Editor.ShapeKind;
        ShapeSelector.Filled = Editor.SelectedShapeFilled ?? Editor.ShapeFilled;
        ImageActionsBorder.IsVisible = Editor.HasImage && !Editor.IsImageCropping
            && (shownTool == AnnotationTool.Image || shownTool == AnnotationTool.Select);
        ImageActions.IsEnabled = available;
        ImageActionTarget.Text = I18n.T(Editor.SelectedImageCount > 0 ? "選取的圖片" : "整張圖面");
    }

    bool CanChangeShapeOptions => !refreshingProperties && !busy && !capturePending
        && !historyDragInProgress && !Editor.IsImageCropping
        && (Editor.SelectedTool ?? activeTool) == AnnotationTool.Shape;

    void ShapeKindChanged(object? sender, EventArgs e)
    {
        if (!CanChangeShapeOptions) return;
        Editor.ShapeKind = ShapeSelector.Value;
        SaveShapeOptions();
    }

    void ShapeFillChanged(object? sender, EventArgs e)
    {
        if (!CanChangeShapeOptions) return;
        Editor.ShapeFilled = ShapeSelector.Filled;
        SaveShapeOptions();
    }

    void SaveShapeOptions()
    {
        var settings = SettingsStore.Current;
        var previousKind = settings.ShapeKind;
        var previousFilled = settings.ShapeFilled;
        if (previousKind == Editor.ShapeKind && previousFilled == Editor.ShapeFilled) return;
        settings.ShapeKind = Editor.ShapeKind;
        settings.ShapeFilled = Editor.ShapeFilled;
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            settings.ShapeKind = previousKind;
            settings.ShapeFilled = previousFilled;
            SettingsStore.Log(ex);
            SetStatus("圖形設定已套用，但偏好設定未儲存。");
        }
    }
}

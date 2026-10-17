using System.Windows.Controls.Primitives;
using SnipFlow.Editor;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    void ImageTransformMenuClick(object sender, RoutedEventArgs e)
    {
        if (!CanChangeHistory || !Editor.HasImage || sender is not Button button) return;
        Editor.CommitTextEdit();
        int generation = documentGeneration;
        bool selectedImages = Editor.SelectedImageCount > 0;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = I18n.T(selectedImages ? "選取的圖片" : "整張圖面"), IsEnabled = false });
        foreach (var option in ImageTransformOptions())
            menu.Items.Add(ImageTransformEntry(option.Label, option.Command, !selectedImages, generation));
        if (selectedImages)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = I18n.T("整張圖面"), IsEnabled = false });
            foreach (var option in ImageTransformOptions())
                menu.Items.Add(ImageTransformEntry(option.Label, option.Command, true, generation));
        }
        menu.IsOpen = true;
    }

    static IEnumerable<(string Label, ImageTransformCommand Command)> ImageTransformOptions()
    {
        yield return ("向左旋轉 90°", ImageTransformCommand.RotateLeft);
        yield return ("向右旋轉 90°", ImageTransformCommand.RotateRight);
        yield return ("水平翻轉", ImageTransformCommand.FlipHorizontal);
        yield return ("垂直翻轉", ImageTransformCommand.FlipVertical);
    }

    MenuItem ImageTransformEntry(string label, ImageTransformCommand command, bool entireCanvas, int generation)
    {
        var entry = new MenuItem { Header = I18n.T(label) };
        entry.Click += (_, _) =>
        {
            if (!CanChangeHistory || !Editor.HasImage || generation != documentGeneration) return;
            RunObjectCommand(() =>
            {
                Editor.ApplyImageTransform(command, entireCanvas);
                SetStatus(entireCanvas ? "圖面已旋轉或翻轉。" : "選取圖片已旋轉或翻轉。");
            });
        };
        return entry;
    }
}

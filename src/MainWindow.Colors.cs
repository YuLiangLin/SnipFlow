using System.Windows.Controls.Primitives;
using SnipFlow.Editor;
using SnipFlow.Services;

namespace SnipFlow;

public partial class MainWindow
{
    Popup? colorPalette;

    void MoreColorsClick(object sender, RoutedEventArgs e)
    {
        if (busy || capturePending) return;
        if (colorPalette?.IsOpen == true) { CloseColorPalette(); return; }
        Editor.CommitTextEdit();
        var view = new ColorPaletteView(Editor.SelectedColor ?? activeColor, SettingsStore.Current.RecentColors);
        view.MaxHeight = Math.Max(200, WindowBoundsService.GetWorkArea(this).Height - 32);
        var popup = new Popup
        {
            PlacementTarget = MoreColorsButton, Placement = PlacementMode.Bottom,
            VerticalOffset = 8, StaysOpen = false, AllowsTransparency = true, Child = view
        };
        view.ColorChosen += color => { ApplyAnnotationColor(color); popup.IsOpen = false; Editor.Focus(); };
        view.Cancelled += () => { popup.IsOpen = false; Editor.Focus(); };
        popup.Opened += (_, _) => view.FocusInput();
        popup.Closed += (_, _) => { popup.Child = null; if (colorPalette == popup) colorPalette = null; };
        colorPalette = popup;
        popup.IsOpen = true;
    }

    void CloseColorPalette()
    {
        if (colorPalette != null) colorPalette.IsOpen = false;
    }

    void ApplyAnnotationColor(Color color)
    {
        activeColor = Color.FromRgb(color.R, color.G, color.B);
        Editor.SetColor(activeColor);
        RefreshProperties();
        var hex = ColorPaletteView.ToHex(activeColor);
        var previous = SettingsStore.Current.RecentColors;
        var recent = new[] { hex }.Concat(previous).Distinct(StringComparer.Ordinal).Take(8).ToList();
        if (previous.SequenceEqual(recent)) return;
        SettingsStore.Current.RecentColors = recent;
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SettingsStore.Current.RecentColors = previous;
            SettingsStore.Log(ex);
            SetStatus("色彩已套用，但最近色彩未儲存。");
        }
    }
}

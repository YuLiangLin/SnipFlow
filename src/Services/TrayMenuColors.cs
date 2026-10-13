using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace SnipFlow.Services;

sealed class TrayMenuColors : Forms.ProfessionalColorTable
{
    public TrayMenuColors() => UseSystemColors = false;

    internal static DrawingColor Color(string key)
    {
        var color = ((SolidColorBrush)ThemeService.Brush(key)).Color;
        return DrawingColor.FromArgb(color.R, color.G, color.B);
    }

    public override DrawingColor ToolStripDropDownBackground => Color("Panel");
    public override DrawingColor MenuBorder => Color("Line");
    public override DrawingColor MenuItemBorder => Color("ControlBorder");
    public override DrawingColor MenuItemSelected => Color("Hover");
    public override DrawingColor MenuItemSelectedGradientBegin => Color("Hover");
    public override DrawingColor MenuItemSelectedGradientEnd => Color("Hover");
    public override DrawingColor MenuItemPressedGradientBegin => Color("Selected");
    public override DrawingColor MenuItemPressedGradientMiddle => Color("Selected");
    public override DrawingColor MenuItemPressedGradientEnd => Color("Selected");
    public override DrawingColor ImageMarginGradientBegin => Color("Panel");
    public override DrawingColor ImageMarginGradientMiddle => Color("Panel");
    public override DrawingColor ImageMarginGradientEnd => Color("Panel");
    public override DrawingColor SeparatorDark => Color("Line");
    public override DrawingColor SeparatorLight => Color("Panel");
}

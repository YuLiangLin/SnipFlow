using Avalonia.Input;
using SnipFlow.Services;

namespace SnipFlow;

public sealed class HotkeyInputBox : TextBox
{
    public string Shortcut => Text ?? "";

    protected override Type StyleKeyOverride => typeof(TextBox);

    public HotkeyInputBox(string shortcut)
    {
        Text = shortcut; IsReadOnly = true;
        FontFamily = UiThemes.MonoFont;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        TextAlignment = TextAlignment.Center;
        GotFocus += (_, _) => SelectAll();
    }

    public void SetGesture(HotkeyGesture gesture)
    {
        Text = gesture.ToString(); SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Escape) { base.OnKeyDown(e); return; }
        e.Handled = true;
        if (HotkeyGesture.TryCreate(e.KeyModifiers, e.Key, out var gesture)) SetGesture(gesture);
    }
}

using System.Windows.Input;
using SnipFlow.Services;

namespace SnipFlow;

public sealed class HotkeyInputBox : TextBox
{
    public string Shortcut => Text;

    public HotkeyInputBox(string shortcut)
    {
        Text = shortcut; IsReadOnly = true;
        FontFamily = new FontFamily("Consolas");
        HorizontalContentAlignment = HorizontalAlignment.Center;
        GotKeyboardFocus += (_, _) => SelectAll();
    }

    public void SetGesture(HotkeyGesture gesture)
    {
        Text = gesture.ToString(); SelectAll();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        if (HotkeyGesture.TryCreate(Keyboard.Modifiers, key, out var gesture)) SetGesture(gesture);
    }
}

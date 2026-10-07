using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SnipFlow.Services;

namespace SnipFlow.Editor;

/// <summary>A compact inline palette. Custom colour edits are committed once, when Apply is pressed.</summary>
public sealed class ColorPaletteView : Border
{
    static readonly string[] Presets =
    {
        "#111827", "#374151", "#6B7280", "#9CA3AF", "#D1D5DB", "#F3F4F6", "#FFFFFF", "#000000",
        "#7F1D1D", "#DC2626", "#FF646C", "#FB7185", "#FDBA74", "#FB923C", "#F97316", "#FFCC6A",
        "#854D0E", "#D97706", "#FBBF24", "#FDE047", "#FEF08A", "#BEF264", "#84CC16", "#16A34A",
        "#14532D", "#22C55E", "#86EFAC", "#63D5C5", "#14B8A6", "#0D9488", "#0891B2", "#67E8F9",
        "#0C4A6E", "#0284C7", "#38BDF8", "#60A5FA", "#3B82F6", "#1D4ED8", "#1E3A8A", "#818CF8",
        "#312E81", "#6366F1", "#8B5CF6", "#A78BFA", "#C084FC", "#D946EF", "#EC4899", "#F9A8D4"
    };

    readonly TextBox hexBox = new() { MaxLength = 7, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center };
    readonly Border preview = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock feedback = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
    readonly Slider[] channels = new Slider[3];
    readonly TextBlock[] channelValues = new TextBlock[3];
    Color draft;
    bool synchronizing;

    public event Action<Color>? ColorChosen;
    public event Action? Cancelled;

    public ColorPaletteView(Color selected, IEnumerable<string> recent)
    {
        Width = 310; Padding = new Thickness(16); CornerRadius = new CornerRadius(12);
        Background = Theme("Panel"); BorderBrush = Theme("Line"); BorderThickness = new Thickness(1);
        SnapsToDevicePixels = UseLayoutRounding = true;
        preview.BorderBrush = Theme("Muted"); feedback.Foreground = Theme("Ink");
        var content = new StackPanel();
        Child = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(Heading("常用色彩"));
        content.Children.Add(Swatches(Presets, selected));
        var recentColors = recent.Where(value => TryParseHex(value, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (recentColors.Length > 0)
        {
            content.Children.Add(Heading("最近使用", 12));
            content.Children.Add(Swatches(recentColors, selected));
        }
        content.Children.Add(new Border { Height = 1, Background = Theme("Line"), Margin = new Thickness(0, 14, 0, 0) });
        content.Children.Add(Heading("自訂色彩", 12));
        var hexRow = new DockPanel();
        DockPanel.SetDock(preview, Dock.Left); hexRow.Children.Add(preview); hexRow.Children.Add(hexBox);
        hexBox.ToolTip = I18n.T("HEX 色碼"); AutomationProperties.SetName(hexBox, I18n.T("HEX 色碼"));
        content.Children.Add(hexRow); content.Children.Add(feedback);
        for (int index = 0; index < channels.Length; index++)
        {
            string name = new[] { "R", "G", "B" }[index];
            var row = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
            var label = new TextBlock { Text = name, Width = 20, Foreground = Theme("Muted"), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Width = 30, TextAlignment = TextAlignment.Right, Foreground = Theme("Muted"), VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left); DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(label); row.Children.Add(value); row.Children.Add(slider); content.Children.Add(row);
            channels[index] = slider; channelValues[index] = value;
            AutomationProperties.SetName(slider, "RGB " + name);
            slider.ValueChanged += ChannelChanged;
        }
        var apply = new Button { Content = I18n.T("套用"), Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 72 };
        apply.Click += (_, _) => ApplyDraft(); content.Children.Add(apply);
        Synchronize(selected, updateHex: true);
        hexBox.TextChanged += (_, _) =>
        {
            if (!synchronizing && TryParseHex(hexBox.Text, out var color)) Synchronize(color, updateHex: false);
        };
        PreviewKeyDown += PaletteKeyDown;
    }

    static Brush Theme(string key) => (Brush)Application.Current.FindResource(key);
    static TextBlock Heading(string key, double top = 0) => new()
    {
        Text = I18n.T(key), FontSize = 12, FontWeight = FontWeights.SemiBold,
        Foreground = Theme("Ink"), Margin = new Thickness(0, top, 0, 8)
    };

    UniformGrid Swatches(IEnumerable<string> values, Color selected)
    {
        var colors = values.ToArray();
        var grid = new UniformGrid { Columns = 8, Rows = (colors.Length + 7) / 8 };
        foreach (var hex in colors)
        {
            if (!TryParseHex(hex, out var color)) continue;
            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("ColorSwatch"), Background = new SolidColorBrush(color),
                Width = 28, Height = 26, Margin = new Thickness(2, 2, 2, 2), ToolTip = ToHex(color),
                BorderBrush = Theme(color == selected ? "Accent" : "Muted"), BorderThickness = new Thickness(color == selected ? 2 : 1)
            };
            AutomationProperties.SetName(button, ToHex(color));
            button.Click += (_, _) => ColorChosen?.Invoke(color); grid.Children.Add(button);
        }
        return grid;
    }

    void ChannelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (synchronizing) return;
        Synchronize(Color.FromRgb((byte)channels[0].Value, (byte)channels[1].Value, (byte)channels[2].Value), updateHex: true);
    }

    void Synchronize(Color color, bool updateHex)
    {
        synchronizing = true;
        try
        {
            draft = Color.FromRgb(color.R, color.G, color.B);
            preview.Background = new SolidColorBrush(draft);
            if (updateHex) hexBox.Text = ToHex(draft);
            byte[] values = { draft.R, draft.G, draft.B };
            for (int i = 0; i < values.Length; i++)
            {
                channels[i].Value = values[i]; channelValues[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
            }
            feedback.Visibility = Visibility.Collapsed;
        }
        finally { synchronizing = false; }
    }

    void ApplyDraft()
    {
        if (TryParseHex(hexBox.Text, out var color)) ColorChosen?.Invoke(color);
        else
        {
            feedback.Text = I18n.T("請輸入六位 HEX 色碼，例如 #3B82F6。"); feedback.Visibility = Visibility.Visible;
            hexBox.Focus(); hexBox.SelectAll();
        }
    }

    void PaletteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancelled?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button) { ApplyDraft(); e.Handled = true; }
    }

    public void FocusInput() { hexBox.Focus(); hexBox.SelectAll(); }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    public static bool TryParseHex(string? value, out Color color)
    {
        color = default; value = value?.Trim();
        if (value is not { Length: 7 } || value[0] != '#' || !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
    }
}

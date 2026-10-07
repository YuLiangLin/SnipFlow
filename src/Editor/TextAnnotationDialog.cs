using System.Globalization;
using System.Windows.Input;
using WpfWindow = System.Windows.Window;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfColor = System.Windows.Media.Color;

namespace SnipFlow.Editor;

internal sealed class TextAnnotationDialog : WpfWindow
{
    private readonly WpfTextBox _text;
    private readonly WpfTextBox _fontSize;

    internal string AnnotationText => _text.Text.Trim();
    internal double AnnotationFontSize
    {
        get
        {
            return double.TryParse(_fontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value)
                ? Math.Clamp(value, 12, 240)
                : 26;
        }
    }

    internal TextAnnotationDialog(string initialText, double fontSize)
    {
        Title = string.IsNullOrEmpty(initialText) ? "新增文字 · SnipFlow" : "編輯文字 · SnipFlow";
        Width = 490;
        Height = 320;
        MinWidth = 400;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = BrushFor("#0C111B");
        Foreground = BrushFor("#EEF3F9");
        FontFamily = new FontFamily("Segoe UI");

        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = "輸入標註文字",
            Foreground = BrushFor("#EEF3F9"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        });
        _text = new WpfTextBox
        {
            Text = initialText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = BrushFor("#151D2B"),
            Foreground = BrushFor("#EEF3F9"),
            CaretBrush = BrushFor("#63D5C5"),
            BorderBrush = BrushFor("#293448"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 18)
        };
        Grid.SetRow(_text, 1);
        layout.Children.Add(_text);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var options = new StackPanel();
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(new TextBlock { Text = "文字大小", FontSize = 12, Foreground = BrushFor("#EEF3F9"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _fontSize = new WpfTextBox
        {
            Width = 72,
            Height = 30,
            Text = Math.Round(fontSize).ToString(CultureInfo.CurrentCulture),
            Background = BrushFor("#151D2B"),
            Foreground = BrushFor("#EEF3F9"),
            CaretBrush = BrushFor("#63D5C5"),
            BorderBrush = BrushFor("#293448"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4)
        };
        sizeRow.Children.Add(_fontSize);
        sizeRow.Children.Add(new TextBlock { Text = "px", FontSize = 12, Foreground = BrushFor("#B6C2D1"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        options.Children.Add(sizeRow);
        options.Children.Add(new TextBlock
        {
            Text = "Ctrl + Enter 套用",
            FontSize = 11,
            Foreground = BrushFor("#B6C2D1"),
            Margin = new Thickness(0, 8, 0, 0)
        });
        footer.Children.Add(options);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cancel = CreateButton("取消", primary: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        var apply = CreateButton("套用", primary: true);
        apply.Margin = new Thickness(10, 0, 0, 0);
        apply.Click += (_, _) => AcceptText();
        buttons.Children.Add(apply);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;

        Loaded += (_, _) =>
        {
            _text.Focus();
            _text.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                AcceptText();
                e.Handled = true;
            }
        };
    }

    private void AcceptText()
    {
        if (string.IsNullOrWhiteSpace(_text.Text))
        {
            _text.Focus();
            return;
        }
        DialogResult = true;
    }

    private static WpfButton CreateButton(string label, bool primary)
    {
        var button = new WpfButton
        {
            Content = new TextBlock { Text = label, Foreground = BrushFor(primary ? "#0C111B" : "#F4F7FC") },
            Foreground = BrushFor(primary ? "#0C111B" : "#EEF3F9"),
            Background = BrushFor(primary ? "#63D5C5" : "#1E293B"),
            BorderBrush = BrushFor(primary ? "#63D5C5" : "#293448"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 9, 18, 9),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfButton.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(WpfButton.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(WpfButton.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(WpfButton.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        button.Template = new ControlTemplate(typeof(WpfButton)) { VisualTree = border };
        return button;
    }

    private static SolidColorBrush BrushFor(string value)
    {
        var brush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

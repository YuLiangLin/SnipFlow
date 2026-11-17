using Avalonia.Automation;
using Avalonia.Controls.Primitives;

namespace SnipFlow.Editor;

/// <summary>Inline shape choices whose previews share the document geometry and fill behavior.</summary>
public sealed class ShapePicker : UserControl
{
    static readonly (ShapeKind Kind, string Key)[] Shapes =
    [
        (ShapeKind.RoundedRectangle, "圓角矩形"), (ShapeKind.Triangle, "三角形"),
        (ShapeKind.RightTriangle, "直角三角形"), (ShapeKind.Diamond, "菱形"),
        (ShapeKind.Pentagon, "五邊形"), (ShapeKind.Hexagon, "六邊形"),
        (ShapeKind.Star, "星形"), (ShapeKind.Heart, "愛心"), (ShapeKind.Plus, "十字形"),
        (ShapeKind.Cloud, "雲形"), (ShapeKind.Callout, "對話方塊"),
        (ShapeKind.RightArrow, "向右粗箭頭"), (ShapeKind.LeftArrow, "向左粗箭頭"),
        (ShapeKind.UpArrow, "向上粗箭頭"), (ShapeKind.DownArrow, "向下粗箭頭"),
        (ShapeKind.DoubleArrow, "雙向粗箭頭"), (ShapeKind.Trapezoid, "梯形"),
        (ShapeKind.Parallelogram, "平行四邊形")
    ];

    readonly TextBlock caption = new() { Foreground = ThemeService.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) };
    readonly CheckBox fill = new() { Name = "ShapeFillToggle", Foreground = ThemeService.Brush("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 4) };
    readonly List<(Button Button, ShapePreview Preview, string Key)> choices = new();
    ShapeKind value;
    bool filled;
    bool synchronizing;
    bool subscribed;

    public event EventHandler? ShapeChanged;
    public event EventHandler? FillChanged;

    public ShapeKind Value
    {
        get => value;
        set
        {
            VerifyAccess();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (this.value == value) return;
            this.value = value;
            Synchronize();
        }
    }

    public bool Filled
    {
        get => filled;
        set
        {
            VerifyAccess();
            if (filled == value) return;
            filled = value;
            Synchronize();
        }
    }

    public ShapePicker()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(caption);
        foreach (var (kind, key) in Shapes)
        {
            var preview = new ShapePreview(this, kind) { Width = 24, Height = 24, IsHitTestVisible = false };
            var button = new Button
            {
                Tag = kind, Width = 34, Height = 30, MinHeight = 0,
                Padding = new Thickness(4, 2), Margin = new Thickness(0, 0, 3, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Content = preview
            }.WithTheme("QuietButton");
            button.Click += (_, _) =>
            {
                Value = kind;
                ShapeChanged?.Invoke(this, EventArgs.Empty);
            };
            choices.Add((button, preview, key));
            row.Children.Add(button);
        }
        fill.IsCheckedChanged += (_, _) =>
        {
            if (synchronizing) return;
            Filled = fill.IsChecked == true;
            FillChanged?.Invoke(this, EventArgs.Empty);
        };
        row.Children.Add(fill);
        Content = row;
        Synchronize();
        RefreshLanguage();
        AttachedToVisualTree += (_, _) =>
        {
            if (!subscribed)
            {
                I18n.Changed += LanguageChanged;
                ThemeService.Changed += AppearanceChanged;
                subscribed = true;
            }
            RefreshLanguage();
            InvalidatePreviews();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (!subscribed) return;
            I18n.Changed -= LanguageChanged;
            ThemeService.Changed -= AppearanceChanged;
            subscribed = false;
        };
    }

    void Synchronize()
    {
        var previous = synchronizing;
        synchronizing = true;
        try
        {
            fill.IsChecked = filled;
            foreach (var (button, preview, _) in choices)
            {
                var selected = Equals(button.Tag, value);
                button.Background = selected ? ThemeService.Brush("Selected") : Brushes.Transparent;
                button.BorderBrush = selected ? ThemeService.Brush("SelectionBorder") : ThemeService.Brush("Line");
                preview.InvalidateVisual();
            }
        }
        finally { synchronizing = previous; }
    }

    void InvalidatePreviews()
    {
        foreach (var (_, preview, _) in choices) preview.InvalidateVisual();
    }

    void LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshLanguage();
        else Dispatcher.UIThread.Post(RefreshLanguage);
    }

    void AppearanceChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) InvalidatePreviews();
        else Dispatcher.UIThread.Post(InvalidatePreviews);
    }

    public void RefreshLanguage()
    {
        VerifyAccess();
        caption.Text = I18n.T("常用圖形");
        fill.Content = I18n.T("填滿圖形");
        AutomationProperties.SetName(this, I18n.T("常用圖形"));
        AutomationProperties.SetName(fill, I18n.T("填滿圖形"));
        ToolTip.SetTip(fill, I18n.T("填滿圖形"));
        foreach (var (button, _, key) in choices)
        {
            var name = I18n.T(key);
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
        }
    }

    sealed class ShapePreview(ShapePicker owner, ShapeKind kind) : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            new AnnotationItem
            {
                Tool = AnnotationTool.Shape, ShapeKind = kind, ShapeFilled = owner.Filled,
                Color = ThemeService.GetColor("Ink"), Width = 1.4,
                Start = new Point(3, 3), End = new Point(Math.Max(3, Bounds.Width - 3), Math.Max(3, Bounds.Height - 3))
            }.Draw(context);
        }
    }
}

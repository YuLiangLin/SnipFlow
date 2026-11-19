using Avalonia.Automation;

namespace SnipFlow.Editor;

/// <summary>One-click arrow formats, drawn with the same renderer as document annotations.</summary>
public sealed class ArrowPresetBar : UserControl
{
    sealed record Preset(string Id, string Key, ArrowHeadKind Start, ArrowHeadKind End, ArrowLineStyle Line);

    static readonly Preset[] Presets =
    [
        new("single", "單向實線箭頭", ArrowHeadKind.None, ArrowHeadKind.Arrow, ArrowLineStyle.Solid),
        new("double", "雙向實線箭頭", ArrowHeadKind.Arrow, ArrowHeadKind.Arrow, ArrowLineStyle.Solid),
        new("line", "無箭頭直線", ArrowHeadKind.None, ArrowHeadKind.None, ArrowLineStyle.Solid),
        new("open", "開放式單向箭頭", ArrowHeadKind.None, ArrowHeadKind.Open, ArrowLineStyle.Solid),
        new("double-open", "開放式雙向箭頭", ArrowHeadKind.Open, ArrowHeadKind.Open, ArrowLineStyle.Solid),
        new("circle-end", "圓點端點直線", ArrowHeadKind.None, ArrowHeadKind.Circle, ArrowLineStyle.Solid),
        new("diamond-end", "菱形端點直線", ArrowHeadKind.None, ArrowHeadKind.Diamond, ArrowLineStyle.Solid),
        new("dashed-single", "單向虛線箭頭", ArrowHeadKind.None, ArrowHeadKind.Arrow, ArrowLineStyle.Dash),
        new("dashed-double", "雙向虛線箭頭", ArrowHeadKind.Arrow, ArrowHeadKind.Arrow, ArrowLineStyle.Dash),
        new("dotted-single", "單向點線箭頭", ArrowHeadKind.None, ArrowHeadKind.Arrow, ArrowLineStyle.Dot)
    ];

    readonly TextBlock caption = new() { Foreground = ThemeService.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) };
    readonly List<(Button Button, ArrowPreview Preview, Preset Preset)> choices = new();
    ArrowStyleOptions value = ArrowStyleOptions.Default;
    Color? previewColor;
    double previewWidth = 4;
    bool subscribed;

    public event EventHandler<ArrowStyleChangedEventArgs>? StyleChanged;

    public ArrowStyleOptions Value
    {
        get => value;
        set
        {
            VerifyAccess();
            if (!ArrowStyleOptions.IsValid(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (this.value == value) return;
            this.value = value;
            Synchronize();
        }
    }

    public Color PreviewColor
    {
        get => previewColor ?? ThemeService.GetColor("Ink");
        set { VerifyAccess(); previewColor = value; InvalidatePreviews(); }
    }

    public double PreviewWidth
    {
        get => previewWidth;
        set
        {
            VerifyAccess();
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            previewWidth = Math.Clamp(value, 1, 32);
            InvalidatePreviews();
        }
    }

    public ArrowPresetBar()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(caption);
        foreach (var preset in Presets)
        {
            var preview = new ArrowPreview(this, preset) { Width = 44, Height = 22, IsHitTestVisible = false };
            var button = new Button
            {
                Tag = preset.Id, Width = 54, Height = 30, MinHeight = 0,
                Padding = new Thickness(4, 3), Margin = new Thickness(0, 0, 4, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Content = preview
            }.WithTheme("QuietButton");
            button.Click += (_, _) => Apply(preset);
            choices.Add((button, preview, preset));
            row.Children.Add(button);
        }
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

    void Apply(Preset preset)
    {
        var previous = value;
        Value = value with { StartHead = preset.Start, EndHead = preset.End, LineStyle = preset.Line };
        // A preset also applies to other selected arrows when the primary arrow already matches.
        StyleChanged?.Invoke(this, new ArrowStyleChangedEventArgs(previous, value));
    }

    void Synchronize()
    {
        foreach (var (button, preview, preset) in choices)
        {
            var selected = value.StartHead == preset.Start && value.EndHead == preset.End && value.LineStyle == preset.Line;
            button.Background = selected ? ThemeService.Brush("Selected") : Brushes.Transparent;
            button.BorderBrush = selected ? ThemeService.Brush("SelectionBorder") : ThemeService.Brush("Line");
            preview.InvalidateVisual();
        }
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
        caption.Text = I18n.T("常用箭頭");
        AutomationProperties.SetName(this, I18n.T("常用箭頭"));
        foreach (var (button, _, preset) in choices)
        {
            var name = I18n.T(preset.Key);
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
        }
    }

    sealed class ArrowPreview(ArrowPresetBar owner, Preset preset) : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var color = owner.PreviewColor;
            var light = .2126 * color.R + .7152 * color.G + .0722 * color.B > 160;
            var background = light ? Color.FromRgb(46, 51, 61) : Color.FromRgb(246, 247, 249);
            context.DrawRectangle(new SolidColorBrush(background), null, new Rect(Bounds.Size), 3, 3);
            const double width = 160, height = 60;
            var scale = Math.Min(Bounds.Width / width, Bounds.Height / height);
            var offset = new Vector((Bounds.Width - width * scale) / 2, (Bounds.Height - height * scale) / 2);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
            {
                new AnnotationItem
                {
                    Tool = AnnotationTool.Arrow, Color = color, Width = Math.Clamp(owner.PreviewWidth * 1.2, 3, 7),
                    Start = new Point(16, height / 2), End = new Point(width - 16, height / 2),
                    ArrowStyle = owner.Value with { StartHead = preset.Start, EndHead = preset.End, LineStyle = preset.Line }
                }.Draw(context);
            }
        }
    }
}

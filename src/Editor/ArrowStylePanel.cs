using Avalonia.Automation;
using Avalonia.Controls.Primitives;

namespace SnipFlow.Editor;

public sealed class ArrowStyleChangedEventArgs(ArrowStyleOptions previous, ArrowStyleOptions value, bool? directionPreset = null) : EventArgs
{
    public ArrowStyleOptions Previous { get; } = previous;
    public ArrowStyleOptions Value { get; } = value;
    public bool? DirectionPreset { get; } = directionPreset;
}

/// <summary>Reusable arrow options and a preview drawn by the document's annotation renderer.</summary>
public sealed class ArrowStylePanel : UserControl
{
    readonly ComboBox startHead = new() { Name = "StartHeadSelector" };
    readonly ComboBox startSize = new() { Name = "StartSizeSelector" };
    readonly ComboBox endHead = new() { Name = "EndHeadSelector" };
    readonly ComboBox endSize = new() { Name = "EndSizeSelector" };
    readonly ComboBox lineStyle = new() { Name = "LineStyleSelector" };
    readonly List<(TextBlock Element, string Key)> labels = new();
    readonly List<(ContentControl Element, string Key)> captions = new();
    readonly List<(StyledElement Element, string Key)> names = new();
    readonly ArrowPreview preview;
    ArrowStyleOptions value = new();
    Color previewColor = Color.FromRgb(99, 213, 197);
    double previewWidth = 4;
    bool synchronizing;
    bool languageSubscribed;

    public event EventHandler<ArrowStyleChangedEventArgs>? ValueChanged;

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
        get => previewColor;
        set { VerifyAccess(); previewColor = value; preview.InvalidateVisual(); }
    }

    public double PreviewWidth
    {
        get => previewWidth;
        set
        {
            VerifyAccess();
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            previewWidth = Math.Clamp(value, 1, 32);
            preview.InvalidateVisual();
        }
    }

    public ArrowStylePanel()
    {
        Width = 340; MaxWidth = 356; MaxHeight = 500;
        preview = new ArrowPreview(this) { Name = "ArrowPreview", Height = 92, ClipToBounds = true };
        var content = new StackPanel { Spacing = 12 };
        Content = new Border
        {
            Background = ThemeService.Brush("Panel"),
            Child = new ScrollViewer
            {
                Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        var title = Label("箭頭設定"); title.FontSize = 15; title.FontWeight = FontWeight.SemiBold;
        content.Children.Add(title);
        var presets = new WrapPanel { Orientation = Orientation.Horizontal };
        presets.Children.Add(Preset("單向箭頭", ArrowHeadKind.None));
        presets.Children.Add(Preset("雙向箭頭", ArrowHeadKind.Arrow));
        content.Children.Add(presets);
        var previewGroup = new StackPanel { Spacing = 6 };
        previewGroup.Children.Add(Label("即時預覽"));
        previewGroup.Children.Add(new Border
        {
            Child = preview, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(1)
        });
        NameControl(preview, "箭頭預覽");
        content.Children.Add(previewGroup);
        HeadChoices(startHead); HeadChoices(endHead);
        SizeChoices(startSize); SizeChoices(endSize);
        AddOption(lineStyle, ArrowLineStyle.Solid, "實線");
        AddOption(lineStyle, ArrowLineStyle.Dash, "虛線");
        AddOption(lineStyle, ArrowLineStyle.Dot, "點線");
        content.Children.Add(EndpointRow("起點樣式", startHead, "起點大小", startSize));
        content.Children.Add(EndpointRow("終點樣式", endHead, "終點大小", endSize));
        content.Children.Add(Field("線條格式", lineStyle));
        foreach (var box in Selectors())
        {
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            box.MinWidth = 0;
            box.SelectionChanged += OptionChanged;
        }
        Synchronize();
        RefreshLanguage();
        AttachedToVisualTree += (_, _) =>
        {
            if (!languageSubscribed) { I18n.Changed += LanguageChanged; languageSubscribed = true; }
            RefreshLanguage();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (languageSubscribed) { I18n.Changed -= LanguageChanged; languageSubscribed = false; }
        };
    }

    ComboBox[] Selectors() => [startHead, startSize, endHead, endSize, lineStyle];

    void HeadChoices(ComboBox box)
    {
        AddOption(box, ArrowHeadKind.None, "無端點");
        AddOption(box, ArrowHeadKind.Arrow, "箭頭");
        AddOption(box, ArrowHeadKind.Triangle, "三角形");
        AddOption(box, ArrowHeadKind.Open, "開放箭頭");
        AddOption(box, ArrowHeadKind.Diamond, "菱形");
        AddOption(box, ArrowHeadKind.Circle, "圓形");
    }

    void SizeChoices(ComboBox box)
    {
        AddOption(box, ArrowHeadSize.Small, "小");
        AddOption(box, ArrowHeadSize.Medium, "中");
        AddOption(box, ArrowHeadSize.Large, "大");
    }

    void AddOption<T>(ComboBox box, T option, string key) where T : struct, Enum
    {
        var item = new ComboBoxItem { Tag = option };
        captions.Add((item, key));
        box.Items.Add(item);
    }

    Grid EndpointRow(string headKey, ComboBox head, string sizeKey, ComboBox size)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,96"), ColumnSpacing = 8 };
        row.Children.Add(Field(headKey, head));
        var sizeField = Field(sizeKey, size); Grid.SetColumn(sizeField, 1); row.Children.Add(sizeField);
        return row;
    }

    StackPanel Field(string key, ComboBox box)
    {
        NameControl(box, key);
        var field = new StackPanel { Spacing = 5 };
        field.Children.Add(Label(key)); field.Children.Add(box);
        return field;
    }

    TextBlock Label(string key)
    {
        var label = new TextBlock { Foreground = ThemeService.Brush("Ink"), TextWrapping = TextWrapping.Wrap };
        labels.Add((label, key));
        return label;
    }

    void NameControl(StyledElement control, string key) => names.Add((control, key));

    Button Preset(string key, ArrowHeadKind start)
    {
        var button = new Button { Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 5) }.WithTheme("QuietButton");
        captions.Add((button, key)); NameControl(button, key);
        button.Click += (_, _) => ApplyDirectionPreset(start != ArrowHeadKind.None);
        return button;
    }

    void ApplyDirectionPreset(bool doubleEnded)
    {
        var previous = value;
        Value = value with { StartHead = doubleEnded ? ArrowHeadKind.Arrow : ArrowHeadKind.None, EndHead = ArrowHeadKind.Arrow };
        // A preset is an explicit instruction for both ends, even when the primary selection already matches.
        ValueChanged?.Invoke(this, new ArrowStyleChangedEventArgs(previous, value, doubleEnded));
    }

    static T Selected<T>(ComboBox box, T fallback) where T : struct, Enum
        => box.SelectedItem is ComboBoxItem { Tag: T selected } ? selected : fallback;

    static void Select<T>(ComboBox box, T option) where T : struct, Enum
        => box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, option));

    void Synchronize()
    {
        var previous = synchronizing; synchronizing = true;
        try
        {
            Select(startHead, value.StartHead); Select(endHead, value.EndHead);
            Select(startSize, value.StartSize); Select(endSize, value.EndSize); Select(lineStyle, value.LineStyle);
            startSize.IsEnabled = value.StartHead != ArrowHeadKind.None;
            endSize.IsEnabled = value.EndHead != ArrowHeadKind.None;
            preview.InvalidateVisual();
        }
        finally { synchronizing = previous; }
    }

    void OptionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizing) return;
        Apply(value with
        {
            StartHead = Selected(startHead, value.StartHead), EndHead = Selected(endHead, value.EndHead),
            StartSize = Selected(startSize, value.StartSize), EndSize = Selected(endSize, value.EndSize),
            LineStyle = Selected(lineStyle, value.LineStyle)
        });
    }

    void Apply(ArrowStyleOptions next)
    {
        if (next == value) return;
        var previous = value;
        Value = next;
        ValueChanged?.Invoke(this, new ArrowStyleChangedEventArgs(previous, next));
    }

    void LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshLanguage();
        else Dispatcher.UIThread.Post(RefreshLanguage);
    }

    public void RefreshLanguage()
    {
        VerifyAccess();
        var previous = synchronizing; synchronizing = true;
        try
        {
            foreach (var (element, key) in labels) element.Text = I18n.T(key);
            foreach (var (element, key) in captions) element.Content = I18n.T(key);
            foreach (var box in Selectors())
            {
                var selected = box.SelectedIndex; box.SelectedIndex = -1; box.SelectedIndex = selected;
            }
            foreach (var (element, key) in names)
            {
                AutomationProperties.SetName(element, I18n.T(key));
                if (element is Control control) ToolTip.SetTip(control, I18n.T(key));
            }
        }
        finally { synchronizing = previous; }
    }

    sealed class ArrowPreview(ArrowStylePanel owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var color = owner.PreviewColor;
            var light = .2126 * color.R + .7152 * color.G + .0722 * color.B > 160;
            context.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(46, 51, 61) : Color.FromRgb(246, 247, 249)), null, new Rect(Bounds.Size));
            var padding = Math.Max(24, owner.PreviewWidth * 6);
            var width = Math.Max(Bounds.Width, padding * 2 + 100);
            var height = Math.Max(Bounds.Height, padding * 2 + 12);
            var scale = Math.Min(Bounds.Width / width, Bounds.Height / height);
            var offset = new Vector((Bounds.Width - width * scale) / 2, (Bounds.Height - height * scale) / 2);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
            {
                new AnnotationItem
                {
                    Tool = AnnotationTool.Arrow, ArrowStyle = owner.Value, Color = color, Width = owner.PreviewWidth,
                    Start = new Point(padding, height / 2), End = new Point(width - padding, height / 2)
                }.Draw(context);
            }
        }
    }
}

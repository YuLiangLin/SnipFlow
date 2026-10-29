using Avalonia.Automation;
using Avalonia.Controls.Templates;

namespace SnipFlow.Editor;

/// <summary>A font list shared by the property bar and offscreen layout verification.</summary>
public sealed class TextFontPicker : UserControl
{
    private sealed record FontChoice(string Family, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox choices;
    private string value = TextFonts.DefaultFamily;
    private bool syncing;

    public event EventHandler? FontChanged;
    public bool IsChoosingFont => choices.IsKeyboardFocusWithin || choices.IsDropDownOpen;

    public TextFontPicker()
    {
        MinWidth = 120; MaxWidth = 180;
        choices = new ComboBox
        {
            MinWidth = 0, MinHeight = 28, Height = 28, Padding = new Thickness(8, 2),
            MaxDropDownHeight = 320, IsTextSearchEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            ItemTemplate = new FuncDataTemplate<FontChoice>((choice, _) =>
            {
                var label = new TextBlock
                {
                    Text = choice?.Label ?? "", TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (choice is not null) ToolTip.SetTip(label, choice.Label);
                return label;
            })
        };
        Content = choices;
        choices.SelectionChanged += (_, _) =>
        {
            if (syncing || choices.SelectedItem is not FontChoice choice || choice.Family == value) return;
            value = choice.Family;
            UpdateDescription();
            FontChanged?.Invoke(this, EventArgs.Empty);
        };
        RefreshLanguage();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Clamp(availableSize.Width, MinWidth, MaxWidth) : MaxWidth;
        var measured = base.MeasureOverride(new Size(width, availableSize.Height));
        return new Size(width, measured.Height);
    }

    /// <summary>Synchronizing an existing object or default never emits a user change.</summary>
    public string Value
    {
        get => value;
        set
        {
            var normalized = TextFonts.NormalizeFamily(value);
            if (this.value == normalized) return;
            this.value = normalized;
            RefreshLanguage();
        }
    }

    public void RefreshLanguage()
    {
        syncing = true;
        try
        {
            var fonts = new List<FontChoice> { new(TextFonts.DefaultFamily, I18n.T("預設字型")) };
            fonts.AddRange(TextFonts.AvailableFamilies.Select(family => new FontChoice(family, family)));
            var selected = fonts.FirstOrDefault(font => string.Equals(font.Family, value, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                selected = new FontChoice(value, value + " · " + I18n.T("未安裝，使用替代字型"));
                fonts.Insert(1, selected);
            }
            choices.ItemsSource = fonts;
            choices.SelectedItem = selected;
            AutomationProperties.SetName(choices, I18n.T("字型"));
            UpdateDescription();
        }
        finally { syncing = false; }
    }

    private void UpdateDescription() => ToolTip.SetTip(choices,
        value.Length == 0 ? I18n.T("使用系統預設字型")
            : TextFonts.IsInstalled(value) ? value : value + " · " + I18n.T("未安裝，使用替代字型"));
}

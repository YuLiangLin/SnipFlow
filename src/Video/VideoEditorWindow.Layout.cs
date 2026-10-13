using System.Windows.Automation;
using System.Windows.Shell;
using SnipFlow.Services;

namespace SnipFlow.Video;

public sealed partial class VideoEditorWindow
{
    readonly List<(TextBlock Control, string Key)> localizedText = [];
    readonly List<(ContentControl Control, string Key)> localizedContent = [];
    readonly List<(FrameworkElement Control, string Key)> localizedNames = [];
    readonly MediaElement media = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
        Stretch = Stretch.Fill, ScrubbingEnabled = true, Volume = 1, IsHitTestVisible = false
    };
    readonly VideoEditorCanvas overlayCanvas = new();
    readonly Grid sourceSurface = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly Grid previewHost = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly Viewbox previewBox = new() { Stretch = Stretch.Uniform };
    readonly VideoEditorRangeBar trimRange = new();
    readonly Slider seek = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true,
        SmallChange = .1, LargeChange = 1, MinHeight = 24 };
    readonly TextBlock fileCaption = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock positionLabel = new(), sourceLabel = new(), rangeLabel = new();
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button open = new(), saveProject = new(), export = new(), addText = new(), addImage = new();
    readonly Button play = new(), cancelOperation = new(), editText = new(), delete = new();
    readonly Button startHere = new(), endHere = new(), resetTrim = new();
    readonly TextBox trimStart = new(), trimEnd = new();
    readonly TextBox x = new(), y = new(), objectWidth = new(), objectHeight = new(), fontSize = new(), color = new();
    readonly TextBox objectStart = new(), objectEnd = new();
    readonly CheckBox entireVideo = new();
    readonly ListBox objectList = new() { BorderThickness = new Thickness(0), MaxHeight = 164 };
    readonly StackPanel properties = new(), textProperties = new(), emptyProperties = new();
    readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 4, BorderThickness = new Thickness(0) };
    readonly TextBlock progressLabel = new();
    readonly StackPanel progressHost = new();
    readonly List<FrameworkElement> busyControls = [];

    void BuildLayout()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(BuildCaption());
        AddGridChild(layout, BuildTools(), 1);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(292) });
        sourceSurface.Children.Add(media); sourceSurface.Children.Add(overlayCanvas);
        previewBox.Child = sourceSurface; previewHost.Children.Add(previewBox);
        body.Children.Add(new Border { Child = previewHost, Margin = new Thickness(20, 16, 20, 16),
            BorderBrush = Resource<Brush>("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        var inspector = BuildInspector(); Grid.SetColumn(inspector, 1); body.Children.Add(inspector);
        AddGridChild(layout, body, 2);
        AddGridChild(layout, BuildTimeline(), 3);
        AddGridChild(layout, BuildStatus(), 4);
        Content = new Border { Background = Resource<Brush>("Bg"), BorderBrush = Resource<Brush>("Line"),
            BorderThickness = new Thickness(1), Child = layout };
    }

    FrameworkElement BuildCaption()
    {
        var caption = new Grid();
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.ColumnDefinitions.Add(new ColumnDefinition());
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.Children.Add(Label("影片編輯", new TextBlock { FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(20, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }));
        fileCaption.Foreground = Resource<Brush>("Muted"); fileCaption.VerticalAlignment = VerticalAlignment.Center;
        fileCaption.IsHitTestVisible = false; Grid.SetColumn(fileCaption, 1); caption.Children.Add(fileCaption);
        var windowActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 7, 6) };
        void AddWindowAction(string glyph, string key, Action action)
        {
            var button = new Button { Content = glyph, Style = Resource<Style>("WindowControl"),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Width = 38, Height = 34 };
            SetAccessibleName(button, key); WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += (_, _) => action(); windowActions.Children.Add(button);
        }
        AddWindowAction("\uE921", "最小化", () => WindowState = WindowState.Minimized);
        AddWindowAction("\uE922", "最大化／還原", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        AddWindowAction("\uE8BB", "關閉", Close);
        Grid.SetColumn(windowActions, 2); caption.Children.Add(windowActions);
        return caption;
    }

    FrameworkElement BuildTools()
    {
        var panel = new DockPanel { Margin = new Thickness(20, 8, 20, 12), LastChildFill = false };
        ConfigureButton(open, "開啟影片／專案"); open.Click += async (_, _) => await OpenDocumentAsync();
        ConfigureButton(saveProject, "儲存專案"); saveProject.Click += async (_, _) => await SaveProjectAsync();
        ConfigureButton(export, "匯出 MP4", primary: true); export.Click += async (_, _) => await ExportVideoAsync();
        ConfigureButton(addText, "新增文字"); addText.Click += (_, _) => AddText();
        ConfigureButton(addImage, "加入圖片"); addImage.Click += (_, _) => AddImage();
        DockPanel.SetDock(export, Dock.Right); panel.Children.Add(export);
        foreach (var button in new[] { open, saveProject, addText, addImage }) panel.Children.Add(button);
        busyControls.AddRange([open, saveProject, export, addText, addImage]);
        return new Border { BorderBrush = Resource<Brush>("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = panel };
    }

    FrameworkElement BuildInspector()
    {
        var contents = new StackPanel { Margin = new Thickness(18, 16, 18, 20) };
        contents.Children.Add(Label("物件", new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }));
        objectList.Background = Brushes.Transparent; objectList.Foreground = Resource<Brush>("Ink");
        objectList.ItemContainerStyle = ListItemStyle(); SetAccessibleName(objectList, "文字與圖片物件");
        contents.Children.Add(objectList);
        contents.Children.Add(new Border { Height = 1, Background = Resource<Brush>("Line"), Margin = new Thickness(0, 16, 0, 18) });
        emptyProperties.Children.Add(Label("加入文字或圖片後，在預覽上拖曳調整。", new TextBlock
            { Foreground = Resource<Brush>("Muted"), TextWrapping = TextWrapping.Wrap, LineHeight = 21 }));
        contents.Children.Add(emptyProperties);

        var selectedActions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };
        ConfigureButton(editText, "編輯文字"); editText.Click += (_, _) => BeginInlineText();
        ConfigureButton(delete, "刪除物件"); delete.Foreground = new SolidColorBrush(Color.FromRgb(245, 183, 186));
        delete.Click += (_, _) => DeleteSelection(); DockPanel.SetDock(delete, Dock.Right);
        selectedActions.Children.Add(delete); selectedActions.Children.Add(editText); properties.Children.Add(selectedActions);
        AddPair(properties, "位置 X", x, "位置 Y", y);
        AddPair(properties, "寬度", objectWidth, "高度", objectHeight);
        properties.Children.Add(new Border { Height = 1, Background = Resource<Brush>("Line"), Margin = new Thickness(0, 12, 0, 14) });
        AddField(textProperties, "字級", fontSize); AddField(textProperties, "文字顏色", color);
        var swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var value in new[] { "#FFFFFF", "#161616", "#FF5E66", "#FFCE67", "#5ECFAE", "#5ABCF5", "#8B9DFF", "#C89AFF" })
        {
            var swatch = new Button { Style = Resource<Style>("ColorSwatch"), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)),
                ToolTip = value, Margin = new Thickness(0, 0, 7, 7) };
            AutomationProperties.SetName(swatch, value); swatch.Click += (_, _) => SetTextColor(value);
            swatches.Children.Add(swatch);
        }
        textProperties.Children.Add(swatches); properties.Children.Add(textProperties);
        ContentLabel(entireVideo, "全片顯示"); entireVideo.Margin = new Thickness(0, 4, 0, 12); properties.Children.Add(entireVideo);
        AddField(properties, "出現時間", objectStart); AddField(properties, "消失時間", objectEnd);
        properties.Children.Add(Label("時間以原始影片為準。", new TextBlock { Foreground = Resource<Brush>("Muted"),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }));
        contents.Children.Add(properties);
        busyControls.AddRange([objectList, properties]);
        return new Border { Background = Resource<Brush>("Panel"), BorderBrush = Resource<Brush>("Line"),
            BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = contents,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    }

    FrameworkElement BuildTimeline()
    {
        var panel = new StackPanel { Margin = new Thickness(20, 12, 20, 14) };
        var playback = new DockPanel { LastChildFill = true };
        ConfigureButton(play, "播放"); play.MinWidth = 70; play.Click += (_, _) => TogglePlayback();
        playback.Children.Add(play); positionLabel.VerticalAlignment = VerticalAlignment.Center;
        positionLabel.Margin = new Thickness(8, 0, 0, 0); positionLabel.FontFamily = new FontFamily("Consolas");
        DockPanel.SetDock(sourceLabel, Dock.Right); sourceLabel.VerticalAlignment = VerticalAlignment.Center;
        sourceLabel.Foreground = Resource<Brush>("Muted"); playback.Children.Add(sourceLabel); playback.Children.Add(positionLabel);
        panel.Children.Add(playback);
        SetAccessibleName(seek, "播放位置"); seek.Margin = new Thickness(0, 6, 0, 0);
        seek.Background = Resource<Brush>("Line"); seek.Foreground = Resource<Brush>("Accent");
        panel.Children.Add(seek); panel.Children.Add(trimRange);
        var trimming = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var startField = TimeField("片頭", trimStart); startField.Margin = new Thickness(0, 0, 16, 8); trimming.Children.Add(startField);
        var endField = TimeField("片尾", trimEnd); endField.Margin = new Thickness(0, 0, 16, 8); trimming.Children.Add(endField);
        ConfigureButton(startHere, "此處設為片頭"); startHere.Click += (_, _) => SetTrimAtPosition(true);
        ConfigureButton(endHere, "此處設為片尾"); endHere.Click += (_, _) => SetTrimAtPosition(false);
        ConfigureButton(resetTrim, "重設裁切"); resetTrim.Click += (_, _) => ResetTrim();
        foreach (var button in new[] { startHere, endHere, resetTrim })
        { button.Margin = new Thickness(0, 0, 6, 8); trimming.Children.Add(button); }
        panel.Children.Add(trimming);
        rangeLabel.Foreground = Resource<Brush>("Muted"); rangeLabel.FontSize = 12; rangeLabel.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(rangeLabel);
        busyControls.AddRange([seek, trimRange, trimStart, trimEnd, startHere, endHere, resetTrim]);
        return new Border { BorderBrush = Resource<Brush>("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = panel };
    }

    FrameworkElement BuildStatus()
    {
        var footer = new Grid { Margin = new Thickness(20, 10, 20, 12), MinHeight = 26 };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Foreground = Resource<Brush>("Muted"); status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(0, 0, 16, 0); footer.Children.Add(status);
        progressHost.Width = 170; progressHost.Margin = new Thickness(0, 0, 12, 0);
        progressLabel.FontSize = 11; progressLabel.Foreground = Resource<Brush>("Muted"); progressLabel.Margin = new Thickness(0, 0, 0, 4);
        progress.Foreground = Resource<Brush>("Accent"); progress.Background = Resource<Brush>("Line");
        progressHost.Children.Add(progressLabel); progressHost.Children.Add(progress); progressHost.Visibility = Visibility.Collapsed;
        Grid.SetColumn(progressHost, 1); footer.Children.Add(progressHost);
        ConfigureButton(cancelOperation, "取消"); cancelOperation.Visibility = Visibility.Collapsed;
        cancelOperation.Click += (_, _) => CancelCurrentOperation(); Grid.SetColumn(cancelOperation, 2); footer.Children.Add(cancelOperation);
        return new Border { Background = Resource<Brush>("Panel"), BorderBrush = Resource<Brush>("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
    }

    FrameworkElement TimeField(string key, TextBox box)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Label(key, new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }));
        PrepareField(box, key); box.Width = 114; box.FontFamily = new FontFamily("Consolas"); panel.Children.Add(box); return panel;
    }

    void AddPair(Panel host, string firstKey, TextBox first, string secondKey, TextBox second)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; AddField(left, firstKey, first);
        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) }; AddField(right, secondKey, second);
        grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); host.Children.Add(grid);
    }

    void AddField(Panel host, string key, TextBox box)
    {
        host.Children.Add(Label(key, new TextBlock { Foreground = Resource<Brush>("Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) }));
        PrepareField(box, key); box.Margin = new Thickness(0, 0, 0, 12); host.Children.Add(box);
    }

    void PrepareField(TextBox box, string key)
    {
        box.MinWidth = 50; box.MaxLength = 24; box.MinHeight = 32; box.VerticalContentAlignment = VerticalAlignment.Center;
        SetAccessibleName(box, key);
    }

    void ConfigureButton(Button button, string key, bool primary = false)
    {
        if (primary) button.Style = Resource<Style>("Primary");
        button.MinHeight = 34; button.Margin = new Thickness(0, 0, 6, 0);
        ContentLabel(button, key); SetAccessibleName(button, key);
    }

    TextBlock Label(string key, TextBlock control)
    { localizedText.Add((control, key)); control.Text = I18n.T(key); return control; }
    void ContentLabel(ContentControl control, string key)
    { localizedContent.Add((control, key)); control.Content = I18n.T(key); }
    void SetAccessibleName(FrameworkElement control, string key)
    { localizedNames.Add((control, key)); control.ToolTip = I18n.T(key); AutomationProperties.SetName(control, I18n.T(key)); }
    static T Resource<T>(string key) where T : class => (T)Application.Current.FindResource(key);
    static void AddGridChild(Grid parent, UIElement child, int row)
    { Grid.SetRow(child, row); parent.Children.Add(child); }

    Style ListItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Resource<Brush>("Ink")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 7, 9, 7));
        border.AppendChild(presenter);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Resource<Brush>("Hover"))); style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Resource<Brush>("Selected"))); style.Triggers.Add(selected);
        return style;
    }

}

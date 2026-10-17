using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using SnipFlow.Services;

namespace SnipFlow.Video;

public sealed partial class VideoEditorWindow
{
    readonly List<(TextBlock Control, string Key)> localizedText = [];
    readonly List<(ContentControl Control, string Key)> localizedContent = [];
    readonly List<(Control Control, string Key)> localizedNames = [];
    // Replaces the WPF MediaElement: libvlc frames (or ffmpeg stills) drawn under the overlay canvas.
    readonly VideoFrameView frameView = new();
    readonly VideoEditorCanvas overlayCanvas = new();
    readonly Grid sourceSurface = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly Grid previewHost = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly Viewbox previewBox = new() { Stretch = Stretch.Uniform };
    readonly Border previewNotice = new()
    {
        IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16), Padding = new Thickness(12, 8),
        CornerRadius = new CornerRadius(8), MaxWidth = 640
    };
    readonly TextBlock previewNoticeText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly VideoEditorRangeBar trimRange = new();
    readonly Slider seek = new() { Minimum = 0, Maximum = 1, SmallChange = .1, LargeChange = 1, MinHeight = 24 };
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
    readonly List<Control> busyControls = [];

    void BuildLayout()
    {
        var layout = new Grid { RowDefinitions = new RowDefinitions("48,Auto,*,Auto,Auto") };
        layout.Children.Add(BuildCaption());
        AddGridChild(layout, BuildTools(), 1);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,292") };
        sourceSurface.Children.Add(frameView); sourceSurface.Children.Add(overlayCanvas);
        previewBox.Child = sourceSurface; previewHost.Children.Add(previewBox);
        previewNotice.Background = ThemeService.Brush("OverlayPanel");
        previewNoticeText.Foreground = ThemeService.Brush("OverlayInk");
        previewNotice.Child = previewNoticeText; previewHost.Children.Add(previewNotice);
        body.Children.Add(new Border { Child = previewHost, Margin = new Thickness(20, 16, 20, 16), ClipToBounds = true,
            BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        var inspector = BuildInspector(); Grid.SetColumn(inspector, 1); body.Children.Add(inspector);
        AddGridChild(layout, body, 2);
        AddGridChild(layout, BuildTimeline(), 3);
        AddGridChild(layout, BuildStatus(), 4);
        Content = new Border { Background = ThemeService.Brush("Bg"), BorderBrush = ThemeService.Brush("Line"),
            BorderThickness = new Thickness(1), Child = layout };
    }

    Control BuildCaption()
    {
        var caption = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Background = Brushes.Transparent };
        caption.Children.Add(Label("影片編輯", new TextBlock { FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(20, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }));
        fileCaption.Foreground = ThemeService.Brush("Muted"); fileCaption.VerticalAlignment = VerticalAlignment.Center;
        fileCaption.IsHitTestVisible = false; Grid.SetColumn(fileCaption, 1); caption.Children.Add(fileCaption);
        var windowActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 7, 6) };
        void AddWindowAction(IconKind icon, string key, Action action)
        {
            var button = new Button { Content = Icons.Create(icon, 12), Width = 38, Height = 34 }.WithTheme("WindowControl");
            SetAccessibleName(button, key);
            button.Click += (_, _) => action(); windowActions.Children.Add(button);
        }
        AddWindowAction(IconKind.Subtract, "最小化", () => WindowState = WindowState.Minimized);
        AddWindowAction(IconKind.Maximize, "最大化／還原", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        AddWindowAction(IconKind.Dismiss, "關閉", Close);
        Grid.SetColumn(windowActions, 2); caption.Children.Add(windowActions);
        WindowChrome.EnableTitleBarDrag(this, caption);
        return caption;
    }

    Control BuildTools()
    {
        var panel = new DockPanel { Margin = new Thickness(20, 8, 20, 12), LastChildFill = false };
        ConfigureButton(open, "開啟影片／專案"); open.Click += async (_, _) => await OpenDocumentAsync();
        ConfigureButton(saveProject, "儲存專案"); saveProject.Click += async (_, _) => await SaveProjectAsync();
        ConfigureButton(export, "匯出 MP4", primary: true); export.Click += async (_, _) => await ExportVideoAsync();
        ConfigureButton(addText, "新增文字"); addText.Click += (_, _) => AddText();
        ConfigureButton(addImage, "加入圖片"); addImage.Click += (_, _) => AddImage();
        DockPanel.SetDock(export, Dock.Right); panel.Children.Add(export);
        foreach (var button in new[] { open, saveProject, addText, addImage })
        { DockPanel.SetDock(button, Dock.Left); panel.Children.Add(button); }
        busyControls.AddRange([open, saveProject, export, addText, addImage]);
        return new Border { BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = panel };
    }

    Control BuildInspector()
    {
        var contents = new StackPanel { Margin = new Thickness(18, 16, 18, 20) };
        contents.Children.Add(Label("物件", new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) }));
        objectList.Background = Brushes.Transparent; objectList.Foreground = ThemeService.Brush("Ink");
        StyleObjectList(); SetAccessibleName(objectList, "文字與圖片物件");
        contents.Children.Add(objectList);
        contents.Children.Add(new Border { Height = 1, Background = ThemeService.Brush("Line"), Margin = new Thickness(0, 16, 0, 18) });
        emptyProperties.Children.Add(Label("加入文字或圖片後，在預覽上拖曳調整。", new TextBlock
            { Foreground = ThemeService.Brush("Muted"), TextWrapping = TextWrapping.Wrap, LineHeight = 21 }));
        contents.Children.Add(emptyProperties);

        var selectedActions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };
        ConfigureButton(editText, "編輯文字"); editText.Click += (_, _) => BeginInlineText();
        ConfigureButton(delete, "刪除物件"); delete.Foreground = ThemeService.Brush("Error");
        delete.Click += (_, _) => DeleteSelection(); DockPanel.SetDock(delete, Dock.Right);
        DockPanel.SetDock(editText, Dock.Left);
        selectedActions.Children.Add(delete); selectedActions.Children.Add(editText); properties.Children.Add(selectedActions);
        AddPair(properties, "位置 X", x, "位置 Y", y);
        AddPair(properties, "寬度", objectWidth, "高度", objectHeight);
        properties.Children.Add(new Border { Height = 1, Background = ThemeService.Brush("Line"), Margin = new Thickness(0, 12, 0, 14) });
        AddField(textProperties, "字級", fontSize); AddField(textProperties, "文字顏色", color);
        var swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var value in new[] { "#FFFFFF", "#161616", "#FF5E66", "#FFCE67", "#5ECFAE", "#5ABCF5", "#8B9DFF", "#C89AFF" })
        {
            var swatch = new Button { Background = Brush.Parse(value), Margin = new Thickness(0, 0, 7, 7) }.WithTheme("ColorSwatch");
            ToolTip.SetTip(swatch, value);
            AutomationProperties.SetName(swatch, value); swatch.Click += (_, _) => SetTextColor(value);
            swatches.Children.Add(swatch);
        }
        textProperties.Children.Add(swatches); properties.Children.Add(textProperties);
        ContentLabel(entireVideo, "全片顯示"); entireVideo.Margin = new Thickness(0, 4, 0, 12); properties.Children.Add(entireVideo);
        AddField(properties, "出現時間", objectStart); AddField(properties, "消失時間", objectEnd);
        properties.Children.Add(Label("時間以原始影片為準。", new TextBlock { Foreground = ThemeService.Brush("Muted"),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }));
        contents.Children.Add(properties);
        busyControls.AddRange([objectList, properties]);
        return new Border { Background = ThemeService.Brush("Panel"), BorderBrush = ThemeService.Brush("Line"),
            BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = contents,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    }

    Control BuildTimeline()
    {
        var panel = new StackPanel { Margin = new Thickness(20, 12, 20, 14) };
        var playback = new DockPanel { LastChildFill = true };
        ConfigureButton(play, "播放"); play.MinWidth = 70; play.Click += (_, _) => TogglePlayback();
        DockPanel.SetDock(play, Dock.Left); playback.Children.Add(play);
        positionLabel.VerticalAlignment = VerticalAlignment.Center;
        positionLabel.Margin = new Thickness(8, 0, 0, 0); positionLabel.FontFamily = UiThemes.MonoFont;
        DockPanel.SetDock(sourceLabel, Dock.Right); sourceLabel.VerticalAlignment = VerticalAlignment.Center;
        sourceLabel.Foreground = ThemeService.Brush("Muted"); playback.Children.Add(sourceLabel); playback.Children.Add(positionLabel);
        panel.Children.Add(playback);
        SetAccessibleName(seek, "播放位置"); seek.Margin = new Thickness(0, 6, 0, 0);
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
        rangeLabel.Foreground = ThemeService.Brush("Muted"); rangeLabel.FontSize = 12; rangeLabel.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(rangeLabel);
        busyControls.AddRange([seek, trimRange, trimStart, trimEnd, startHere, endHere, resetTrim]);
        return new Border { BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = panel };
    }

    Control BuildStatus()
    {
        var footer = new Grid { Margin = new Thickness(20, 10, 20, 12), MinHeight = 26, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        status.Foreground = ThemeService.Brush("Muted"); status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(0, 0, 16, 0); footer.Children.Add(status);
        progressHost.Width = 170; progressHost.Margin = new Thickness(0, 0, 12, 0); progressHost.VerticalAlignment = VerticalAlignment.Center;
        progressLabel.FontSize = 11; progressLabel.Foreground = ThemeService.Brush("Muted"); progressLabel.Margin = new Thickness(0, 0, 0, 4);
        progress.Foreground = ThemeService.Brush("Accent"); progress.Background = ThemeService.Brush("Line");
        progressHost.Children.Add(progressLabel); progressHost.Children.Add(progress); progressHost.IsVisible = false;
        Grid.SetColumn(progressHost, 1); footer.Children.Add(progressHost);
        ConfigureButton(cancelOperation, "取消"); cancelOperation.IsVisible = false;
        cancelOperation.Click += (_, _) => CancelCurrentOperation(); Grid.SetColumn(cancelOperation, 2); footer.Children.Add(cancelOperation);
        return new Border { Background = ThemeService.Brush("Panel"), BorderBrush = ThemeService.Brush("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
    }

    Control TimeField(string key, TextBox box)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Label(key, new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }));
        PrepareField(box, key); box.Width = 114; box.FontFamily = UiThemes.MonoFont; panel.Children.Add(box); return panel;
    }

    void AddPair(Panel host, string firstKey, TextBox first, string secondKey, TextBox second)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; AddField(left, firstKey, first);
        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) }; AddField(right, secondKey, second);
        grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); host.Children.Add(grid);
    }

    void AddField(Panel host, string key, TextBox box)
    {
        host.Children.Add(Label(key, new TextBlock { Foreground = ThemeService.Brush("Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) }));
        PrepareField(box, key); box.Margin = new Thickness(0, 0, 0, 12); host.Children.Add(box);
    }

    void PrepareField(TextBox box, string key)
    {
        box.MinWidth = 50; box.MaxLength = 24; box.MinHeight = 32; box.VerticalContentAlignment = VerticalAlignment.Center;
        SetAccessibleName(box, key);
    }

    void ConfigureButton(Button button, string key, bool primary = false)
    {
        if (primary) button.WithTheme("Primary");
        button.MinHeight = 34; button.Margin = new Thickness(0, 0, 6, 0);
        ContentLabel(button, key); SetAccessibleName(button, key);
    }

    TextBlock Label(string key, TextBlock control)
    { localizedText.Add((control, key)); control.Text = I18n.T(key); return control; }
    void ContentLabel(ContentControl control, string key)
    { localizedContent.Add((control, key)); control.Content = I18n.T(key); }
    void SetAccessibleName(Control control, string key)
    { localizedNames.Add((control, key)); ToolTip.SetTip(control, I18n.T(key)); AutomationProperties.SetName(control, I18n.T(key)); }
    static void AddGridChild(Grid parent, Control child, int row)
    { Grid.SetRow(child, row); parent.Children.Add(child); }

    void StyleObjectList()
    {
        // Fluent list item states, mapped onto the app palette (the WPF version used its own template).
        objectList.Resources["SystemControlHighlightListLowBrush"] = ThemeService.Brush("Hover");
        objectList.Resources["SystemControlHighlightListMediumBrush"] = ThemeService.Brush("Selected");
        objectList.Resources["SystemControlHighlightListAccentLowBrush"] = ThemeService.Brush("Selected");
        objectList.Resources["SystemControlHighlightListAccentMediumBrush"] = ThemeService.Brush("Selected");
        objectList.Resources["SystemControlHighlightListAccentHighBrush"] = ThemeService.Brush("Selected");
        objectList.Resources["SystemControlHighlightAltBaseHighBrush"] = ThemeService.Brush("Ink");
    }

    ListBoxItem CreateListItem(object content) => new()
    {
        Content = content, Foreground = ThemeService.Brush("Ink"), Background = Brushes.Transparent,
        Padding = new Thickness(9, 7, 9, 7), CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
}

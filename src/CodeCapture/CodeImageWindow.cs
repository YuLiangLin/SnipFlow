using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;

namespace SnipFlow.CodeCapture;

public sealed class CodeImageWindow : DialogWindow
{
    readonly TextEditor code = new();
    readonly ComboBox language = new(), font = new(), size = new(), theme = new();
    readonly TextBox width = new() { Text = "1280" }, startingLine = new() { Text = "1" };
    readonly CheckBox wrap = new() { IsChecked = true }, numbers = new() { IsChecked = true };
    readonly Image preview = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly ScrollViewer previewScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock status = new(), dimensions = new(), emptyPreview = new();
    readonly Button render = new(), copy = new(), save = new(), import = new();
    readonly List<(TextBlock Element, string Key)> labels = new();
    readonly List<(ContentControl Element, string Key)> captions = new();
    readonly List<(StyledElement Element, string Key)> names = new();
    CodeColorizer? colorizer;
    CancellationTokenSource? renderingCancellation;
    Bitmap? previewBitmap;
    int revision, renderedRevision = -1;
    bool isClosed, isRendering, exportRunning, fitWidth = true, ready, refreshingCaptions;
    string? statusKey, appliedOptions;
    object?[] statusValues = Array.Empty<object?>();

    /// <summary>The current preview, returned only by the explicit import action.</summary>
    public Bitmap? Result { get; private set; }

    public CodeImageWindow(string? initialCode = null)
    {
        if (!string.IsNullOrEmpty(initialCode)) CodeImageLimits.ValidateText(initialCode);
        // AvaloniaEdit's control templates, scoped to this window.
        Styles.Add(new StyleInclude(new Uri("avares://SnipFlow/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Title = I18n.T("SnipFlow · 程式碼長圖");
        Width = 1160; Height = 780; MinWidth = 900; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; WindowDecorations = WindowDecorations.None; CanResize = true;
        if (Application.Current?.TryGetResource("SnipFlowIcon", null, out var icon) == true && icon is WindowIcon windowIcon)
            Icon = windowIcon;
        WindowBoundsService.Attach(this);
        WindowAppearance.Apply(this);
        WindowChrome.EnableResizeBorder(this);

        InitializeOptions();
        var layout = new Grid { RowDefinitions = new RowDefinitions("48,Auto,*,Auto") };
        layout.Children.Add(BuildCaption());
        var options = BuildOptions(); Grid.SetRow(options, 1); layout.Children.Add(options);
        var body = BuildBody(); Grid.SetRow(body, 2); layout.Children.Add(body);
        var footer = BuildFooter(); Grid.SetRow(footer, 3); layout.Children.Add(footer);
        Content = new Border { Background = ThemeService.Brush("Bg"), BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(1), Child = layout };

        code.Options.IndentationSize = 4;
        code.Options.ConvertTabsToSpaces = false;
        code.Options.EnableHyperlinks = false;
        code.Options.EnableEmailHyperlinks = false;
        code.Options.EnableRectangularSelection = false;
        code.Options.EnableTextDragDrop = false;
        DragDrop.SetAllowDrop(code, false);
        DragDrop.SetAllowDrop(code.TextArea, false);
        code.Document.UndoStack.SizeLimit = 32;
        code.TextChanged += (_, _) => { if (ready) MarkDirty(); };
        code.TextArea.TextEntering += (_, e) => { if (e.Text is { } value && !CanInsert(value)) e.Handled = true; };
        // Run before AvaloniaEdit's own bindings, like the WPF preview command handlers.
        foreach (var command in new[] { ApplicationCommands.Paste, EditingCommands.EnterParagraphBreak, EditingCommands.EnterLineBreak, EditingCommands.TabForward })
            code.TextArea.CommandBindings.Insert(0, new RoutedCommandBinding(command, EditCommand, null!));
        foreach (var control in new[] { language, font, size, theme }) control.SelectionChanged += (_, _) => OptionsChanged();
        width.TextChanged += (_, _) => OptionsChanged(); startingLine.TextChanged += (_, _) => OptionsChanged();
        wrap.IsCheckedChanged += (_, _) => OptionsChanged();
        numbers.IsCheckedChanged += (_, _) => OptionsChanged();
        render.Click += async (_, _) => await RenderPreviewAsync();
        copy.Click += async (_, _) => await CopyPreviewAsync();
        save.Click += async (_, _) => await SavePreviewAsync();
        import.Click += (_, _) => ImportPreview();
        previewScroll.SizeChanged += (_, _) => ApplyPreviewZoom();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            // Escape closes the window unless a drop-down or AvaloniaEdit's search box (Ctrl+F) is open.
            if (e.Key == Key.Escape && !new[] { language, font, size, theme }.Any(box => box.IsDropDownOpen) && code.SearchPanel is not { IsOpened: true })
            { e.Handled = true; Close(); }
        }, RoutingStrategies.Tunnel);
        I18n.Changed += LanguageChanged;
        Closed += (_, _) =>
        {
            isClosed = true; renderingCancellation?.Cancel();
            I18n.Changed -= LanguageChanged;
        };

        if (!string.IsNullOrEmpty(initialCode))
        {
            code.Text = initialCode; code.Document.UndoStack.ClearAll();
        }
        ready = true; ApplyEditorOptions(); ApplyLanguage(); RefreshActions();
        ShowStatus("貼上程式碼後產生預覽；所有處理都在本機完成。");
        Opened += async (_, _) =>
        {
            var workArea = WindowBoundsService.GetWorkArea(this);
            MinWidth = Math.Min(MinWidth, workArea.Width); MinHeight = Math.Min(MinHeight, workArea.Height);
            if (Width > workArea.Width || Height > workArea.Height)
            {
                Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
                var scaling = WindowBoundsService.GetScaling(this);
                Position = new PixelPoint((int)Math.Round((workArea.X + (workArea.Width - Width) / 2) * scaling),
                    (int)Math.Round((workArea.Y + (workArea.Height - Height) / 2) * scaling));
            }
            if (!string.IsNullOrWhiteSpace(code.Text)) await RenderPreviewAsync();
            if (!isClosed) code.TextArea.Focus();
        };
    }

    void InitializeOptions()
    {
        language.Items.Add(Item("純文字", null, localize: true));
        foreach (var entry in new[]
        {
            ("C#", "C#"), ("C++", "C++"), ("Python", "Python"), ("JavaScript", "JavaScript"),
            ("JSON", "Json"), ("XML", "XML"), ("HTML", "HTML"), ("PowerShell", "PowerShell"),
            ("SQL", "TSQL"), ("Java", "Java"), ("CSS", "CSS"), ("VB", "VB"), ("Markdown", "MarkDown")
        })
            if (HighlightingManager.Instance.GetDefinition(entry.Item2) != null)
                language.Items.Add(Item(entry.Item1, entry.Item2));
        language.SelectedIndex = 1;
        // Windows keeps Cascadia Mono / Consolas / Courier New; Linux lists the installed monospaced fonts.
        foreach (var name in CodeFonts.Choices) font.Items.Add(Item(name, name));
        font.SelectedIndex = Math.Max(0, CodeFonts.Choices.ToList().IndexOf(CodeFonts.DefaultFont));
        foreach (var value in new[] { 12, 14, 16, 18, 20, 24, 28, 32 }) size.Items.Add(Item(value.ToString(CultureInfo.InvariantCulture), value));
        size.SelectedIndex = 3;
        theme.Items.Add(Item("深色", true, localize: true)); theme.Items.Add(Item("淺色", false, localize: true)); theme.SelectedIndex = 0;
    }

    ComboBoxItem Item(string text, object? value, bool localize = false)
    {
        var item = new ComboBoxItem { Content = localize ? I18n.T(text) : text, Tag = value };
        if (localize) captions.Add((item, text));
        return item;
    }

    Control BuildCaption()
    {
        var caption = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Brushes.Transparent };
        caption.Children.Add(Label(new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }, "程式碼長圖"));
        var close = new Button { Content = Icons.Create(IconKind.Dismiss, 12), Width = 42, Height = 34, Margin = new Thickness(0, 6, 7, 6), IsCancel = true }.WithTheme("WindowControl");
        SetAccessibleName(close, "關閉");
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); caption.Children.Add(close);
        WindowChrome.EnableTitleBarDrag(this, caption);
        return caption;
    }

    Control BuildOptions()
    {
        var panel = new WrapPanel { Margin = new Thickness(20, 8, 8, 14) };
        AddOption(panel, "程式語言", language, 132); AddOption(panel, "等寬字型", font, PlatformInfo.IsWindows ? 148 : 196);
        AddOption(panel, "字級", size, 64); AddOption(panel, "圖片寬度 (px)", width, 102);
        AddOption(panel, "配色", theme, 85); AddOption(panel, "起始行號", startingLine, 86);
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 24, 0, 0) };
        ContentText(wrap, "自動換行"); ContentText(numbers, "行號"); wrap.Margin = new Thickness(0, 0, 16, 0);
        toggles.Children.Add(wrap); toggles.Children.Add(numbers); panel.Children.Add(toggles);
        return new Border { BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = panel };
    }

    void AddOption(Panel panel, string title, Control control, double controlWidth)
    {
        var column = new StackPanel { Width = controlWidth, Margin = new Thickness(0, 0, 12, 4) };
        column.Children.Add(Label(new TextBlock { Foreground = ThemeService.Brush("Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) }, title));
        control.MinHeight = 34; control.HorizontalAlignment = HorizontalAlignment.Stretch; SetAccessibleName(control, title);
        if (control is TextBox box) { box.VerticalContentAlignment = VerticalAlignment.Center; box.Padding = new Thickness(9, 5, 9, 5); box.MaxLength = 7; }
        column.Children.Add(control); panel.Children.Add(column);
    }

    Control BuildBody()
    {
        var body = new Grid { Margin = new Thickness(20, 16, 20, 12), ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var input = new Grid { RowDefinitions = new RowDefinitions("36,*") };
        var inputBar = new DockPanel();
        var paste = ActionButton("貼上程式碼"); paste.Padding = new Thickness(10, 4, 10, 4); paste.Click += (_, _) => PasteCode();
        DockPanel.SetDock(paste, Dock.Right); inputBar.Children.Add(paste);
        inputBar.Children.Add(Label(new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, "程式碼")); input.Children.Add(inputBar);
        code.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; code.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        code.Padding = new Thickness(12); code.BorderThickness = new Thickness(0);
        SetAccessibleName(code, "程式碼");
        var codeBorder = new Border { BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = code, ClipToBounds = true };
        Grid.SetRow(codeBorder, 1); input.Children.Add(codeBorder); body.Children.Add(input);

        var output = new Grid { RowDefinitions = new RowDefinitions("36,*") };
        var outputBar = new DockPanel();
        var actual = ActionButton("原始大小"); actual.Padding = new Thickness(8, 3, 8, 3); actual.Click += (_, _) => { fitWidth = false; ApplyPreviewZoom(); };
        var fit = ActionButton("符合寬度"); fit.Padding = new Thickness(8, 3, 8, 3); fit.Click += (_, _) => { fitWidth = true; ApplyPreviewZoom(); };
        DockPanel.SetDock(actual, Dock.Right); DockPanel.SetDock(fit, Dock.Right); outputBar.Children.Add(actual); outputBar.Children.Add(fit);
        dimensions.Foreground = ThemeService.Brush("Muted"); dimensions.FontSize = 11; dimensions.Margin = new Thickness(10, 0, 0, 0); dimensions.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(dimensions, Dock.Right); outputBar.Children.Add(dimensions);
        outputBar.Children.Add(Label(new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, "預覽")); output.Children.Add(outputBar);
        var previewHost = new Grid { Background = ThemeService.Brush("Panel") };
        previewScroll.Content = preview; previewScroll.Padding = new Thickness(12); previewHost.Children.Add(previewScroll);
        emptyPreview.HorizontalAlignment = HorizontalAlignment.Center; emptyPreview.VerticalAlignment = VerticalAlignment.Center;
        emptyPreview.Foreground = ThemeService.Brush("Muted"); emptyPreview.IsHitTestVisible = false; Label(emptyPreview, "產生預覽後顯示圖片"); previewHost.Children.Add(emptyPreview);
        var previewBorder = new Border { BorderBrush = ThemeService.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = previewHost, ClipToBounds = true };
        Grid.SetRow(previewBorder, 1); output.Children.Add(previewBorder); Grid.SetColumn(output, 2); body.Children.Add(output);
        return body;
    }

    Control BuildFooter()
    {
        var footer = new Grid { Margin = new Thickness(20, 0, 20, 18), RowDefinitions = new RowDefinitions("Auto,Auto") };
        status.TextWrapping = TextWrapping.Wrap; status.FontSize = 12; status.Margin = new Thickness(0, 0, 0, 12); footer.Children.Add(status);
        var actions = new DockPanel();
        var outputs = new StackPanel { Orientation = Orientation.Horizontal };
        ContentText(copy, "複製圖片"); ContentText(save, "儲存 PNG"); ContentText(import, "加入編輯器");
        import.WithTheme("Primary"); copy.WithTheme("QuietButton"); save.WithTheme("QuietButton");
        foreach (var action in new[] { copy, save, import }) { action.Margin = new Thickness(8, 0, 0, 0); outputs.Children.Add(action); }
        DockPanel.SetDock(outputs, Dock.Right); actions.Children.Add(outputs);
        ContentText(render, "產生預覽"); render.WithTheme("NavigationButton"); render.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(render);
        Grid.SetRow(actions, 1); footer.Children.Add(actions); return footer;
    }

    void EditCommand(object? sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste) { e.Handled = true; PasteCode(); }
        else if (e.Command == EditingCommands.EnterParagraphBreak || e.Command == EditingCommands.EnterLineBreak)
        {
            e.Handled = true;
            var line = code.Document.GetLineByOffset(code.CaretOffset);
            var text = code.Document.GetText(line.Offset, Math.Min(line.Length, code.CaretOffset - line.Offset));
            var indent = new string(text.TakeWhile(c => c is ' ' or '\t').ToArray());
            InsertText(Environment.NewLine + indent);
        }
        else if (e.Command == EditingCommands.TabForward)
        {
            e.Handled = true;
            if (code.SelectionLength == 0) { InsertText("\t"); return; }
            var first = code.Document.GetLineByOffset(code.SelectionStart);
            var last = code.Document.GetLineByOffset(code.SelectionStart + code.SelectionLength);
            if (last.Offset == code.SelectionStart + code.SelectionLength && last.LineNumber > first.LineNumber) last = last.PreviousLine!;
            var replacement = new StringBuilder();
            for (var line = first; line != null && line.LineNumber <= last.LineNumber; line = line.NextLine)
                replacement.Append('\t').Append(code.Document.GetText(line.Offset, line.TotalLength));
            int end = last.Offset + last.TotalLength;
            if (!ValidateCandidate(code.Text.Remove(first.Offset, end - first.Offset).Insert(first.Offset, replacement.ToString()))) return;
            code.Document.Replace(first.Offset, end - first.Offset, replacement.ToString());
            code.Select(first.Offset, replacement.Length);
        }
    }

    async void PasteCode()
    {
        if (isClosed || exportRunning) return;
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            var value = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (isClosed || exportRunning) return;
            if (string.IsNullOrEmpty(value)) { ShowStatus("剪貼簿沒有可貼上的文字。"); return; }
            InsertText(value); code.TextArea.Focus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!isClosed) ShowStatus("剪貼簿暫時無法讀取，請在視窗內貼上程式碼。", error: true);
        }
    }

    bool CanInsert(string value)
        => ValidateCandidate(value.Length > CodeImageLimits.MaxCharacters ? value : code.Text.Remove(code.SelectionStart, code.SelectionLength).Insert(code.SelectionStart, value));

    bool ValidateCandidate(string text)
    {
        try { CodeImageLimits.ValidateText(text); return true; }
        catch (ArgumentException ex) { ShowRawError(ex.Message); return false; }
    }

    void InsertText(string value)
    {
        if (!CanInsert(value)) return;
        int start = code.SelectionStart;
        code.SelectedText = value; code.Select(start + value.Length, 0);
    }

    void OptionsChanged()
    {
        // Avalonia raises TextBox.TextChanged (and some selection events) later on the dispatcher,
        // including for the initial values; only a real change invalidates the preview.
        if (!ready || refreshingCaptions || OptionsSnapshot() == appliedOptions) return;
        ApplyEditorOptions(); MarkDirty();
    }

    string OptionsSnapshot() => string.Join("\u001f", (language.SelectedItem as ComboBoxItem)?.Tag, (font.SelectedItem as ComboBoxItem)?.Tag,
        (size.SelectedItem as ComboBoxItem)?.Tag, (theme.SelectedItem as ComboBoxItem)?.Tag, width.Text, startingLine.Text, wrap.IsChecked, numbers.IsChecked);

    void ApplyEditorOptions()
    {
        appliedOptions = OptionsSnapshot();
        var dark = (theme.SelectedItem as ComboBoxItem)?.Tag as bool? ?? true;
        var palette = new CodePalette(dark);
        code.Background = palette.Background; code.Foreground = palette.Foreground;
        code.LineNumbersForeground = palette.LineNumbers;
        code.FontFamily = CodeFonts.Family((font.SelectedItem as ComboBoxItem)?.Tag as string ?? CodeFonts.DefaultFont);
        code.FontSize = Convert.ToDouble((size.SelectedItem as ComboBoxItem)?.Tag ?? 18, CultureInfo.InvariantCulture);
        code.ShowLineNumbers = numbers.IsChecked == true; code.WordWrap = wrap.IsChecked == true;
        // Keep the gap between the numbers, the dotted separator and the text of the WPF editor.
        foreach (var margin in code.TextArea.LeftMargins)
            if (DottedLineMargin.IsDottedLineMargin(margin)) margin.Margin = new Thickness(2, 0, 4, 0);
        code.TextArea.SelectionBrush = new SolidColorBrush(dark ? Color.FromRgb(64, 78, 100) : Color.FromRgb(208, 227, 246));
        code.TextArea.SelectionForeground = palette.Foreground;
        code.TextArea.CaretBrush = palette.Foreground;
        startingLine.IsEnabled = numbers.IsChecked == true;
        if (colorizer != null) code.TextArea.TextView.LineTransformers.Remove(colorizer);
        colorizer = null;
        if ((language.SelectedItem as ComboBoxItem)?.Tag is string name && HighlightingManager.Instance.GetDefinition(name) is { } definition)
        {
            colorizer = new CodeColorizer(definition, palette);
            code.TextArea.TextView.LineTransformers.Add(colorizer);
        }
    }

    void MarkDirty()
    {
        revision++; renderingCancellation?.Cancel();
        preview.Opacity = .4;
        ShowStatus("內容已變更，請重新產生預覽。"); RefreshActions();
    }

    CodeImageOptions ReadOptions()
    {
        if (!int.TryParse(width.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixels) || pixels is < 640 or > 4096)
            throw new ArgumentException(I18n.T("圖片寬度須為 640–4096 px。"));
        bool showNumbers = numbers.IsChecked == true;
        int first = 1;
        if (showNumbers && (!int.TryParse(startingLine.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out first) || first is < 1 or > 1_000_000))
            throw new ArgumentException(I18n.T("起始行號須為 1–1,000,000。"));
        return new CodeImageOptions((language.SelectedItem as ComboBoxItem)?.Tag as string,
            (font.SelectedItem as ComboBoxItem)?.Tag as string ?? CodeFonts.DefaultFont, code.FontSize, pixels,
            wrap.IsChecked == true, showNumbers, first, (theme.SelectedItem as ComboBoxItem)?.Tag as bool? ?? true);
    }

    async Task RenderPreviewAsync()
    {
        if (isClosed || isRendering || exportRunning) return;
        isRendering = true; int snapshotRevision = revision;
        using var cancellation = new CancellationTokenSource(); renderingCancellation = cancellation;
        ShowStatus("正在產生預覽…"); RefreshActions();
        try
        {
            var image = await CodeImageRenderer.RenderAsync(code.Text, ReadOptions(), cancellation.Token);
            if (isClosed || snapshotRevision != revision || cancellation.IsCancellationRequested) return;
            previewBitmap = image; renderedRevision = snapshotRevision;
            preview.Source = image; preview.Opacity = 1;
            emptyPreview.IsVisible = false;
            dimensions.Text = $"{image.PixelSize.Width:N0} × {image.PixelSize.Height:N0} px";
            ApplyPreviewZoom(); previewScroll.ScrollToHome();
            ShowStatus("預覽已更新。");
        }
        catch (OperationCanceledException) { if (!isClosed && snapshotRevision == revision) ShowStatus("已取消產生預覽。"); }
        catch (Exception ex) { if (!isClosed) { SettingsStore.Log(ex); ShowRawError(ex.Message); } }
        finally
        {
            if (ReferenceEquals(renderingCancellation, cancellation)) renderingCancellation = null;
            isRendering = false; if (!isClosed) RefreshActions();
        }
    }

    bool CurrentPreview => previewBitmap != null && renderedRevision == revision && !isRendering;

    void RefreshActions()
    {
        render.IsEnabled = !isRendering && !exportRunning;
        copy.IsEnabled = save.IsEnabled = import.IsEnabled = CurrentPreview && !exportRunning;
    }

    void ApplyPreviewZoom()
    {
        if (previewBitmap == null || isClosed) return;
        var pixels = previewBitmap.PixelSize;
        double zoom = fitWidth && previewScroll.Bounds.Width > 32 ? Math.Min(1, (previewScroll.Bounds.Width - 42) / pixels.Width) : 1;
        preview.Width = pixels.Width * Math.Max(.05, zoom);
        preview.Height = pixels.Height * Math.Max(.05, zoom);
    }

    async Task CopyPreviewAsync()
    {
        if (!CurrentPreview || exportRunning) return;
        var image = previewBitmap!; int snapshotRevision = revision;
        exportRunning = true; RefreshActions();
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("The clipboard is not available.");
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (isClosed || snapshotRevision != revision) return;
                try { await clipboard.SetBitmapAsync(image); ShowStatus("圖片已複製。"); return; }
                catch (Exception ex) when (attempt < 3 && ex is not OutOfMemoryException) { await Task.Delay(120); }
            }
        }
        catch (Exception ex) { if (!isClosed) { SettingsStore.Log(ex); ShowStatus("圖片複製失敗，請再試一次。", error: true); } }
        finally { exportRunning = false; if (!isClosed) RefreshActions(); }
    }

    async Task SavePreviewAsync()
    {
        if (!CurrentPreview || exportRunning) return;
        var image = previewBitmap!; int snapshotRevision = revision;
        var path = FileDialogs.SaveFile(this, I18n.T("儲存程式碼長圖"), "PNG (*.png)|*.png",
            $"SnipFlow_Code_{DateTime.Now:yyyyMMdd_HHmmss}.png", ".png");
        if (path == null || isClosed || snapshotRevision != revision) return;
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        { ShowStatus("請使用 .png 副檔名儲存圖片。", error: true); return; }
        exportRunning = true; RefreshActions();
        try
        {
            await Task.Run(() => SaveAtomic(image, path));
            if (!isClosed && snapshotRevision == revision) ShowStatus("圖片已儲存。");
        }
        catch (Exception ex) { if (!isClosed) { SettingsStore.Log(ex); ShowRawError(I18n.F("圖片儲存失敗：{0}", ex.Message)); } }
        finally { exportRunning = false; if (!isClosed) RefreshActions(); }
    }

    static void SaveAtomic(Bitmap image, string path)
    {
        var target = Path.GetFullPath(path);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) ImageTools.SavePng(image, stream);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    void ImportPreview()
    {
        if (!CurrentPreview || exportRunning) return;
        Result = previewBitmap; DialogResult = true;
    }

    void ShowStatus(string key, bool error = false, params object?[] values)
    {
        statusKey = key; statusValues = values;
        status.Text = I18n.F(key, values); status.Foreground = ThemeService.Brush(error ? "Error" : "Muted");
    }

    void ShowRawError(string text)
    {
        statusKey = null; status.Text = text; status.Foreground = ThemeService.Brush("Error");
    }

    void LanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(ApplyLanguage); return; }
        ApplyLanguage();
    }

    void ApplyLanguage()
    {
        if (isClosed) return;
        Title = I18n.T("SnipFlow · 程式碼長圖");
        foreach (var (element, key) in labels) element.Text = I18n.T(key);
        foreach (var (element, key) in captions) element.Content = I18n.T(key);
        // A combo box shows a copy of the selected caption; reselect so it shows the new language.
        refreshingCaptions = true;
        try
        {
            foreach (var box in new[] { language, theme })
            {
                var index = box.SelectedIndex;
                box.SelectedIndex = -1; box.SelectedIndex = index;
            }
        }
        finally { refreshingCaptions = false; }
        foreach (var (element, key) in names) AutomationProperties.SetName(element, I18n.T(key));
        if (statusKey != null) status.Text = I18n.F(statusKey, statusValues);
    }

    TextBlock Label(TextBlock element, string key) { labels.Add((element, key)); element.Text = I18n.T(key); return element; }
    void ContentText(ContentControl element, string key) { captions.Add((element, key)); element.Content = I18n.T(key); SetAccessibleName(element, key); }
    void SetAccessibleName(StyledElement element, string key) { names.Add((element, key)); AutomationProperties.SetName(element, I18n.T(key)); }
    Button ActionButton(string key) { var button = new Button().WithTheme("QuietButton"); ContentText(button, key); return button; }
}

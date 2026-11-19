using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnipFlow.Imaging;
using SnipFlow.Services;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    // Load the actual idle/hover QuietButton scalar theme, based on the real Fluent template.
    // A missing or newly complex setter fails this fixture rather than silently using a replacement.
    static void LoadEditingButtonTheme(string source)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var node = XDocument.Load(source).Descendants().Single(node => node.Name.LocalName == "ControlTheme" && (string?)node.Attribute(xaml + "Key") == "QuietButton");
        Check((string?)node.Attribute("TargetType") == "Button" && (string?)node.Attribute("BasedOn") == "{StaticResource {x:Type Button}}", "QuietButton source contract changed.");
        Check(Application.Current!.TryGetResource(typeof(Button), Application.Current.ActualThemeVariant, out var raw) && raw is ControlTheme, "Actual Fluent Button theme unavailable.");
        var theme = new ControlTheme { TargetType = typeof(Button), BasedOn = (ControlTheme)raw! };
        foreach (var setter in node.Elements().Where(element => element.Name.LocalName == "Setter")) {
            Check((string?)setter.Attribute("Property") == "Foreground" && (string?)setter.Attribute("Value") == "{DynamicResource Muted}", "Unreviewed QuietButton setter.");
            theme.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, ThemeService.Brush("Muted")));
        }
        foreach (var styleNode in node.Elements().Where(element => element.Name.LocalName == "Style")) {
            Check((string?)styleNode.Attribute("Selector") == "^:pointerover", "Unreviewed QuietButton state.");
            var style = new Style(selector => selector.Nesting().Class(":pointerover"));
            foreach (var setter in styleNode.Elements()) {
                Check((string?)setter.Attribute("Property") == "Foreground" && (string?)setter.Attribute("Value") == "{DynamicResource Ink}", "Unreviewed QuietButton hover setter.");
                style.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, ThemeService.Brush("Ink")));
            }
            theme.Children.Add(style);
        }
        Application.Current.Resources["QuietButton"] = theme;
    }

    static void MaterializeEditingPalette(ThemeVariantScope scope, double width)
    {
        var initialized = new HashSet<Control>();
        for (var pass = 0; pass < 8; pass++) {
            foreach (var control in scope.GetVisualDescendants().OfType<Control>().Prepend(scope).ToArray()) {
                if (initialized.Add(control)) { control.BeginInit(); control.EndInit(); }
                if (control is TemplatedControl templated && templated.Template is null && templated.Theme is null
                    && Application.Current!.TryGetResource(control.StyleKey, scope.ActualThemeVariant, out var resource) && resource is ControlTheme theme) templated.Theme = theme;
                control.ApplyStyling(); control.ApplyTemplate();
                if (control is ContentPresenter presenter) presenter.UpdateChild();
                control.InvalidateMeasure(); control.InvalidateArrange();
            }
            scope.Measure(new Size(width, double.PositiveInfinity)); scope.Arrange(new Rect(0, 0, width, scope.DesiredSize.Height));
        }
        Dispatcher.UIThread.RunJobs(); scope.Measure(new Size(width, double.PositiveInfinity)); scope.Arrange(new Rect(0, 0, width, scope.DesiredSize.Height));
    }

    static int WriteEditingPaletteVisuals(string[] args)
    {
        var fixtures = new List<object>(); var success = false; string? error = null; string? source = null;
        try {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Visual fixture loaded live preferences."); var sourceIndex = Array.IndexOf(args, "--app-theme-source");
            source = Path.GetFullPath(sourceIndex >= 0 && sourceIndex + 1 < args.Length ? args[sourceIndex + 1] : Path.Combine(Directory.GetCurrentDirectory(), "src", "App.axaml"));
            LoadFontPickerStyles(source);
            foreach (var dark in new[] { false, true }) foreach (var language in new[] { "zh-TW", "en-US" }) foreach (var width in new[] { 280, 820 }) foreach (var name in new[] { "ArrowPresetBar", "ShapePicker" }) foreach (var filled in name == "ShapePicker" ? new[] { false, true } : new[] { false }) {
                ApplyFontPickerPalette(dark); I18n.SetLanguage(language); LoadEditingButtonTheme(source);
                var palette = (Control)Activator.CreateInstance(Product.GetType("SnipFlow.Editor." + name, true)!)!;
                if (name == "ShapePicker") { Set(palette, "Value", Kind("Star")); Set(palette, "Filled", filled); } else Set(palette, "Value", Style("Arrow", "Arrow", "Small", "Large", "Dash"));
                Call(palette, "RefreshLanguage"); var host = new Border { Padding = new Thickness(8), Background = ThemeService.Brush("Panel"), Child = palette };
                var scope = new ThemeVariantScope { Width = width, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Child = host };
                LoadFontPickerStyles(source, scope.Styles); foreach (var resource in Application.Current!.Resources) scope.Resources[resource.Key] = resource.Value;
                scope.SetValue(TextElement.FontFamilyProperty, (FontFamily)Application.Current.Resources["AppFont"]!);
                var window = XDocument.Load(source).Descendants().Single(element => element.Name.LocalName == "Style" && (string?)element.Attribute("Selector") == "Window");
                scope.SetValue(TextElement.FontSizeProperty, double.Parse(window.Elements().Single(element => (string?)element.Attribute("Property") == "FontSize").Attribute("Value")!.Value, CultureInfo.InvariantCulture));
                scope.SetValue(TextElement.ForegroundProperty, ThemeService.Brush("Ink")); MaterializeEditingPalette(scope, width);
                Check(TopLevel.GetTopLevel(scope) is null && Application.Current.ApplicationLifetime is null && !scope.GetVisualDescendants().OfType<Popup>().Any(popup => popup.IsOpen), "Visual fixture created a native host/popup.");
                var buttons = scope.GetVisualDescendants().OfType<Button>().Where(button => button.Tag is not null).ToArray(); Check(buttons.Length == (name == "ShapePicker" ? CommonShapes.Length : 10), "Actual palette buttons were not materialized.");
                foreach (var button in buttons) {
                    Check(button.Template is not null && button.Bounds.Width > 0 && button.Bounds.Height > 0, "Real Fluent button template missing.");
                    Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), "Palette icon lacks accessible name.");
                    var bounds = new Rect(button.Bounds.Size).TransformToAABB(button.TransformToVisual(scope)!.Value); Check(bounds.Left >= 0 && bounds.Right <= width + .1 && bounds.Bottom <= scope.Bounds.Height + .1, "Wrapped palette button is clipped.");
                    Check(button.GetVisualDescendants().Any(control => control.GetType().Name is "ArrowPreview" or "ShapePreview"), "Actual geometry preview missing.");
                }
                if (width == 280) Check(buttons.Select(button => button.TransformToVisual(scope)!.Value.Transform(new Point()).Y).Distinct().Count() > 1, "Narrow palette did not wrap.");
                var height = (int)Math.Ceiling(scope.Bounds.Height); using var image = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96)); image.Render(scope);
                var filename = $"{name}-{(dark ? "dark" : "light")}-{language}-{width}-{(filled ? "fill" : "outline")}.png"; var path = Path.Combine(Output, filename); ImageTools.SavePng(image, path);
                fixtures.Add(new { filename, name, language, theme = dark ? "dark" : "light", width, height, filled, pngSha256 = Hash(File.ReadAllBytes(path)), names = buttons.Select(AutomationProperties.GetName).ToArray(), buttonBounds = buttons.Select(button => button.Bounds.ToString()).ToArray() });
            }
            // A separate document-rendered contact sheet proves all preview choices have actual vector shapes.
            using var sheet = ImageTools.Render(new PixelSize(960, 680), context => {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 960, 680));
                for (var n = 0; n < CommonShapes.Length; n++) for (var fill = 0; fill < 2; fill++) {
                    var item = VectorShape(CommonShapes[n], fill == 1); var x = 20 + n % 6 * 160; var y = 18 + n / 6 * 220 + fill * 96;
                    Set(item, "Start", new Point(x, y)); Set(item, "End", new Point(x + 125, y + 74)); Call(item, "Draw", context);
                    var label = new FormattedText(CommonShapes[n] + (fill == 1 ? " fill" : ""), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Black); context.DrawText(label, new Point(x, y + 76));
                }
            }); Save("common-shapes-contact-sheet.png", sheet);
            Check(!LazyCreated(lazy), "Visual fixture initialized SettingsStore.Current."); success = true;
        }
        catch (Exception exception) { error = Unwrap(exception).ToString(); }
        var report = new { mode = "editing-palette-visual", success, error, source, appThemeSourceSha256 = source is not null && File.Exists(source) ? Hash(File.ReadAllBytes(source)) : null, productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)), fixtureCount = fixtures.Count, fixtures, method = "Actual standalone product ArrowPresetBar/ShapePicker and annotation geometry renderer; actual Fluent ControlThemes and source-validated App.axaml QuietButton idle/hover setters, font resources and TextBlock styles in ThemeVariantScope; actual ThemeService palette. Finite narrow and normal widths, both languages, both themes, outline/fill. No substitute templates.", limitations = "No Window/MainWindow, open Popup, native focus, pointer hover, keyboard/OS input, full-window layout, DPI or desktop behavior. Standalone palette visuals only." };
        var reportPath = Path.Combine(Output, "editing-palette-visual.json"); File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine("Editing palette visual: " + (success ? "PASS " : "FAIL ") + reportPath); return success ? 0 : 1;
    }
}

using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnipFlow.Services;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    // Read the real application's scalar picker styles without constructing App or running its startup.
    // The source path and SHA are reported: this is never presented as loading all MainWindow styles.
    static void LoadFontPickerStyles(string path, Styles? destination = null)
    {
        var app = Application.Current!; var document = XDocument.Load(path); XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = document.Root!.Elements().Single(node => node.Name.LocalName == "Application.Resources");
        foreach (var element in resources.Elements().Where(node => node.Name.LocalName is "FontFamily" or "CornerRadius")) {
            var key = (string?)element.Attribute(xaml + "Key"); if (key is null) continue;
            app.Resources[key] = element.Name.LocalName == "FontFamily" ? new FontFamily(element.Value.Trim()) : new CornerRadius(double.Parse(element.Value.Trim(), CultureInfo.InvariantCulture));
        }
        destination ??= app.Styles;
        destination.Add(new FluentTheme());
        var styles = document.Root.Elements().Single(node => node.Name.LocalName == "Application.Styles");
        foreach (var node in styles.Elements().Where(node => node.Name.LocalName == "Style" && (string?)node.Attribute("Selector") is "ComboBox" or "ComboBoxItem" or "TextBlock")) {
            var selector = (string)node.Attribute("Selector")!;
            var style = selector switch {
                "ComboBox" => new Style(s => s.Is<ComboBox>()),
                "ComboBoxItem" => new Style(s => s.Is<ComboBoxItem>()),
                _ => new Style(s => s.Is<TextBlock>())
            };
            foreach (var setter in node.Elements().Where(element => element.Name.LocalName == "Setter")) {
                var property = (string?)setter.Attribute("Property"); var value = (string?)setter.Attribute("Value") ?? throw new InvalidDataException("Picker style has a non-scalar setter.");
                style.Setters.Add((selector, property) switch {
                    ("ComboBox", "MinHeight") => new Setter(Control.MinHeightProperty, double.Parse(value, CultureInfo.InvariantCulture)),
                    ("ComboBox", "MaxDropDownHeight") => new Setter(ComboBox.MaxDropDownHeightProperty, double.Parse(value, CultureInfo.InvariantCulture)),
                    ("ComboBox", "CornerRadius") => new Setter(ComboBox.CornerRadiusProperty, new CornerRadius(double.Parse(value, CultureInfo.InvariantCulture))),
                    ("ComboBoxItem", "Padding") => new Setter(ComboBoxItem.PaddingProperty, Thickness.Parse(value)),
                    ("ComboBoxItem", "CornerRadius") => new Setter(ComboBoxItem.CornerRadiusProperty, new CornerRadius(double.Parse(value, CultureInfo.InvariantCulture))),
                    ("TextBlock", "TextWrapping") => new Setter(TextBlock.TextWrappingProperty, Enum.Parse<TextWrapping>(value)),
                    _ => throw new InvalidDataException("Unreviewed picker style setter: " + selector + "." + property)
                });
            }
            destination.Add(style);
        }
    }

    static void ApplyFontPickerPalette(bool dark)
    {
        var type = typeof(ThemeService); var resources = Application.Current!.Resources;
        var palette = ((string Key, string Dark, string Light)[])type.GetField("Palette", Any)!.GetValue(null)!;
        var brushes = (IDictionary)type.GetField("Brushes", Any)!.GetValue(null)!;
        foreach (var entry in palette) {
            var brush = (SolidColorBrush)brushes[entry.Key]!; brush.Color = Color.Parse(dark ? entry.Dark : entry.Light); resources[entry.Key] = brush;
        }
        foreach (var alias in ((string FluentKey, string PaletteKey)[])type.GetField("FluentAliases", Any)!.GetValue(null)!) resources[alias.FluentKey] = brushes[alias.PaletteKey];
        Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    static int WriteFontPickerVisuals(string[] args)
    {
        object report; var fixtures = new List<object>(); var diagnostics = new List<object>(); var success = false;
        try {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Visual mode started with loaded Current.");
            var sourceIndex = Array.IndexOf(args, "--app-theme-source");
            var source = Path.GetFullPath(sourceIndex >= 0 && sourceIndex + 1 < args.Length ? args[sourceIndex + 1] : Path.Combine(Directory.GetCurrentDirectory(), "src", "App.axaml"));
            Check(File.Exists(source), "Actual App.axaml source is required; pass --app-theme-source <path>.");
            LoadFontPickerStyles(source);
            var host = new Border { Height = 52, Padding = new Thickness(8), Background = ThemeService.Brush("Panel") };
            var scope = new ThemeVariantScope { Height = 52, Child = host };
            LoadFontPickerStyles(source, scope.Styles);
            foreach (var resource in Application.Current!.Resources) scope.Resources[resource.Key] = resource.Value;
            scope.SetValue(TextElement.FontFamilyProperty, (FontFamily)Application.Current.Resources["AppFont"]!);
            var windowStyles = XDocument.Load(source).Descendants().Single(element => element.Name.LocalName == "Style" && (string?)element.Attribute("Selector") == "Window");
            var windowFontSize = windowStyles.Elements().Single(element => (string?)element.Attribute("Property") == "FontSize").Attribute("Value")!.Value;
            scope.SetValue(TextElement.FontSizeProperty, double.Parse(windowFontSize, CultureInfo.InvariantCulture));
            scope.SetValue(TextElement.ForegroundProperty, ThemeService.Brush("Ink"));
            var comboStyles = XDocument.Load(source).Descendants().Single(element => element.Name.LocalName == "Style" && (string?)element.Attribute("Selector") == "ComboBox");
            var expectedCornerRadius = new CornerRadius(double.Parse(comboStyles.Elements().Single(element => (string?)element.Attribute("Property") == "CornerRadius").Attribute("Value")!.Value, CultureInfo.InvariantCulture));
            var values = new[] { "", PreferredFontFamily, "SnipFlow Very Long Missing Font Family " + new string('W', 75) };
            foreach (var dark in new[] { false, true }) foreach (var language in new[] { "zh-TW", "en-US" }) foreach (var width in new[] { 120, 150, 180 }) for (var valueIndex = 0; valueIndex < values.Length; valueIndex++) {
                ApplyFontPickerPalette(dark); I18n.SetLanguage(language);
                foreach (var resource in Application.Current!.Resources) scope.Resources[resource.Key] = resource.Value;
                scope.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var picker = (Control)Activator.CreateInstance(FontPickerType)!; Set(picker, "Value", values[valueIndex]); Call(picker, "RefreshLanguage");
                var combo = (ComboBox)FontPickerType.GetFields(Any).Single(field => field.FieldType == typeof(ComboBox)).GetValue(picker)!;
                // Exercise the actual shared control's 120-180px flexible slots without constructing MainWindow.
                scope.Width = width + 16; host.Width = width + 16; host.Child = picker;
                // An unattached ContentPresenter defers its child by design. UpdateChild is Avalonia's
                // real unit-test seam; materialize the actual templates, never substitute controls.
                var initialized = new HashSet<Control>();
                for (var pass = 0; pass < 8; pass++) {
                    foreach (var control in scope.GetVisualDescendants().OfType<Control>().Prepend(scope).ToArray()) {
                        if (initialized.Add(control)) { control.BeginInit(); control.EndInit(); }
                        if (control is TemplatedControl themed && themed.Template is null
                            && Application.Current!.TryGetResource(control.StyleKey, Application.Current.ActualThemeVariant, out var resource)
                            && resource is ControlTheme theme) themed.Theme = theme;
                        control.ApplyStyling(); control.ApplyTemplate();
                        if (control is ContentPresenter presenter) presenter.UpdateChild();
                        control.InvalidateMeasure(); control.InvalidateArrange();
                    }
                    scope.Measure(new Size(width + 16, 52)); scope.Arrange(new Rect(0, 0, width + 16, 52));
                }
                Dispatcher.UIThread.RunJobs(); scope.Measure(new Size(width + 16, 52)); scope.Arrange(new Rect(0, 0, width + 16, 52));
                object LayoutState(Control control) => new { type = control.GetType().FullName, name = control.Name, bounds = control.Bounds.ToString(), desiredSize = control.DesiredSize.ToString(), control.IsMeasureValid, control.IsArrangeValid, control.IsEffectivelyVisible, hasTemplate = control is TemplatedControl templated ? templated.Template is not null : (bool?)null, cornerRadius = control is TemplatedControl rounded ? rounded.CornerRadius.ToString() : null, foreground = control is TemplatedControl { Foreground: ISolidColorBrush foreground } ? ColorHex(foreground.Color) : control is TextBlock { Foreground: ISolidColorBrush textColor } ? ColorHex(textColor.Color) : null };
                diagnostics.Add(new { theme = dark ? "dark" : "light", language, slotWidth = width, requestedFamily = values[valueIndex], host = LayoutState(host), picker = LayoutState(picker), combo = LayoutState(combo), descendants = host.GetVisualDescendants().OfType<Control>().Select(LayoutState).ToArray() });
                Check(!combo.IsDropDownOpen && TopLevel.GetTopLevel(host) is null && Application.Current!.ApplicationLifetime is null, "Visual mode opened a native host/popup.");
                Check(picker.Bounds.Left >= 8 - .01 && picker.Bounds.Right <= host.Bounds.Width - 8 + .01 && picker.Bounds.Height > 0, "Picker exceeds its measured slot.");
                Check(combo.Bounds.Width > 0 && combo.Bounds.Width <= picker.Bounds.Width + .01 && combo.Bounds.Height >= 28, "ComboBox has invalid bounds.");
                Check(combo.CornerRadius == expectedCornerRadius, "Actual ComboBox did not receive the real App.axaml corner radius.");
                Check(combo.Foreground is ISolidColorBrush ink && ink.Color == ThemeService.GetColor("Ink"), "Actual ComboBox foreground does not match the real theme palette.");
                Check(!scope.GetVisualDescendants().OfType<Popup>().Any(popup => popup.IsOpen), "Visual mode opened a popup.");
                var labels = combo.GetVisualDescendants().OfType<TextBlock>().Where(label => label.IsEffectivelyVisible && !string.IsNullOrEmpty(label.Text)).ToArray();
                Check(labels.Length > 0, "Real Fluent selection template did not produce a visible label; no visual proof available.");
                Check(labels.Any(label => label.TextTrimming == TextTrimming.CharacterEllipsis), "Actual font selection label does not use ellipsis.");
                using var image = new RenderTargetBitmap(new PixelSize(width + 16, 52), new Vector(96, 96)); image.Render(scope);
                var filename = $"font-picker-{(dark ? "dark" : "light")}-{language}-{width}-{valueIndex}.png"; var path = Path.Combine(Output, filename); ImageTools.SavePng(image, path);
                fixtures.Add(new { filename, theme = dark ? "dark" : "light", language, slotWidth = width, requestedFamily = values[valueIndex], pickerBounds = picker.Bounds.ToString(), comboBounds = combo.Bounds.ToString(), computedCornerRadius = combo.CornerRadius.ToString(), computedForeground = ColorHex(((ISolidColorBrush)combo.Foreground!).Color), expectedPaletteForeground = ColorHex(ThemeService.GetColor("Ink")), labelTexts = labels.Select(label => label.Text).ToArray(), pngSha256 = Hash(File.ReadAllBytes(path)) });
            }
            Check(!LazyCreated(lazy), "Visual mode initialized Current."); success = true;
            report = new { mode = "font-picker-visual", fullRegressionRun = false, success, fixtureCount = fixtures.Count, productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)), appThemeSource = source, appThemeSourceSha256 = Hash(File.ReadAllBytes(source)), themeMethod = "Actual FluentTheme plus real App.axaml FontFamily/CornerRadius resources and ComboBox/ComboBoxItem/TextBlock scalar setters in a real ThemeVariantScope. The root inherits the source Window font size and AppFont/Ink context. Real ThemeService palette/brush/Fluent alias data is applied directly without Initialize/Apply or settings. Unattached controls use BeginInit/EndInit, real Fluent ControlTheme lookup, ApplyStyling/ApplyTemplate and ContentPresenter.UpdateChild to materialize actual templates, then Measure/Arrange; no substitute controls or templates.", lazyCurrentBefore = false, lazyCurrentAfter = false, fixtures, diagnostics, limitations = "Closed standalone TextFontPicker only. No Window or open Popup, MainWindow toolbar, desktop input, native popup placement, keyboard focus, IME, or full-window narrow layout was exercised. Other App.axaml styles are not loaded." };
        }
        catch (Exception error) { report = new { mode = "font-picker-visual", fullRegressionRun = false, success, error = Unwrap(error).ToString(), fixtures, diagnostics }; }
        var reportPath = Path.Combine(Output, "font-picker-visual.json"); File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Font picker visual: " + (success ? "PASS " : "FAIL ") + reportPath); return success ? 0 : 1;
    }
}

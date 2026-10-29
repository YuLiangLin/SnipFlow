using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

public sealed class CropTestApplication : Application
{
    public override void Initialize()
    {
        Resources["AppFont"] = new FontFamily("Segoe UI, Microsoft JhengHei UI, Noto Sans CJK TC, sans-serif");
        Resources["MonoFont"] = new FontFamily("Consolas, DejaVu Sans Mono, monospace");
        // Panel state tests need the named theme key, but do not render or validate its template.
        Resources["QuietButton"] = new Avalonia.Styling.ControlTheme { TargetType = typeof(Avalonia.Controls.Button) };
    }
}

internal static partial class Program
{
    internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static Assembly Product => typeof(EditorView).Assembly;
    internal static Type ItemType => Product.GetType("SnipFlow.Editor.AnnotationItem", true)!;
    internal static Type? CropGeometryType => Product.GetType("SnipFlow.Editor.ImageCropGeometry");
    internal static string Output = "";
    static readonly List<object> Results = new();
    static int passed, failed;

    [STAThread]
    static int Main(string[] args)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        Output = Path.GetFullPath(outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1]
            : Path.Combine("verification", "image-crop", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
        Directory.CreateDirectory(Output);
        try
        {
            AppBuilder.Configure<CropTestApplication>().UsePlatformDetect().SetupWithoutStarting();
            if (args.Contains("--legacy-arrow-fixtures")) return WriteLegacyArrowFixtures();
            if (args.Contains("--font-picker-visual")) return WriteFontPickerVisuals(args);
            var preferenceProbe = Array.IndexOf(args, "--arrow-preference-probe");
            if (preferenceProbe >= 0)
            {
                if (preferenceProbe + 1 >= args.Length) throw new ArgumentException("--arrow-preference-probe requires a settings file path.");
                return WriteArrowPreferenceProbe(args[preferenceProbe + 1]);
            }
            Case("SNIPFLOW-IMAGE-CROP-001 contract", () => {
                Check(ItemType.GetProperty("ImageCrop", Any)?.PropertyType == typeof(Rect?), "ImageCrop Rect? contract is missing (expected failure on pre-crop assembly).");
                Check(CropGeometryType is not null, "ImageCropGeometry is missing.");
                Check(Application.Current is CropTestApplication && Application.Current.ApplicationLifetime is null, "Unexpected application/lifetime.");
            });
            Case("SNIPFLOW-IMAGE-CROP-ZOOM-002 zoom contract", () => {
                Check(typeof(EditorView).GetMethod("SetImageCropZoom", Any, null, new[] { typeof(double) }, null) is not null, "SetImageCropZoom(double) is missing (expected failure on 0.8.1).");
                Check(typeof(EditorView).GetProperty("ImageCropZoom", Any)?.PropertyType == typeof(double), "ImageCropZoom double getter is missing.");
            });
            Case("SNIPFLOW-DOUBLE-ARROW-003 contract", () => {
                Check(ItemType.GetProperty("DoubleEndedArrow", Any)?.PropertyType == typeof(bool), "AnnotationItem.DoubleEndedArrow is missing (expected failure on 0.8.2).");
                Check(typeof(EditorView).GetProperty("DoubleEndedArrow", Any)?.PropertyType == typeof(bool), "EditorView.DoubleEndedArrow is missing.");
                Check(typeof(EditorView).GetProperty("SelectedDoubleEndedArrow", Any)?.PropertyType == typeof(bool?), "SelectedDoubleEndedArrow bool? is missing.");
            });
            Case("SNIPFLOW-ARROW-STYLES-004 contract", () => {
                var style = Product.GetType("SnipFlow.Editor.ArrowStyleOptions");
                Check(style is not null && ItemType.GetProperty("ArrowStyle", Any)?.PropertyType == style, "AnnotationItem.ArrowStyle is missing (expected failure on 0.8.3).");
                Check(typeof(EditorView).GetProperty("ArrowStyle", Any)?.PropertyType == style, "EditorView.ArrowStyle is missing.");
                Check(typeof(EditorView).GetProperty("SelectedArrowStyle", Any)?.PropertyType == style, "SelectedArrowStyle is missing.");
                Check(typeof(EditorView).GetMethods(Any).Any(method => method.Name == "UpdateArrowStyle" && method.GetParameters().Length == 2), "UpdateArrowStyle is missing.");
            });
            Case("SNIPFLOW-ARROW-PREFERENCE-005 contract", () => {
                Check(SettingsType.GetProperty("ArrowStyle", Any)?.PropertyType == StyleType, "UserSettings.ArrowStyle is missing (expected failure on 0.8.4).");
                Check(SettingsStoreType.GetMethod("ReadSettingsFile", Any, null, new[] { typeof(string) }, null)?.ReturnType == SettingsType, "Isolated ReadSettingsFile(string) is missing.");
                Check(SettingsStoreType.GetMethod("WriteSettingsFile", Any, null, new[] { typeof(string), SettingsType }, null) is not null, "Isolated WriteSettingsFile(string, UserSettings) is missing.");
            });
            Case("SNIPFLOW-ARROW-APPEARANCE-006 contract", () => {
                Check(SettingsType.GetProperty("ArrowColor", Any)?.PropertyType == typeof(string), "UserSettings.ArrowColor is missing (expected failure on 0.8.5).");
                Check(SettingsType.GetProperty("ArrowStrokeWidth", Any)?.PropertyType == typeof(double), "UserSettings.ArrowStrokeWidth is missing.");
                Check(typeof(EditorView).GetProperty("ArrowColor", Any)?.PropertyType == typeof(Color), "EditorView.ArrowColor is missing.");
                Check(typeof(EditorView).GetProperty("ArrowStrokeWidth", Any)?.PropertyType == typeof(double), "EditorView.ArrowStrokeWidth is missing.");
                Check(typeof(EditorView).GetProperty("DrawingColor", Any)?.PropertyType == typeof(Color), "EditorView.DrawingColor is missing.");
                Check(typeof(EditorView).GetMethod("RestoreArrowAppearance", Any, null, new[] { typeof(Color), typeof(double) }, null) is not null, "RestoreArrowAppearance(Color,double) is missing.");
            });
            Case("SNIPFLOW-TOOL-APPEARANCE-007 contract", () => {
                var appearance = Product.GetType("SnipFlow.Editor.ToolAppearanceOptions");
                Check(appearance is not null, "ToolAppearanceOptions is missing (expected failure on 0.8.6).");
                Check(SettingsType.GetProperty("ToolAppearances", Any)?.PropertyType == typeof(Dictionary<,>).MakeGenericType(typeof(AnnotationTool), appearance!), "UserSettings.ToolAppearances dictionary is missing.");
                Check(typeof(EditorView).GetProperty("AppearanceTool", Any)?.PropertyType == typeof(AnnotationTool), "EditorView.AppearanceTool is missing.");
                foreach (var owner in new[] { SettingsType, typeof(EditorView) })
                    Check(owner.GetMethod("GetToolAppearance", Any, null, new[] { typeof(AnnotationTool) }, null)?.ReturnType == appearance, owner.Name + ".GetToolAppearance is missing.");
                Check(SettingsType.GetMethod("SetToolAppearance", Any, null, new[] { typeof(AnnotationTool), appearance! }, null) is not null, "UserSettings.SetToolAppearance is missing.");
                Check(typeof(EditorView).GetMethod("RestoreToolAppearance", Any, null, new[] { typeof(AnnotationTool), appearance! }, null) is not null, "EditorView.RestoreToolAppearance is missing.");
            });
            Case("SNIPFLOW-TEXT-SIZE-008 contract", () => {
                Check(SettingsType.GetProperty("TextSize", Any)?.PropertyType == typeof(double), "UserSettings.TextSize is missing (expected failure on 0.8.7).");
                Check(typeof(EditorView).GetMethod("RestoreTextSize", Any, null, new[] { typeof(double) }, null) is not null, "EditorView.RestoreTextSize(double) is missing.");
            });
            Case("SNIPFLOW-TEXT-FONT-009 contract", () => {
                Check(SettingsType.GetProperty("TextFontFamily", Any)?.PropertyType == typeof(string), "UserSettings.TextFontFamily is missing (expected failure on 0.8.8).");
                Check(Product.GetType("SnipFlow.Editor.TextFonts") is not null, "TextFonts is missing.");
                Check(ItemType.GetProperty("TextFontFamily", Any)?.PropertyType == typeof(string), "AnnotationItem.TextFontFamily is missing.");
                Check(typeof(EditorView).GetProperty("TextFontFamily", Any)?.PropertyType == typeof(string), "EditorView.TextFontFamily is missing.");
                Check(typeof(EditorView).GetProperty("SelectedTextFontFamily", Any)?.PropertyType == typeof(string), "SelectedTextFontFamily is missing.");
                Check(typeof(EditorView).GetMethod("RestoreTextFontFamily", Any, null, new[] { typeof(string) }, null) is not null, "RestoreTextFontFamily(string) is missing.");
                Check(SettingsType.GetProperty("LastAnnotationTool", Any)?.PropertyType == typeof(AnnotationTool), "LastAnnotationTool is missing.");
                Check(Product.GetType("SnipFlow.Editor.TextFontPicker") is not null, "TextFontPicker is missing.");
            });
            if (failed == 0)
            {
                ArrowPreferenceCases();
                ArrowAppearanceCases();
                ToolAppearanceCases();
                TextSizeCases();
                TextFontCases();
                LastToolCases();
                ModelCases();
                ProjectCases();
                ArrowCases();
                ArrowStyleCases();
                if (!args.Contains("--model-only")) { EditorCases(); SyntheticAndEdgeCases(); ZoomCases(); }
            }
        }
        catch (Exception ex) { failed++; Results.Add(new { name = "harness", status = "FAIL", error = Unwrap(ex).ToString() }); }
        var assemblyHash = Hash(File.ReadAllBytes(Product.Location));
        var report = new {
            guard = "SNIPFLOW-TEXT-FONT-009", timestamp = DateTimeOffset.Now, passed, failed,
            productAssembly = Product.Location, productAssemblySha256 = assemblyHash,
            safety = "Empty application; no Avalonia Window, SnipFlow.App/MainWindow, OS input, clipboard, capture, hotkey, installation, or deployment.",
            method = "Real compiled product via ProjectReference and reflection; assertions call product rendering, document operations, project persistence, and isolated settings-file IO. Two fresh console processes load only the supplied test settings path and apply all seven tools' color/width plus five arrow options, TextSize, requested TextFontFamily and LastAnnotationTool to a new offscreen EditorView and the real drawing factory. Both probes invoke BeginTextEdit and CommitTextEdit and inspect draft, TextBox and committed size/family without a Window. Separate cases compare real installed fonts' metrics and pixels, missing-font fallback, per-item clone/cache/project persistence, preferences and document undo. A minimal QuietButton theme placeholder supports arrow panel state tests only; actual font-picker visual checks run in the separate --font-picker-visual mode with real Fluent/palette/source styles. No OS or routed input events.",
            limitations = "Does not instantiate MainWindow or prove its startup/save wiring, double-click routing, interactive pointer capture, native-window layout, IME, or desktop scaling. Linux execution requires an X11/XWayland display; use a virtual display such as Xvfb.",
            tests = Results
        };
        File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Image crop regression: {passed} passed, {failed} failed. {Path.Combine(Output, "results.json")}");
        return failed == 0 ? 0 : 1;
    }

    internal static void Case(string name, Action action)
    {
        try { action(); passed++; Results.Add(new { name, status = "PASS" }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Results.Add(new { name, status = "FAIL", error = Unwrap(ex).ToString() }); Console.WriteLine("FAIL " + name + ": " + Unwrap(ex).Message); }
    }
    internal static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    internal static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : e;
    internal static void Rejected(Action action)
    {
        try { action(); }
        catch (Exception ex) when (Unwrap(ex) is InvalidDataException or ArgumentException or JsonException) { return; }
        throw new Exception("Invalid crop/project was accepted.");
    }
    internal static object? Invoke(object? target, Type type, string name, params object?[] args)
    {
        var matches = type.GetMethods(Any).Where(m => m.Name == name && m.GetParameters().Length == args.Length)
            .Where(m => m.GetParameters().Select((p, i) => args[i] is null || p.ParameterType.IsInstanceOfType(args[i]) || Nullable.GetUnderlyingType(p.ParameterType)?.IsInstanceOfType(args[i]) == true).All(v => v)).ToArray();
        if (matches.Length != 1) throw new MissingMethodException($"Expected one {type.Name}.{name} overload, found {matches.Length}.");
        try { return matches[0].Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    internal static object? Call(object target, string name, params object?[] args) => Invoke(target, target.GetType(), name, args);
    internal static object? Static(Type type, string name, params object?[] args) => Invoke(null, type, name, args);
    internal static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Any)!.GetValue(obj);
    internal static void Set(object obj, string name, object? value) => obj.GetType().GetProperty(name, Any)!.SetValue(obj, value);
    internal static object ImageItem(Bitmap image, Rect frame, Rect? crop)
    {
        var item = Activator.CreateInstance(ItemType, true)!;
        Set(item, "Tool", AnnotationTool.Image); Set(item, "Start", frame.TopLeft); Set(item, "End", frame.BottomRight);
        Set(item, "Width", 1.0); Set(item, "Image", image); Set(item, "ImageCrop", crop);
        return item;
    }
    internal static EditorView Editor(Bitmap image, Rect? crop, Rect? frame = null)
    {
        var editor = new EditorView();
        var board = ImageTools.Solid(320, 240, Colors.White);
        var array = Array.CreateInstance(ItemType, 1); array.SetValue(ImageItem(image, frame ?? new Rect(80, 60, 160, 120), crop), 0);
        Call(editor, "RestoreSession", board, array, false, true);
        editor.SelectAll();
        return editor;
    }
    internal static IList Items(EditorView editor) => (IList)typeof(EditorView).GetField("_annotations", Any)!.GetValue(editor)!;
    internal static object First(EditorView editor) => Items(editor)[0]!;
    internal static Rect? Crop(object item) => (Rect?)Get(item, "ImageCrop");
    internal static Rect Bounds(object item) => (Rect)Get(item, "Bounds")!;
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static PixelBuffer Pixels(Bitmap image)
    {
        using var decoded = ImageTools.Decode(ImageTools.EncodePng(image));
        return PixelBuffer.FromBitmap(decoded);
    }
    internal static string PixelHash(Bitmap image) => Hash(Pixels(image).Pixels);
    internal static void Save(string name, Bitmap image) => ImageTools.SavePng(image, Path.Combine(Output, name));
    internal static Color At(PixelBuffer image, int x, int y)
    {
        var p = image.Pixels; var i = (y * image.Width + x) * 4;
        return Color.FromArgb(p[i+3],p[i+2],p[i+1],p[i]);
    }
    internal static Bitmap Quadrants()
    {
        var pixels = new PixelBuffer(80, 60);
        for (int y = 0; y < 60; y++) for (int x = 0; x < 80; x++) {
            var color = y < 30 ? x < 40 ? Colors.Red : Colors.Lime : x < 40 ? Colors.Blue : Colors.Yellow;
            var i = (y * 80 + x) * 4; pixels.Pixels[i] = color.B; pixels.Pixels[i+1] = color.G; pixels.Pixels[i+2] = color.R; pixels.Pixels[i+3] = 255;
        }
        return pixels.ToBitmap();
    }
    internal static Bitmap RenderItem(object item, int width, int height) => ImageTools.Render(new PixelSize(width,height), context => Call(item,"Draw",context));
    internal static void EqualRect(Rect? actual, Rect? expected, string message)
    {
        Check(actual.HasValue == expected.HasValue, message);
        if (actual is {} a && expected is {} b) Check(Math.Abs(a.X-b.X)<1e-9 && Math.Abs(a.Y-b.Y)<1e-9 && Math.Abs(a.Width-b.Width)<1e-9 && Math.Abs(a.Height-b.Height)<1e-9,message+$" ({a} != {b})");
    }
}

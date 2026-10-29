using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static string ColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    static void CheckAppearanceSettings(object settings, string color, double width)
    {
        Check((string)Get(settings, "ArrowColor")! == color, "Arrow color preference differs.");
        Check((double)Get(settings, "ArrowStrokeWidth")! == width, "Arrow width preference differs.");
    }
    static void CheckArrowAppearance(EditorView editor, Color color, double width)
    {
        Check((Color)Get(editor, "ArrowColor")! == color, "Arrow color cache differs.");
        Check((double)Get(editor, "ArrowStrokeWidth")! == width, "Arrow width cache differs.");
        CheckItemAppearance(Call(editor, "CreateShapeAnnotation", AnnotationTool.Arrow, new Point(30, 30))!, color, width);
    }
    static void CheckItemAppearance(object item, Color color, double width)
    {
        Check((Color)Get(item, "Color")! == color, "Factory/document color differs.");
        Check((double)Get(item, "Width")! == width, "Factory/document width differs.");
    }
    static object AppearanceSettings(object style, string color, double width)
    {
        var settings = NewPreferenceSettings(style);
        Set(settings, "ArrowColor", color); Set(settings, "ArrowStrokeWidth", width);
        return settings;
    }

    static void ArrowAppearanceCases()
    {
        var root = Path.Combine(Output, "arrow-appearance"); Directory.CreateDirectory(root);
        var style = Style("Circle", "Diamond", "Small", "Large", "Dash");
        var path = Path.Combine(root, "appearance.json");
        var preferred = Color.Parse("#ABCDEF"); const double preferredWidth = 11.5;

        Case("arrow appearance JSON roundtrips canonical RGB and fractional width", () => {
            var settings = AppearanceSettings(style, "#abcdef", preferredWidth);
            WritePreferences(path, settings);
            var json = PreferenceJson(path);
            Check(json["ArrowColor"]!.GetValue<string>() == "#ABCDEF", "Writer did not canonicalize RGB.");
            Check(json["ArrowStrokeWidth"]!.GetValue<double>() == preferredWidth, "Writer lost fractional width.");
            Check((string)Get(settings, "ArrowColor")! == "#abcdef", "Writer mutated caller preferences.");
            var loaded = ReadPreferences(path); CheckAppearanceSettings(loaded, "#ABCDEF", preferredWidth);
            EqualStyle(Get(loaded, "ArrowStyle"), style, "Appearance IO lost five style options."); CheckOtherPreferences(loaded);
            foreach (var width in new[] { 1.0, 32.0 }) {
                var edge = Path.Combine(root, "width-" + width + ".json");
                WritePreferences(edge, AppearanceSettings(style, "#00ff09", width));
                CheckAppearanceSettings(ReadPreferences(edge), "#00FF09", width);
            }
        });

        Case("legacy appearance omissions default each field without replacing style", () => {
            foreach (var omitted in new[] { "ArrowColor", "ArrowStrokeWidth", "both" }) {
                var json = PreferenceJson(path);
                if (omitted != "ArrowStrokeWidth") json.Remove("ArrowColor");
                if (omitted != "ArrowColor") json.Remove("ArrowStrokeWidth");
                var legacy = Path.Combine(root, "legacy-" + omitted + ".json"); File.WriteAllText(legacy, json.ToJsonString());
                var loaded = ReadPreferences(legacy);
                CheckAppearanceSettings(loaded, omitted == "ArrowStrokeWidth" ? "#ABCDEF" : "#63D5C5", omitted == "ArrowColor" ? preferredWidth : 4);
                EqualStyle(Get(loaded, "ArrowStyle"), style, "Legacy appearance fallback replaced style."); CheckOtherPreferences(loaded);
            }
            CheckAppearanceSettings(Activator.CreateInstance(SettingsType)!, "#63D5C5", 4);
        });

        Case("invalid appearance fields fall back independently and duplicates use the last value", () => {
            var invalidColors = new JsonNode?[] { null, JsonValue.Create(true), JsonValue.Create(123), new JsonObject(), JsonValue.Create("red"), JsonValue.Create("#123"), JsonValue.Create("#80ABCDEF"), JsonValue.Create("#GG0000"), JsonValue.Create(" #ABCDEF") };
            for (var n = 0; n < invalidColors.Length; n++) {
                var json = PreferenceJson(path); json["ArrowColor"] = invalidColors[n];
                var bad = Path.Combine(root, "bad-color-" + n + ".json"); File.WriteAllText(bad, json.ToJsonString());
                var loaded = ReadPreferences(bad); CheckAppearanceSettings(loaded, "#63D5C5", preferredWidth);
                EqualStyle(Get(loaded, "ArrowStyle"), style, "Bad color replaced style."); CheckOtherPreferences(loaded);
            }
            var invalidWidths = new JsonNode?[] { null, JsonValue.Create("7.5"), JsonValue.Create(false), new JsonObject(), JsonValue.Create(-1), JsonValue.Create(0), JsonValue.Create(.99), JsonValue.Create(32.01), JsonNode.Parse("1e999") };
            for (var n = 0; n < invalidWidths.Length; n++) {
                var json = PreferenceJson(path); json["ArrowStrokeWidth"] = invalidWidths[n];
                var bad = Path.Combine(root, "bad-width-" + n + ".json"); File.WriteAllText(bad, json.ToJsonString());
                var loaded = ReadPreferences(bad); CheckAppearanceSettings(loaded, "#ABCDEF", 4);
                EqualStyle(Get(loaded, "ArrowStyle"), style, "Bad width replaced style."); CheckOtherPreferences(loaded);
            }
            var original = File.ReadAllText(path).Trim();
            var duplicate = Path.Combine(root, "duplicate-appearance.json");
            File.WriteAllText(duplicate, "{\"ArrowColor\":false,\"ArrowStrokeWidth\":-1," + original[1..]);
            CheckAppearanceSettings(ReadPreferences(duplicate), "#ABCDEF", preferredWidth);
            File.WriteAllText(duplicate, original[..^1] + ",\"ArrowColor\":false,\"ArrowStrokeWidth\":-1}");
            var fallback = ReadPreferences(duplicate); CheckAppearanceSettings(fallback, "#63D5C5", 4);
            EqualStyle(Get(fallback, "ArrowStyle"), style, "Duplicate fallback lost style."); CheckOtherPreferences(fallback);
        });

        Case("invalid appearance writes reject before publication without loading Current", () => {
            var lazy = SettingsLazy(); var initialized = LazyCreated(lazy); var original = Hash(File.ReadAllBytes(path));
            var invalid = new List<object>();
            foreach (var color in new string?[] { null, "", "#123", "#80ABCDEF", "#GG0000" })
                invalid.Add(AppearanceSettings(style, color!, preferredWidth));
            foreach (var width in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, 0, .99, 32.01 })
                invalid.Add(AppearanceSettings(style, "#ABCDEF", width));
            foreach (var settings in invalid) {
                Rejected(() => WritePreferences(path, settings));
                Check(Hash(File.ReadAllBytes(path)) == original && !File.Exists(path + ".tmp"), "Rejected appearance write changed published data.");
                var uncreated = Path.Combine(root, "must-not-create", "settings.json");
                Rejected(() => WritePreferences(uncreated, settings));
                Check(!Directory.Exists(Path.GetDirectoryName(uncreated)), "Validation created a directory before rejecting caller data.");
            }
            Check(ReferenceEquals(SettingsLazy(), lazy) && LazyCreated(lazy) == initialized, "Appearance writes initialized global Current.");
        });

        Case("restoring arrow appearance changes defaults without editing selected documents", () => {
            var editor = ArrowEditor(Arrow(new Point(40, 120), new Point(280, 120), 2));
            var pixels = PixelHash(editor.ExportImage()); var revision = editor.Revision; var depth = UndoDepth(editor);
            var selectedColor = Get(First(editor), "Color"); var selectedWidth = Get(First(editor), "Width");
            Call(editor, "RestoreArrowAppearance", Color.FromArgb(30, preferred.R, preferred.G, preferred.B), preferredWidth);
            CheckArrowAppearance(editor, preferred, preferredWidth);
            Check(Equals(Get(First(editor), "Color"), selectedColor) && Equals(Get(First(editor), "Width"), selectedWidth), "Restoring preferences edited the selection.");
            foreach (var invalid in new[] { 0, 33, double.NaN, double.PositiveInfinity }) {
                Rejected(() => Call(editor, "RestoreArrowAppearance", Colors.Red, invalid));
                CheckArrowAppearance(editor, preferred, preferredWidth);
            }
            Check(editor.Revision == revision && UndoDepth(editor) == depth && PixelHash(editor.ExportImage()) == pixels, "Preference restore changed document pixels/history.");
        });

        Case("arrow appearance survives tool switches and non-arrow primary edits", () => {
            var editor = new EditorView(); Call(editor, "RestoreArrowAppearance", preferred, preferredWidth);
            editor.SetTool(AnnotationTool.Rectangle); editor.SetColor(Colors.Blue); editor.StrokeWidth = 2.5;
            foreach (var tool in new[] { AnnotationTool.Pen, AnnotationTool.Rectangle, AnnotationTool.Text }) {
                editor.SetTool(tool);
                var expectedColor = tool == AnnotationTool.Rectangle ? Colors.Blue : Color.Parse("#63D5C5");
                var expectedWidth = tool == AnnotationTool.Rectangle ? 2.5 : 4;
                Check((Color)Get(editor, "DrawingColor")! == expectedColor && editor.StrokeWidth == expectedWidth, "Independent non-arrow defaults changed while switching tools.");
                CheckArrowAppearance(editor, preferred, preferredWidth);
            }
            editor.SetTool(AnnotationTool.Arrow);
            Check((Color)Get(editor, "DrawingColor")! == preferred && editor.StrokeWidth == preferredWidth, "Arrow tool did not restore its own effective defaults.");
            editor.SetColor(Colors.Orange); editor.StrokeWidth = 9.75;
            CheckArrowAppearance(editor, Colors.Orange, 9.75);
            editor.SetTool(AnnotationTool.Rectangle);
            CheckItemAppearance(Call(editor, "CreateShapeAnnotation", AnnotationTool.Rectangle, new Point(30, 30))!, Colors.Blue, 2.5);
            var rectangle = Call(editor, "CreateShapeAnnotation", AnnotationTool.Rectangle, new Point(30, 30))!; Set(rectangle, "End", new Point(200, 160));
            var array = Array.CreateInstance(ItemType, 1); array.SetValue(rectangle, 0);
            Call(editor, "RestoreSession", ImageTools.Solid(320, 240, Colors.White), array, false, true); editor.SelectAll();
            editor.SetColor(Colors.Purple); editor.StrokeWidth = 5.5;
            CheckItemAppearance(First(editor), Colors.Purple, 5.5);
            CheckArrowAppearance(editor, Colors.Orange, 9.75);
        });

        Case("selected-arrow edits update future defaults while undo and new documents retain saved preferences", () => {
            var a = Arrow(new Point(30, 70), new Point(290, 70), 2); Set(a, "Color", Colors.Red);
            var b = Arrow(new Point(30, 170), new Point(290, 170), 6); Set(b, "Color", Colors.Blue);
            var editor = ArrowEditor(a, b); Call(editor, "RestoreArrowAppearance", preferred, preferredWidth);
            var original = PixelHash(editor.ExportImage());
            editor.SetTool(AnnotationTool.Rectangle); editor.SelectAll();
            editor.SetColor(Colors.Orange); editor.StrokeWidth = 9.75;
            foreach (var item in Items(editor).Cast<object>()) CheckItemAppearance(item, Colors.Orange, 9.75);
            Check(UndoDepth(editor) == 2, "Color and width edits did not each produce one document undo.");
            CheckArrowAppearance(editor, Colors.Orange, 9.75);
            var saved = Path.Combine(root, "selected-arrow-defaults.json");
            WritePreferences(saved, AppearanceSettings(style, ColorHex((Color)Get(editor, "ArrowColor")!), (double)Get(editor, "ArrowStrokeWidth")!));
            var savedHash = Hash(File.ReadAllBytes(saved));
            editor.Undo(); editor.Undo();
            Check(PixelHash(editor.ExportImage()) == original, "Undo did not restore selected arrow appearances.");
            CheckArrowAppearance(editor, Colors.Orange, 9.75);
            editor.Redo(); editor.Redo(); foreach (var item in Items(editor).Cast<object>()) CheckItemAppearance(item, Colors.Orange, 9.75);
            var other = Array.CreateInstance(ItemType, 1); other.SetValue(Arrow(new Point(20, 120), new Point(300, 120), 1), 0);
            Call(editor, "RestoreSession", ImageTools.Solid(320, 240, Colors.White), other, false, true); editor.SelectAll();
            CheckArrowAppearance(editor, Colors.Orange, 9.75);
            Call(editor, "NewCollage"); CheckArrowAppearance(editor, Colors.Orange, 9.75);
            Check(Hash(File.ReadAllBytes(saved)) == savedHash, "Undo/restore/new collage rewrote saved arrow preferences.");
            var reopened = ReadPreferences(saved); CheckAppearanceSettings(reopened, "#FFA500", 9.75); EqualStyle(Get(reopened, "ArrowStyle"), style, "Appearance edit lost stored style.");
        });
    }
}

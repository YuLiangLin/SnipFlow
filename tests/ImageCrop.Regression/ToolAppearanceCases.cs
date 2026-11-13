using System.Collections;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static Type ToolAppearanceType => Product.GetType("SnipFlow.Editor.ToolAppearanceOptions", true)!;
    static readonly (AnnotationTool Tool, string Color, double Width)[] ToolSamples = {
        (AnnotationTool.Arrow, "#A1B2C3", 7.25),
        (AnnotationTool.Rectangle, "#D05060", 2.5),
        (AnnotationTool.Ellipse, "#22AA44", 3.75),
        (AnnotationTool.Pen, "#4433CC", 5.5),
        (AnnotationTool.Highlight, "#EEDD11", 9.125),
        (AnnotationTool.Text, "#09AABB", 6.25),
        (AnnotationTool.Mosaic, "#997766", 13.5)
    };
    static object ToolAppearance(string? color, double width)
    {
        var appearance = Activator.CreateInstance(ToolAppearanceType)!;
        Set(appearance, "Color", color); Set(appearance, "StrokeWidth", width);
        return appearance;
    }
    static IDictionary ToolDictionary(object settings) => (IDictionary)Get(settings, "ToolAppearances")!;
    static void PopulateToolPreferences(object settings)
    {
        foreach (var sample in ToolSamples)
            Call(settings, "SetToolAppearance", sample.Tool, ToolAppearance(sample.Color.ToLowerInvariant(), sample.Width));
    }
    static void RestoreToolPreferences(EditorView editor)
    {
        foreach (var sample in ToolSamples)
            Call(editor, "RestoreToolAppearance", sample.Tool, ToolAppearance(sample.Color, sample.Width));
    }
    static void CheckToolAppearance(object owner, AnnotationTool tool, string color, double width)
    {
        var appearance = Call(owner, "GetToolAppearance", tool)!;
        Check((string)Get(appearance, "Color")! == color && (double)Get(appearance, "StrokeWidth")! == width, owner.GetType().Name + " appearance differs for " + tool);
    }
    static void CheckAllToolAppearances(object owner, AnnotationTool? changed = null, string? color = null, double width = 0)
    {
        foreach (var sample in ToolSamples)
            CheckToolAppearance(owner, sample.Tool, sample.Tool == changed ? color! : sample.Color, sample.Tool == changed ? width : sample.Width);
    }
    static Dictionary<string, object> AppearanceNames(object appearance) => new() {
        ["color"] = (string)Get(appearance, "Color")!, ["strokeWidth"] = (double)Get(appearance, "StrokeWidth")!
    };
    static Dictionary<string, Dictionary<string, object>> ToolAppearanceNames(object owner) => ToolSamples.ToDictionary(
        sample => sample.Tool.ToString(), sample => AppearanceNames(Call(owner, "GetToolAppearance", sample.Tool)!));
    static object ToolItem(EditorView editor, AnnotationTool tool)
    {
        var item = Call(editor, "CreateShapeAnnotation", tool, new Point(30, 30))!;
        Set(item, "End", new Point(180, 90));
        if (tool == AnnotationTool.Text) Set(item, "Text", "Tool preference");
        if (tool is AnnotationTool.Pen or AnnotationTool.Highlight)
        {
            var points = (IList)Get(item, "Points")!; points.Add(new Point(30, 30)); points.Add(new Point(180, 90));
        }
        return item;
    }
    static void ReplaceToolsJson(string path, JsonObject source, string tools)
    {
        var copy = source.DeepClone().AsObject(); copy.Remove("ToolAppearances");
        var json = copy.ToJsonString(); File.WriteAllText(path, json[..^1] + ",\"ToolAppearances\":" + tools + "}");
    }

    static void ToolAppearanceCases()
    {
        var root = Path.Combine(Output, "tool-appearance"); Directory.CreateDirectory(root);
        var style = Style("Circle", "Diamond", "Small", "Large", "Dash");
        var path = Path.Combine(root, "all-tools.json");
        Case("tool appearance contract validates supported tools and atomic generic setters", () => {
            var settings = NewPreferenceSettings(style); var editor = new EditorView(); RestoreToolPreferences(editor);
            foreach (var sample in ToolSamples) {
                Check((bool)Static(ToolAppearanceType, "Supports", sample.Tool)!, "Supported tool omitted: " + sample.Tool);
                Check((bool)Static(ToolAppearanceType, "IsValid", ToolAppearance(sample.Color, sample.Width))!, "Valid appearance rejected.");
                foreach (var invalid in new object?[] { null, ToolAppearance("#123", sample.Width), ToolAppearance(sample.Color, double.NaN), ToolAppearance(sample.Color, 32.01) }) {
                    Check(!(bool)Static(ToolAppearanceType, "IsValid", invalid)!, "Invalid appearance passed IsValid.");
                    Rejected(() => Call(settings, "SetToolAppearance", sample.Tool, invalid));
                    Rejected(() => Call(editor, "RestoreToolAppearance", sample.Tool, invalid));
                    CheckAllToolAppearances(settings); CheckAllToolAppearances(editor);
                }
            }
            foreach (var tool in new[] { AnnotationTool.Select, AnnotationTool.Image, (AnnotationTool)999 }) {
                Check(!(bool)Static(ToolAppearanceType, "Supports", tool)!, "Unsupported tool accepted.");
                Rejected(() => Call(settings, "GetToolAppearance", tool)); Rejected(() => Call(editor, "GetToolAppearance", tool));
                Rejected(() => Call(settings, "SetToolAppearance", tool, ToolAppearance("#123456", 2)));
                Rejected(() => Call(editor, "RestoreToolAppearance", tool, ToolAppearance("#123456", 2)));
            }
            Call(settings, "SetToolAppearance", AnnotationTool.Arrow, ToolAppearance("#abcdef", 1));
            CheckAppearanceSettings(settings, "#ABCDEF", 1); Check(!ToolDictionary(settings).Contains(AnnotationTool.Arrow), "Generic Arrow setter created a second truth.");
            Call(editor, "RestoreArrowAppearance", Colors.Orange, 12.5);
            CheckToolAppearance(editor, AnnotationTool.Arrow, "#FFA500", 12.5);
            Call(editor, "RestoreToolAppearance", AnnotationTool.Arrow, ToolAppearance("#abcdef", 2.25));
            CheckArrowAppearance(editor, Color.Parse("#ABCDEF"), 2.25);
        });

        Case("all seven tool appearances roundtrip canonical colors and independent fractional widths", () => {
            var settings = NewPreferenceSettings(style);
            foreach (var sample in ToolSamples.Where(sample => sample.Tool != AnnotationTool.Arrow))
                ToolDictionary(settings)[sample.Tool] = ToolAppearance(sample.Color.ToLowerInvariant(), sample.Width);
            WritePreferences(path, settings); var json = PreferenceJson(path); var tools = json["ToolAppearances"]!.AsObject();
            Check(tools.Count == 6 && !tools.ContainsKey("Arrow"), "Writer did not keep one Arrow truth and six named entries.");
            foreach (var sample in ToolSamples.Where(sample => sample.Tool != AnnotationTool.Arrow)) {
                var encoded = tools[sample.Tool.ToString()]!.AsObject();
                Check(encoded.Count == 2 && encoded["Color"]!.GetValue<string>() == sample.Color && encoded["StrokeWidth"]!.GetValue<double>() == sample.Width, "Encoded tool differs: " + sample.Tool);
                Check((string)Get(ToolDictionary(settings)[sample.Tool]!, "Color")! == sample.Color.ToLowerInvariant(), "Writer mutated caller's record.");
            }
            var loaded = ReadPreferences(path); CheckAllToolAppearances(loaded); CheckOtherPreferences(loaded);
            EqualStyle(Get(loaded, "ArrowStyle"), style, "Tool IO lost legacy arrow style."); CheckAppearanceSettings(loaded, "#A1B2C3", 7.25);
            var reencoded = Path.Combine(root, "resaved.json"); WritePreferences(reencoded, loaded);
            Check(JsonNode.DeepEquals(json, PreferenceJson(reencoded)), "Second publication changed appearance values.");
        });

        Case("legacy or malformed tool blocks retain Arrow and unrelated settings with independent defaults", () => {
            foreach (var kind in new[] { "missing", "null", "scalar", "array" }) {
                var json = PreferenceJson(path);
                if (kind == "missing") json.Remove("ToolAppearances");
                else json["ToolAppearances"] = kind == "null" ? null : kind == "array" ? new JsonArray() : JsonValue.Create(false);
                var legacy = Path.Combine(root, "legacy-" + kind + ".json"); File.WriteAllText(legacy, json.ToJsonString());
                var loaded = ReadPreferences(legacy);
                CheckAppearanceSettings(loaded, "#A1B2C3", 7.25); EqualStyle(Get(loaded, "ArrowStyle"), style, "Legacy tools replaced Arrow style."); CheckOtherPreferences(loaded);
                foreach (var sample in ToolSamples.Where(sample => sample.Tool != AnnotationTool.Arrow)) CheckToolAppearance(loaded, sample.Tool, "#63D5C5", 4);
            }
        });

        Case("each tool falls back only its invalid color or width without losing the other six tools", () => {
            var invalidColors = new JsonNode?[] { null, JsonValue.Create(false), JsonValue.Create(123), new JsonObject(), JsonValue.Create("red"), JsonValue.Create("#123"), JsonValue.Create("#80ABCDEF"), JsonValue.Create("#GG0000"), JsonValue.Create(" #ABCDEF") };
            var invalidWidths = new JsonNode?[] { null, JsonValue.Create("7.5"), JsonValue.Create(false), new JsonObject(), JsonValue.Create(-1), JsonValue.Create(0), JsonValue.Create(.99), JsonValue.Create(32.01), JsonNode.Parse("1e999") };
            foreach (var sample in ToolSamples) foreach (var field in new[] { "Color", "StrokeWidth" }) {
                var invalid = field == "Color" ? invalidColors : invalidWidths;
                for (var n = 0; n < invalid.Length; n++) {
                    var json = PreferenceJson(path);
                    if (sample.Tool == AnnotationTool.Arrow) json[field == "Color" ? "ArrowColor" : "ArrowStrokeWidth"] = invalid[n]?.DeepClone();
                    else json["ToolAppearances"]![sample.Tool.ToString()]![field] = invalid[n]?.DeepClone();
                    var bad = Path.Combine(root, $"invalid-{sample.Tool}-{field}-{n}.json"); File.WriteAllText(bad, json.ToJsonString());
                    var loaded = ReadPreferences(bad);
                    CheckAllToolAppearances(loaded, sample.Tool, field == "Color" ? "#63D5C5" : sample.Color, field == "StrokeWidth" ? 4 : sample.Width);
                    EqualStyle(Get(loaded, "ArrowStyle"), style, "Invalid field replaced Arrow style."); CheckOtherPreferences(loaded);
                }
            }
        });

        Case("tool block entry and field duplicates use final values including invalid fallback", () => {
            var source = PreferenceJson(path); var duplicate = Path.Combine(root, "duplicate-tools.json");
            foreach (var sample in ToolSamples.Where(sample => sample.Tool != AnnotationTool.Arrow)) {
                var key = sample.Tool.ToString();
                var entry = "{\"Color\":false,\"Color\":\"#abcdef\",\"StrokeWidth\":-1,\"StrokeWidth\":8.25}";
                ReplaceToolsJson(duplicate, source, "{\"" + key + "\":null,\"" + key + "\":" + entry + "}");
                CheckToolAppearance(ReadPreferences(duplicate), sample.Tool, "#ABCDEF", 8.25);
                ReplaceToolsJson(duplicate, source, "{\"" + key + "\":" + entry + ",\"" + key + "\":null}");
                CheckToolAppearance(ReadPreferences(duplicate), sample.Tool, "#63D5C5", 4);
                ReplaceToolsJson(duplicate, source, "{\"" + key + "\":{\"Color\":\"#abcdef\",\"Color\":false,\"StrokeWidth\":8.25,\"StrokeWidth\":-1}}");
                CheckToolAppearance(ReadPreferences(duplicate), sample.Tool, "#63D5C5", 4);
            }
            var original = File.ReadAllText(path).Trim();
            File.WriteAllText(duplicate, "{\"ToolAppearances\":null," + original[1..]); CheckAllToolAppearances(ReadPreferences(duplicate));
            File.WriteAllText(duplicate, original[..^1] + ",\"ToolAppearances\":null}");
            var loaded = ReadPreferences(duplicate); CheckAppearanceSettings(loaded, "#A1B2C3", 7.25); CheckOtherPreferences(loaded);
            foreach (var sample in ToolSamples.Where(sample => sample.Tool != AnnotationTool.Arrow)) CheckToolAppearance(loaded, sample.Tool, "#63D5C5", 4);
        });

        Case("unsupported tool entries cannot shadow legacy Arrow and are ignored on read and publication", () => {
            var source = PreferenceJson(path); var ignored = Path.Combine(root, "unsupported.json");
            ReplaceToolsJson(ignored, source, "{\"Arrow\":{\"Color\":\"#FF0000\",\"StrokeWidth\":1},\"Select\":null,\"Image\":false,\"999\":{},\"rectangle\":{},\"1\":{}}");
            var loaded = ReadPreferences(ignored); CheckAppearanceSettings(loaded, "#A1B2C3", 7.25); Check(ToolDictionary(loaded).Count == 0, "Unsupported JSON keys created tool entries.");
            var settings = NewPreferenceSettings(style);
            foreach (var tool in new[] { AnnotationTool.Arrow, AnnotationTool.Select, AnnotationTool.Image, (AnnotationTool)999 }) ToolDictionary(settings)[tool] = ToolAppearance(null, double.NaN);
            WritePreferences(ignored, settings); CheckAllToolAppearances(ReadPreferences(ignored));
            Check(PreferenceJson(ignored)["ToolAppearances"]!.AsObject().Count == 6, "Ignored raw entries leaked into published JSON.");
        });

        Case("invalid supported tool writes reject atomically before IO and never initialize Current", () => {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Current was already loaded."); var original = Hash(File.ReadAllBytes(path));
            foreach (var sample in ToolSamples.Where(sample => sample.Tool != AnnotationTool.Arrow)) {
                var invalid = new List<object?> { null };
                foreach (var color in new string?[] { null, "", "#123", "#80ABCDEF", "#GG0000" }) invalid.Add(ToolAppearance(color, sample.Width));
                foreach (var width in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, 0, .99, 32.01 }) invalid.Add(ToolAppearance(sample.Color, width));
                foreach (var appearance in invalid) {
                    var settings = NewPreferenceSettings(style); ToolDictionary(settings)[sample.Tool] = appearance;
                    Rejected(() => WritePreferences(path, settings));
                    Check(Hash(File.ReadAllBytes(path)) == original && !File.Exists(path + ".tmp"), "Rejected tool write changed published file.");
                    var uncreated = Path.Combine(root, "must-not-create", "settings.json"); Rejected(() => WritePreferences(uncreated, settings));
                    Check(!Directory.Exists(Path.GetDirectoryName(uncreated)), "Invalid tool data created a directory before rejection.");
                }
            }
            Check(ReferenceEquals(lazy, SettingsLazy()) && !LazyCreated(lazy), "Tool IO initialized or replaced global Current.");
        });

        Case("all tool switches and explicit factories retain independent defaults regardless of active context", () => {
            var editor = new EditorView(); RestoreToolPreferences(editor);
            foreach (var active in ToolSamples) {
                editor.SetTool(active.Tool);
                Check((AnnotationTool)Get(editor, "AppearanceTool")! == active.Tool, "No-selection appearance context differs.");
                Check(editor.DrawingColor == Color.Parse(active.Color) && editor.StrokeWidth == active.Width, "Tool switch displays another default.");
                foreach (var sample in ToolSamples) CheckItemAppearance(Call(editor, "CreateShapeAnnotation", sample.Tool, new Point(10, 10))!, Color.Parse(sample.Color), sample.Width);
                CheckAllToolAppearances(editor);
            }
            foreach (var sample in ToolSamples) {
                var edited = new EditorView(); RestoreToolPreferences(edited); edited.SetTool(sample.Tool);
                edited.SetColor(Colors.Coral); edited.StrokeWidth = 10.75;
                CheckAllToolAppearances(edited, sample.Tool, "#FF7F50", 10.75);
            }
            foreach (var fallback in new[] { AnnotationTool.Select, AnnotationTool.Image }) {
                editor.SetTool(fallback); editor.SetColor(Colors.Magenta); editor.StrokeWidth = 17;
                CheckAllToolAppearances(editor);
            }
        });

        Case("primary selection updates only its default while mixed document selection keeps batch semantics", () => {
            foreach (var primary in ToolSamples) {
                var editor = new EditorView(); RestoreToolPreferences(editor);
                var samples = ToolSamples.Where(sample => sample.Tool != primary.Tool).Concat(new[] { primary }).ToArray();
                var array = Array.CreateInstance(ItemType, samples.Length);
                for (var n = 0; n < samples.Length; n++) array.SetValue(ToolItem(editor, samples[n].Tool), n);
                Call(editor, "RestoreSession", ImageTools.Solid(320, 240, Colors.White), array, false, true);
                editor.SetTool(primary.Tool == AnnotationTool.Arrow ? AnnotationTool.Rectangle : AnnotationTool.Arrow); editor.SelectAll();
                Check((AnnotationTool)Get(editor, "AppearanceTool")! == primary.Tool, "Selected primary did not override active tool.");
                editor.SetColor(Colors.Coral); editor.StrokeWidth = 10.75;
                CheckAllToolAppearances(editor, primary.Tool, "#FF7F50", 10.75);
                foreach (var item in Items(editor).Cast<object>()) {
                    var tool = (AnnotationTool)Get(item, "Tool")!; var initial = ToolSamples.Single(sample => sample.Tool == tool);
                    CheckItemAppearance(item, tool == AnnotationTool.Mosaic ? Color.Parse(initial.Color) : Colors.Coral, tool == AnnotationTool.Text ? initial.Width : 10.75);
                }
                Check(UndoDepth(editor) == 2, "Batch color and width did not each produce one document undo.");
                editor.Undo(); editor.Undo(); CheckAllToolAppearances(editor, primary.Tool, "#FF7F50", 10.75);
                foreach (var item in Items(editor).Cast<object>()) { var initial = ToolSamples.Single(sample => sample.Tool == (AnnotationTool)Get(item, "Tool")!); CheckItemAppearance(item, Color.Parse(initial.Color), initial.Width); }
                editor.Redo(); editor.Redo(); CheckAllToolAppearances(editor, primary.Tool, "#FF7F50", 10.75);
            }
        });

        Case("restoring all tool defaults leaves selection pixels revision undo and subsequent documents intact", () => {
            var editor = ArrowEditor(Arrow(new Point(20, 120), new Point(300, 120), 2));
            var before = PixelHash(editor.ExportImage()); var revision = editor.Revision; var undo = UndoDepth(editor);
            RestoreToolPreferences(editor); CheckAllToolAppearances(editor);
            Check(PixelHash(editor.ExportImage()) == before && editor.Revision == revision && UndoDepth(editor) == undo, "Restoring defaults edited the selected document/history.");
            var other = Array.CreateInstance(ItemType, 1); other.SetValue(Arrow(new Point(20, 100), new Point(300, 100), 1), 0);
            Call(editor, "RestoreSession", ImageTools.Solid(320, 240, Colors.White), other, false, true); CheckAllToolAppearances(editor);
            editor.LoadImage(ImageTools.Solid(320, 240, Colors.White)); CheckAllToolAppearances(editor);
            Call(editor, "NewCollage"); CheckAllToolAppearances(editor);
        });

        Case("real text edit creation and commit use Text color and retain defaults through undo", () => {
            var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.White)); RestoreToolPreferences(editor);
            editor.SetTool(AnnotationTool.Rectangle); editor.TextSize = 31;
            Call(editor, "BeginTextEdit", (object?)null, new Rect(20, 20, 250, 90), (object?)null);
            var draft = typeof(EditorView).GetField("_textDraft", Any)!.GetValue(editor)!;
            var text = ToolSamples.Single(sample => sample.Tool == AnnotationTool.Text); CheckItemAppearance(draft, Color.Parse(text.Color), text.Width);
            Check((AnnotationTool)Get(editor, "AppearanceTool")! == AnnotationTool.Text && (double)Get(draft, "FontSize")! == 31, "Text draft does not drive appearance context/font size.");
            var box = (TextBox)typeof(EditorView).GetField("_textEditor", Any)!.GetValue(editor)!;
            Check(box.Foreground is ISolidColorBrush brush && brush.Color == Color.Parse(text.Color), "Actual text editor foreground differs from Text preference.");
            Check(TopLevel.GetTopLevel(editor) is null && Application.Current!.ApplicationLifetime is null, "Text fixture attached to a native host.");
            box.Text = "Text preference 文字"; editor.CommitTextEdit();
            Check(Items(editor).Count == 1 && (AnnotationTool)Get(First(editor), "Tool")! == AnnotationTool.Text, "Actual commit did not create Text.");
            CheckItemAppearance(First(editor), Color.Parse(text.Color), text.Width); Check((string)Get(First(editor), "Text")! == "Text preference 文字", "Text content was not committed.");
            Check(UndoDepth(editor) == 1, "Text creation did not create one undo."); CheckAllToolAppearances(editor);
            editor.Undo(); Check(Items(editor).Count == 0, "Text undo did not remove the created item."); CheckAllToolAppearances(editor);
            editor.Redo(); CheckItemAppearance(First(editor), Color.Parse(text.Color), text.Width); CheckAllToolAppearances(editor);
        });
    }
}

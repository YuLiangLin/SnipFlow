using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    const double PreferredTextSize = 37.5;
    static object TextSizeSettings(object style, double size)
    {
        var settings = NewPreferenceSettings(style); Set(settings, "TextSize", size); return settings;
    }
    static void CheckTextSizeCompanions(object settings, object style)
    {
        CheckAllToolAppearances(settings); CheckAppearanceSettings(settings, "#A1B2C3", 7.25);
        EqualStyle(Get(settings, "ArrowStyle"), style, "TextSize IO replaced Arrow options."); CheckOtherPreferences(settings);
    }
    static object ExistingText(EditorView editor, double size, string text, Point start)
    {
        var item = Call(editor, "CreateShapeAnnotation", AnnotationTool.Text, start)!;
        Set(item, "FontSize", size); Set(item, "Text", text); Set(item, "TextWidth", 240.0); Set(item, "TextBoxHeight", 80.0);
        return item;
    }
    static void CheckTextDefault(EditorView editor, double expected)
    {
        Check(editor.TextSize == expected, "Remembered default TextSize differs.");
        Check((double)Get(Call(editor, "CreateShapeAnnotation", AnnotationTool.Text, new Point(20, 20))!, "FontSize")! == expected, "Real text factory lost the remembered size.");
    }

    static void TextSizeCases()
    {
        var root = Path.Combine(Output, "text-size"); Directory.CreateDirectory(root);
        var style = Style("Circle", "Diamond", "Small", "Large", "Dash");
        var path = Path.Combine(root, "text-size.json");

        Case("text size roundtrips fractional and boundary values while retaining all other preferences", () => {
            Check((double)Get(Activator.CreateInstance(SettingsType)!, "TextSize")! == 26, "New settings changed compatible TextSize default.");
            foreach (var size in new[] { PreferredTextSize, 12.0, 240.0 }) {
                var settings = TextSizeSettings(style, size); WritePreferences(path, settings);
                Check(PreferenceJson(path)["TextSize"]!.GetValue<double>() == size, "TextSize was not serialized as an exact number.");
                var loaded = ReadPreferences(path); Check((double)Get(loaded, "TextSize")! == size, "TextSize roundtrip differs.");
                Check((double)Get(settings, "TextSize")! == size, "Writer mutated caller TextSize."); CheckTextSizeCompanions(loaded, style);
            }
            WritePreferences(path, TextSizeSettings(style, PreferredTextSize));
        });

        Case("legacy malformed and duplicate text sizes fall back independently without discarding seven tool preferences", () => {
            var invalid = new (string Name, JsonNode? Value)[] {
                ("missing", null), ("null", null), ("string", JsonValue.Create("37.5")), ("bool", JsonValue.Create(true)),
                ("object", new JsonObject()), ("array", new JsonArray()), ("zero", JsonValue.Create(0)),
                ("below", JsonValue.Create(11.99)), ("above", JsonValue.Create(240.01)), ("overflow", JsonNode.Parse("1e999"))
            };
            foreach (var entry in invalid) {
                var json = PreferenceJson(path);
                if (entry.Name == "missing") json.Remove("TextSize"); else json["TextSize"] = entry.Value?.DeepClone();
                var bad = Path.Combine(root, "invalid-" + entry.Name + ".json"); File.WriteAllText(bad, json.ToJsonString());
                var loaded = ReadPreferences(bad); Check((double)Get(loaded, "TextSize")! == 26, "Malformed size did not default: " + entry.Name);
                CheckTextSizeCompanions(loaded, style);
            }
            var original = File.ReadAllText(path).Trim(); var duplicate = Path.Combine(root, "duplicate.json");
            File.WriteAllText(duplicate, "{\"TextSize\":false," + original[1..]);
            var validLast = ReadPreferences(duplicate); Check((double)Get(validLast, "TextSize")! == PreferredTextSize, "Duplicate valid last TextSize lost."); CheckTextSizeCompanions(validLast, style);
            File.WriteAllText(duplicate, original[..^1] + ",\"TextSize\":false}");
            var invalidLast = ReadPreferences(duplicate); Check((double)Get(invalidLast, "TextSize")! == 26, "Duplicate invalid last TextSize did not default."); CheckTextSizeCompanions(invalidLast, style);
            File.WriteAllText(duplicate, original[..^1] + ",\"TextSize\":51.25}");
            var changedLast = ReadPreferences(duplicate); Check((double)Get(changedLast, "TextSize")! == 51.25, "Duplicate changed last TextSize lost."); CheckTextSizeCompanions(changedLast, style);
        });

        Case("invalid text size writes reject before IO without changing published data or lazy Current", () => {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Current was already loaded."); var original = Hash(File.ReadAllBytes(path));
            foreach (var size in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1, 0, 11.99, 240.01 }) {
                var settings = TextSizeSettings(style, size); Rejected(() => WritePreferences(path, settings));
                Check(Hash(File.ReadAllBytes(path)) == original && !File.Exists(path + ".tmp"), "Invalid TextSize changed published data or left temp data.");
                var uncreated = Path.Combine(root, "must-not-create", "settings.json"); Rejected(() => WritePreferences(uncreated, settings));
                Check(!Directory.Exists(Path.GetDirectoryName(uncreated)), "Invalid TextSize created a directory before validation.");
                CheckTextSizeCompanions(settings, style);
            }
            Check(ReferenceEquals(lazy, SettingsLazy()) && !LazyCreated(lazy), "TextSize IO initialized global Current.");
        });

        Case("restoring text size changes only future text and strictly rejects invalid values without document history edits", () => {
            var factory = new EditorView(); RestoreToolPreferences(factory);
            var editor = ArrowEditor(ExistingText(factory, 18.5, "Existing text", new Point(20, 30)));
            RestoreToolPreferences(editor);
            var pixels = PixelHash(editor.ExportImage()); var revision = editor.Revision; var undo = UndoDepth(editor);
            Call(editor, "RestoreTextSize", PreferredTextSize); CheckTextDefault(editor, PreferredTextSize);
            Check((double)Get(First(editor), "FontSize")! == 18.5 && editor.SelectedTextSize == 18.5, "Restore resized selected existing text.");
            foreach (var invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, 11.99, 240.01 }) {
                Rejected(() => Call(editor, "RestoreTextSize", invalid)); CheckTextDefault(editor, PreferredTextSize);
            }
            foreach (var boundary in new[] { 12.0, 240.0, PreferredTextSize }) { Call(editor, "RestoreTextSize", boundary); CheckTextDefault(editor, boundary); }
            Check(PixelHash(editor.ExportImage()) == pixels && editor.Revision == revision && UndoDepth(editor) == undo, "Restore changed selected pixels/revision/undo.");
            CheckAllToolAppearances(editor);
        });

        Case("active text size edits keep selection undo and future defaults independent across tools and new documents", () => {
            var factory = new EditorView(); RestoreToolPreferences(factory);
            var editor = ArrowEditor(ExistingText(factory, 18.5, "First", new Point(20, 20)), ExistingText(factory, 22.25, "Second", new Point(20, 120)));
            RestoreToolPreferences(editor); Call(editor, "RestoreTextSize", PreferredTextSize);
            Call(editor, "SelectItem", First(editor));
            Check(editor.SelectedTextSize == 18.5, "Selection did not preserve the existing text size."); CheckTextDefault(editor, PreferredTextSize);
            var initial = PixelHash(editor.ExportImage()); editor.TextSize = 41.25;
            Check((double)Get(First(editor), "FontSize")! == 41.25 && (double)Get(Items(editor)[1]!, "FontSize")! == 22.25, "Active resize did not affect only selected text.");
            Check(UndoDepth(editor) == 1, "One active text resize did not produce one document undo."); CheckTextDefault(editor, 41.25);
            editor.Undo(); Check(PixelHash(editor.ExportImage()) == initial, "Text-size undo did not restore existing pixels."); CheckTextDefault(editor, 41.25);
            editor.Redo(); Check((double)Get(First(editor), "FontSize")! == 41.25, "Text-size redo lost the active edit."); CheckTextDefault(editor, 41.25);
            editor.SelectAll(); editor.TextSize = 5;
            foreach (var item in Items(editor).Cast<object>()) Check((double)Get(item, "FontSize")! == 12, "Existing text setter no longer clamps and edits all selected Text.");
            editor.TextSize = 500;
            foreach (var item in Items(editor).Cast<object>()) Check((double)Get(item, "FontSize")! == 240, "Upper clamp did not preserve selected text edits.");
            Rejected(() => editor.TextSize = double.NaN); CheckTextDefault(editor, 240);
            foreach (var tool in ToolSamples.Select(sample => sample.Tool)) { editor.SetTool(tool); CheckTextDefault(editor, 240); }
            var loaded = Array.CreateInstance(ItemType, 1); loaded.SetValue(ExistingText(factory, 16, "Loaded", new Point(20, 20)), 0);
            Call(editor, "RestoreSession", ImageTools.Solid(320, 240, Colors.White), loaded, false, true); editor.SelectAll();
            Check(editor.SelectedTextSize == 16, "Loaded existing text changed size."); CheckTextDefault(editor, 240);
            editor.LoadImage(ImageTools.Solid(320, 240, Colors.White)); CheckTextDefault(editor, 240);
            Call(editor, "NewCollage"); CheckTextDefault(editor, 240); CheckAllToolAppearances(editor);
        });

        Case("actual new text draft and commit use remembered fractional size while existing text edit preserves its own size", () => {
            var factory = new EditorView(); RestoreToolPreferences(factory);
            var editor = ArrowEditor(ExistingText(factory, 16, "Existing", new Point(20, 20)));
            RestoreToolPreferences(editor); Call(editor, "RestoreTextSize", PreferredTextSize); editor.SetTool(AnnotationTool.Rectangle);
            var oldPixels = PixelHash(editor.ExportImage());
            Call(editor, "BeginTextEdit", (object?)null, new Rect(20, 110, 270, 100), (object?)null);
            var draft = typeof(EditorView).GetField("_textDraft", Any)!.GetValue(editor)!;
            var textEditor = (TextBox)typeof(EditorView).GetField("_textEditor", Any)!.GetValue(editor)!;
            Check((double)Get(draft, "FontSize")! == PreferredTextSize && textEditor.FontSize == PreferredTextSize, "Actual text draft or TextBox did not use remembered size.");
            Check(TopLevel.GetTopLevel(editor) is null && Application.Current!.ApplicationLifetime is null, "Actual text fixture attached to a native host.");
            textEditor.Text = "Remembered 37.5"; editor.CommitTextEdit();
            Check(Items(editor).Count == 2 && (double)Get(Items(editor)[0]!, "FontSize")! == 16 && (double)Get(Items(editor)[1]!, "FontSize")! == PreferredTextSize, "Text commit lost remembered size or resized old text.");
            Check(UndoDepth(editor) == 1, "Actual text creation did not produce one undo.");
            editor.Undo(); Check(Items(editor).Count == 1 && PixelHash(editor.ExportImage()) == oldPixels, "Text creation undo changed old text."); CheckTextDefault(editor, PreferredTextSize);
            editor.Redo(); Check((double)Get(Items(editor)[1]!, "FontSize")! == PreferredTextSize, "Text creation redo lost fractional size.");
            Call(editor, "BeginTextEdit", First(editor), (object?)null, (object?)null);
            var oldDraft = typeof(EditorView).GetField("_textDraft", Any)!.GetValue(editor)!;
            Check((double)Get(oldDraft, "FontSize")! == 16 && textEditor.FontSize == 16, "Editing old text silently applied the new default.");
            CheckTextDefault(editor, PreferredTextSize); editor.CancelTextEdit(); CheckAllToolAppearances(editor);
        });

        Case("text size input helper preserves displayed precision and validates changes across cultures without a Window", () => {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Current was already loaded.");
            var windowType = Product.GetType("SnipFlow.MainWindow", true)!;
            var helper = windowType.GetMethod("TryGetTextSizeChange", Any, null,
                new[] { typeof(string), typeof(double), typeof(CultureInfo), typeof(double).MakeByRefType() }, null);
            Check(helper is { IsStatic: true } && helper.ReturnType == typeof(bool), "Pure TryGetTextSizeChange helper is missing.");
            var inputs = new (string? Input, double Current, string Culture, bool Changed, double Normalized)[] {
                ("42.1", 42.125, "en-US", false, 0),
                ("32", 32, "en-US", false, 0),
                ("32.000", 32, "en-US", false, 0),
                (" 32.0 ", 32, "en-US", false, 0),
                ("37.500", 37.5, "en-US", false, 0),
                (null, 26, "en-US", false, 0),
                ("", 26, "en-US", false, 0),
                ("   ", 26, "en-US", false, 0),
                ("NaN", 26, "en-US", false, 0),
                ("Infinity", 26, "en-US", false, 0),
                ("-Infinity", 26, "en-US", false, 0),
                ("∞", 26, "en-US", false, 0),
                ("1e999", 26, "en-US", false, 0),
                ("invalid", 26, "en-US", false, 0),
                ("37.5", 26, "en-US", true, 37.5),
                ("12", 26, "en-US", true, 12),
                ("240", 26, "en-US", true, 240),
                ("-10", 26, "en-US", true, 12),
                ("999", 26, "en-US", true, 240),
                ("12", 12, "en-US", false, 0),
                ("240.0", 240, "en-US", false, 0),
                ("-10", 12, "en-US", false, 0),
                ("999", 240, "en-US", false, 0),
                ("42,1", 42.125, "de-DE", false, 0),
                ("32,000", 32, "de-DE", false, 0),
                ("37,5", 26, "de-DE", true, 37.5),
                ("11,5", 26, "de-DE", true, 12),
                ("240,5", 26, "de-DE", true, 240),
                ("37,5", 26, "fr-FR", true, 37.5)
            };
            foreach (var input in inputs) {
                var culture = CultureInfo.GetCultureInfo(input.Culture);
                object?[] args = { input.Input, input.Current, culture, 0d };
                var changed = (bool)helper!.Invoke(null, args)!;
                Check(changed == input.Changed, $"Input '{input.Input}' at {input.Current} in {input.Culture} changed={changed}.");
                Check((double)args[3]! == (changed ? input.Normalized : input.Current), "Input helper returned the wrong parsed/clamped or unchanged size.");
            }
            foreach (var cultureName in new[] { "en-US", "de-DE", "fr-FR", "zh-TW" }) {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                foreach (var current in new[] { 12.125, 37.5625, 42.125, 239.875 }) {
                    object?[] args = { current.ToString("0.#", culture), current, culture, 0d };
                    Check(!(bool)helper!.Invoke(null, args)!, "Unedited one-digit formatter output changed the original fractional preference.");
                    Check((double)args[3]! == current, "Unedited formatter output lost the original precision in its out value.");
                }
            }
            Check(ReferenceEquals(lazy, SettingsLazy()) && !LazyCreated(lazy), "Pure text input helper initialized Current.");
            Check(Application.Current!.ApplicationLifetime is null, "Pure text input test started an application lifetime.");
        });
    }
}

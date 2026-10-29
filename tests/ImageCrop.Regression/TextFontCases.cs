using System.Collections;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static Type TextFontsType => Product.GetType("SnipFlow.Editor.TextFonts", true)!;
    static Type FontPickerType => Product.GetType("SnipFlow.Editor.TextFontPicker", true)!;
    const string MissingFontFamily = "SnipFlow Regression Missing Typeface 20261029";
    static string[] AvailableFonts => ((IEnumerable<string>)TextFontsType.GetProperty("AvailableFamilies", Any)!.GetValue(null)!).ToArray();
    static string? preferredFontFamily;
    static string PreferredFontFamily => preferredFontFamily ??= new[] { "DejaVu Sans Mono", "Consolas", "Liberation Mono", "Courier New" }
        .Concat(AvailableFonts).First(family => AvailableFonts.Contains(family, StringComparer.Ordinal) && (bool)Static(TextFontsType, "IsInstalled", family)!);
    static string FontOf(object item) => (string)Get(item, "TextFontFamily")!;
    static object FontText(string family, string text = "iiii WWWW 0123456789", Point? start = null)
    {
        var item = Activator.CreateInstance(ItemType, true)!;
        Set(item, "Tool", AnnotationTool.Text); Set(item, "Start", start ?? new Point(20, 20)); Set(item, "End", start ?? new Point(20, 20));
        Set(item, "Color", Colors.Black); Set(item, "Width", 4.0); Set(item, "FontSize", 32.0); Set(item, "Text", text); Set(item, "TextFontFamily", family);
        return item;
    }
    static (string First, string Second) DifferentMetricFonts()
    {
        var first = PreferredFontFamily; var item = FontText(first);
        var width = ((TextLayout)Call(item, "FormatText")!).WidthIncludingTrailingWhitespace;
        foreach (var family in new[] { "DejaVu Sans", "Segoe UI", "Arial", "Liberation Sans", "Times New Roman" }.Concat(AvailableFonts).Distinct().Take(100)) {
            if (!(bool)Static(TextFontsType, "IsInstalled", family)! || family == first) continue;
            var other = ((TextLayout)Call(FontText(family), "FormatText")!).WidthIncludingTrailingWhitespace;
            if (Math.Abs(other - width) > .25) return (first, family);
        }
        throw new InvalidOperationException("Two real installed fonts with different ASCII metrics are required; first=" + first);
    }
    static void CheckTextBoxTypeface(TextBox box, Typeface face)
    {
        Check(box.FontFamily.Equals(face.FontFamily) && box.FontWeight == face.Weight && box.FontStyle == face.Style && box.FontStretch == face.Stretch,
            "Actual TextBox family/weight/style/stretch differs from the product's effective typeface.");
    }
    static void CheckFontCompanions(object settings, object style)
    {
        CheckTextSizeCompanions(settings, style); Check((double)Get(settings, "TextSize")! == PreferredTextSize, "Font IO replaced remembered TextSize.");
        Check((AnnotationTool)Get(settings, "LastAnnotationTool")! == AnnotationTool.Text, "Font IO replaced last-tool preference.");
    }
    static string[] InvalidFontNames => new[] { "Bad,Family", "Bad#Family", "a/b", "a\\b", "http:font", "a;b", "Bad\nFamily", "Bad\0Family", new string('A', 129) };

    static void TextFontCases()
    {
        var root = Path.Combine(Output, "text-font"); Directory.CreateDirectory(root);
        var style = Style("Circle", "Diamond", "Small", "Large", "Dash"); var settingsPath = Path.Combine(root, "settings.json");

        Case("font catalog normalization and missing-family rendering retain the requested font safely", () => {
            Check((string)TextFontsType.GetField("DefaultFamily", Any)!.GetValue(null)! == "", "Legacy default font changed.");
            var fonts = AvailableFonts; Check(fonts.Length > 0 && fonts.All(family => family.Length > 0 && (bool)Static(TextFontsType, "IsInstalled", family)!), "Catalog has no installed fonts or includes non-installed entries.");
            Check(fonts.Distinct(StringComparer.OrdinalIgnoreCase).Count() == fonts.Length, "Catalog has duplicate names.");
            Check(fonts.SequenceEqual(fonts.Order(StringComparer.OrdinalIgnoreCase)) || fonts.SequenceEqual(fonts.Order(StringComparer.CurrentCultureIgnoreCase)), "Catalog is not sorted.");
            Check((string)Static(TextFontsType, "NormalizeFamily", "  " + PreferredFontFamily + "  ")! == PreferredFontFamily, "Font normalization lost trimmed installed name.");
            Check((string)Static(TextFontsType, "NormalizeFamily", "   ")! == "", "Whitespace did not normalize to legacy font.");
            Check((string)Static(TextFontsType, "NormalizeFamily", new string('A', 128))! == new string('A', 128), "Valid maximum-length missing name was rejected.");
            var tryNormalize = TextFontsType.GetMethod("TryNormalizeFamily", Any)!;
            foreach (var invalid in InvalidFontNames.Cast<string?>().Append(null)) {
                object?[] args = { invalid, null }; Check(!(bool)tryNormalize.Invoke(null, args)!, "Unsafe font passed TryNormalizeFamily.");
                Rejected(() => Static(TextFontsType, "NormalizeFamily", (object?)invalid));
            }
            Check(!(bool)Static(TextFontsType, "IsInstalled", MissingFontFamily)!, "Missing font fixture unexpectedly exists.");
            var missing = FontText(MissingFontFamily); var legacy = FontText("");
            Check(FontOf(missing) == MissingFontFamily, "Missing font request was discarded.");
            var effective = (Typeface)Get(missing, "EffectiveTextTypeface")!;
            Check(effective.Equals((Typeface)Static(TextFontsType, "ResolveTypeface", "")!), "Missing font did not resolve to legacy fallback.");
            using var actual = RenderItem(missing, 800, 100); using var expected = RenderItem(legacy, 800, 100);
            Check(PixelHash(actual) == PixelHash(expected) && OpaqueCount(Pixels(actual)) > 0, "Missing-family fallback did not render the legacy text.");
            Save("font-missing-fallback.png", actual);
        });

        Case("different installed fonts change real layout pixels and hit bounds while clone copy and caches preserve each item", () => {
            var pair = DifferentMetricFonts(); var item = FontText(pair.First); var clone = Call(item, "Clone")!;
            Check(FontOf(clone) == pair.First && (bool)Call(item, "ContentEquals", clone)!, "Font clone/equality lost its family.");
            var firstLayout = (TextLayout)Call(item, "FormatText")!; var firstWidth = firstLayout.WidthIncludingTrailingWhitespace;
            using var first = RenderItem(item, 1000, 110); var firstHash = PixelHash(first);
            Set(item, "TextFontFamily", pair.Second); var secondLayout = (TextLayout)Call(item, "FormatText")!;
            Check(!ReferenceEquals(firstLayout, secondLayout) && Math.Abs(firstWidth - secondLayout.WidthIncludingTrailingWhitespace) > .25, "Font-only edit reused cached layout or unchanged metrics.");
            using var second = RenderItem(item, 1000, 110); Check(PixelHash(second) != firstHash && OpaqueCount(Pixels(second)) > 0, "Different installed fonts rendered the same pixels.");
            Check(!(bool)Call(item, "ContentEquals", clone)!, "Font-only edit was ignored by equality.");
            var bounds = Bounds(item); Check((bool)Call(item, "HitTest", bounds.Center, 0d)! && !(bool)Call(item, "HitTest", new Point(bounds.Right + 10, bounds.Center.Y), 0d)!, "Font hit bounds do not match updated layout.");
            Call(clone, "CopyFrom", item); Check(FontOf(clone) == pair.Second && (bool)Call(item, "ContentEquals", clone)!, "CopyFrom lost font.");
            Set(item, "TextFontFamily", pair.First); using var restored = RenderItem(item, 1000, 110); Check(PixelHash(restored) == firstHash, "Returning to the first font did not restore pixels.");
            var independent = FontText(pair.Second); Check(((TextLayout)Call(independent, "FormatText")!).WidthIncludingTrailingWhitespace == secondLayout.WidthIncludingTrailingWhitespace, "Per-item typeface leaked from another text item.");
            File.WriteAllText(Path.Combine(root, "installed-font-metrics.json"), JsonSerializer.Serialize(new { firstFamily = pair.First, secondFamily = pair.Second, firstWidth, secondWidth = secondLayout.WidthIncludingTrailingWhitespace, firstPixelSha256 = firstHash, secondPixelSha256 = PixelHash(second) }, new JsonSerializerOptions { WriteIndented = true }));
            Save("font-first.png", first); Save("font-second.png", second);
        });

        Case("font preferences roundtrip trim missing names and fall back per field with duplicate last values", () => {
            var settings = NewPreferenceSettings(style); Set(settings, "TextFontFamily", "  " + PreferredFontFamily + "  "); WritePreferences(settingsPath, settings);
            Check(PreferenceJson(settingsPath)["TextFontFamily"]!.GetValue<string>() == PreferredFontFamily, "Font writer did not normalize name.");
            Check((string)Get(settings, "TextFontFamily")! == "  " + PreferredFontFamily + "  ", "Writer mutated caller font.");
            var loaded = ReadPreferences(settingsPath); Check(FontOf(loaded) == PreferredFontFamily, "Font roundtrip lost requested name."); CheckFontCompanions(loaded, style);
            var missing = NewPreferenceSettings(style); Set(missing, "TextFontFamily", MissingFontFamily); var missingPath = Path.Combine(root, "missing-installed.json"); WritePreferences(missingPath, missing);
            Check(FontOf(ReadPreferences(missingPath)) == MissingFontFamily, "Uninstalled font name did not survive settings IO.");
            var invalid = new List<JsonNode?> { null, JsonValue.Create(false), JsonValue.Create(17), new JsonObject(), new JsonArray() };
            invalid.AddRange(InvalidFontNames.Select(name => (JsonNode?)JsonValue.Create(name)));
            for (var n = -1; n < invalid.Count; n++) {
                var json = PreferenceJson(settingsPath); if (n < 0) json.Remove("TextFontFamily"); else json["TextFontFamily"] = invalid[n]?.DeepClone();
                var path = Path.Combine(root, "invalid-font-" + n + ".json"); File.WriteAllText(path, json.ToJsonString());
                var fallback = ReadPreferences(path); Check(FontOf(fallback) == "", "Invalid/missing font did not use legacy default."); CheckFontCompanions(fallback, style);
            }
            var original = File.ReadAllText(settingsPath).Trim(); var duplicate = Path.Combine(root, "duplicate-font.json");
            File.WriteAllText(duplicate, "{\"TextFontFamily\":false," + original[1..]); Check(FontOf(ReadPreferences(duplicate)) == PreferredFontFamily, "Last valid font lost.");
            File.WriteAllText(duplicate, original[..^1] + ",\"TextFontFamily\":false}"); var fallbackLast = ReadPreferences(duplicate); Check(FontOf(fallbackLast) == "", "Last invalid font did not default."); CheckFontCompanions(fallbackLast, style);
        });

        Case("invalid font writes reject before publication or directory creation without loading Current", () => {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Current was already loaded."); var before = Hash(File.ReadAllBytes(settingsPath));
            foreach (var invalid in InvalidFontNames.Cast<string?>().Append(null)) {
                var settings = NewPreferenceSettings(style); Set(settings, "TextFontFamily", invalid);
                Rejected(() => WritePreferences(settingsPath, settings)); Check(Hash(File.ReadAllBytes(settingsPath)) == before && !File.Exists(settingsPath + ".tmp"), "Bad font changed published settings.");
                var uncreated = Path.Combine(root, "must-not-create", "settings.json"); Rejected(() => WritePreferences(uncreated, settings)); Check(!Directory.Exists(Path.GetDirectoryName(uncreated)), "Bad font caused IO before validation.");
                CheckFontCompanions(settings, style);
            }
            Check(ReferenceEquals(lazy, SettingsLazy()) && !LazyCreated(lazy), "Font IO loaded Current.");
        });

        Case("restoring font defaults leaves selected text pixels revision and undo unchanged and rejects unsafe names", () => {
            var editor = ArrowEditor(FontText("", "Existing text")); RestoreToolPreferences(editor); Call(editor, "RestoreTextSize", PreferredTextSize);
            var before = PixelHash(editor.ExportImage()); var revision = editor.Revision; var depth = UndoDepth(editor);
            Call(editor, "RestoreTextFontFamily", PreferredFontFamily);
            Check((string)Get(editor, "TextFontFamily")! == PreferredFontFamily && (string)Get(editor, "SelectedTextFontFamily")! == "" && FontOf(First(editor)) == "", "Restore changed selected text instead of only defaults.");
            foreach (var invalid in InvalidFontNames.Cast<string?>().Append(null)) {
                Rejected(() => Call(editor, "RestoreTextFontFamily", invalid)); Rejected(() => Set(editor, "TextFontFamily", invalid));
                Check((string)Get(editor, "TextFontFamily")! == PreferredFontFamily && FontOf(First(editor)) == "", "Unsafe font mutated default/document.");
            }
            Check(PixelHash(editor.ExportImage()) == before && editor.Revision == revision && UndoDepth(editor) == depth, "Font restore edited document pixels/history.");
            CheckAllToolAppearances(editor); CheckTextDefault(editor, PreferredTextSize);
        });

        Case("font selection edits preserve individual sizes and nontext data while undo and new documents retain future default", () => {
            var pair = DifferentMetricFonts(); var a = FontText(pair.First, "First", new Point(20, 20)); var b = FontText("", "Second", new Point(20, 100)); Set(a, "FontSize", 18.5); Set(b, "FontSize", 22.25);
            var editor = ArrowEditor(a, b, Arrow(new Point(20, 210), new Point(290, 210))); RestoreToolPreferences(editor); Call(editor, "RestoreTextSize", PreferredTextSize); Call(editor, "RestoreTextFontFamily", pair.First);
            Check(Get(editor, "SelectedTextFontFamily") is null, "Nontext primary reports a selected font.");
            var before = PixelHash(editor.ExportImage()); Set(editor, "TextFontFamily", pair.Second);
            Check(FontOf(Items(editor)[0]!) == pair.Second && FontOf(Items(editor)[1]!) == pair.Second && FontOf(Items(editor)[2]!) == "", "Font batch edit skipped Text or modified nontext.");
            Check((double)Get(Items(editor)[0]!, "FontSize")! == 18.5 && (double)Get(Items(editor)[1]!, "FontSize")! == 22.25, "Font edit replaced per-item size.");
            Check(UndoDepth(editor) == 1, "Font batch edit should produce one undo."); editor.Undo(); Check(PixelHash(editor.ExportImage()) == before, "Font undo did not restore old text pixels.");
            Check((string)Get(editor, "TextFontFamily")! == pair.Second, "Undo overwrote future font default."); editor.Redo();
            Set(editor, "TextFontFamily", pair.Second); Check(UndoDepth(editor) == 1, "Same font created another document undo.");
            editor.SetTool(AnnotationTool.Rectangle); var fontText = Call(editor, "CreateShapeAnnotation", AnnotationTool.Text, new Point(20, 20))!; Check(FontOf(fontText) == pair.Second, "Text factory used active tool instead of remembered font.");
            var loaded = Array.CreateInstance(ItemType, 1); loaded.SetValue(FontText(pair.First), 0); Call(editor, "RestoreSession", ImageTools.Solid(320, 240, Colors.White), loaded, false, true); editor.SelectAll();
            Check((string)Get(editor, "SelectedTextFontFamily")! == pair.First && (string)Get(editor, "TextFontFamily")! == pair.Second, "Selection/restore overwrote future font.");
            editor.LoadImage(ImageTools.Solid(320, 240, Colors.White)); Call(editor, "NewCollage"); Check((string)Get(editor, "TextFontFamily")! == pair.Second, "New document forgot font default."); CheckAllToolAppearances(editor); CheckTextDefault(editor, PreferredTextSize);
        });

        Case("actual text editor applies each effective typeface and commits remembered font without changing old text", () => {
            var editor = ArrowEditor(FontText("", "Old text")); RestoreToolPreferences(editor); Call(editor, "RestoreTextSize", PreferredTextSize); Call(editor, "RestoreTextFontFamily", PreferredFontFamily); editor.SetTool(AnnotationTool.Rectangle);
            Call(editor, "BeginTextEdit", (object?)null, new Rect(20, 100, 280, 100), (object?)null);
            var draft = typeof(EditorView).GetField("_textDraft", Any)!.GetValue(editor)!; var box = (TextBox)typeof(EditorView).GetField("_textEditor", Any)!.GetValue(editor)!;
            Check(FontOf(draft) == PreferredFontFamily, "New actual draft forgot remembered font."); CheckTextBoxTypeface(box, (Typeface)Get(draft, "EffectiveTextTypeface")!);
            box.Text = "Font preference 文字"; editor.CommitTextEdit(); Check(Items(editor).Count == 2 && FontOf(Items(editor)[0]!) == "" && FontOf(Items(editor)[1]!) == PreferredFontFamily, "Actual commit lost font or rewrote old text.");
            editor.Undo(); Check(Items(editor).Count == 1 && (string)Get(editor, "TextFontFamily")! == PreferredFontFamily, "Undo changed future font."); editor.Redo(); Check(FontOf(Items(editor)[1]!) == PreferredFontFamily, "Redo lost font.");
            Call(editor, "BeginTextEdit", First(editor), (object?)null, (object?)null); var oldDraft = typeof(EditorView).GetField("_textDraft", Any)!.GetValue(editor)!;
            Check(FontOf(oldDraft) == "", "Editing old text replaced its font with the new default."); CheckTextBoxTypeface(box, (Typeface)Get(oldDraft, "EffectiveTextTypeface")!); editor.CancelTextEdit();
            Call(editor, "RestoreTextFontFamily", MissingFontFamily); Call(editor, "BeginTextEdit", (object?)null, new Rect(20, 160, 280, 70), (object?)null);
            var missingDraft = typeof(EditorView).GetField("_textDraft", Any)!.GetValue(editor)!; Check(FontOf(missingDraft) == MissingFontFamily, "New draft discarded missing requested font."); CheckTextBoxTypeface(box, (Typeface)Static(TextFontsType, "ResolveTypeface", "")!); editor.CancelTextEdit();
            Check(TopLevel.GetTopLevel(editor) is null && Application.Current!.ApplicationLifetime is null, "Font edit fixture created a native host.");
        });

        var project = Path.Combine(root, "two-fonts.snipflow");
        Case("schema 8 project preserves two independent text fonts sizes and exact same-platform pixels", () => {
            var pair = DifferentMetricFonts(); var first = FontText(pair.First, "First font", new Point(20, 20)); var second = FontText(pair.Second, "Second font", new Point(20, 120)); Set(first, "FontSize", 18.5); Set(second, "FontSize", 37.5);
            var editor = ArrowEditor(first, second); var before = PixelHash(editor.ExportImage()); Static(StoreType, "SaveProject", editor, project);
            using (var archive = ZipFile.OpenRead(project)) { var manifest = ReadManifest(archive); Check((int)manifest["SchemaVersion"]! == 8, "Font project did not use schema 8."); Check((string?)manifest["Annotations"]![0]!["TextFontFamily"] == pair.First && (string?)manifest["Annotations"]![1]!["TextFontFamily"] == pair.Second, "Manifest lost per-text font."); }
            var reopened = new EditorView(); Static(StoreType, "RestoreProject", reopened, project);
            Check(FontOf(Items(reopened)[0]!) == pair.First && FontOf(Items(reopened)[1]!) == pair.Second && (double)Get(Items(reopened)[0]!, "FontSize")! == 18.5 && (double)Get(Items(reopened)[1]!, "FontSize")! == 37.5, "Reopened text preferences differ.");
            Check(PixelHash(reopened.ExportImage()) == before && !reopened.HasEdits, "Font project roundtrip changed pixels/saved state.");
        });

        Case("legacy schemas 1 through 7 retain legacy text while unsafe or misplaced fonts reject atomically", () => {
            // Use the same sizes as the real font project before comparing legacy render output.
            var legacyReference = ArrowEditor(FontText("", "First font", new Point(20, 20)), FontText("", "Second font", new Point(20, 120))); Set(Items(legacyReference)[0]!, "FontSize", 18.5); Set(Items(legacyReference)[1]!, "FontSize", 37.5); var legacyPixels = PixelHash(legacyReference.ExportImage());
            foreach (var schema in Enumerable.Range(1, 7)) {
                var path = MutateProject(project, "legacy-text-font-" + schema + ".snipflow", json => { json["SchemaVersion"] = schema; foreach (var item in json["Annotations"]!.AsArray()) item!.AsObject().Remove("TextFontFamily"); });
                var editor = new EditorView(); Static(StoreType, "RestoreProject", editor, path); Check(Items(editor).Cast<object>().All(item => FontOf(item) == "") && PixelHash(editor.ExportImage()) == legacyPixels, "Legacy schema " + schema + " text font/pixels changed.");
                var nonempty = MutateProject(project, "old-font-invalid-" + schema + ".snipflow", json => json["SchemaVersion"] = schema);
                var before = PixelHash(editor.ExportImage()); Rejected(() => Static(StoreType, "RestoreProject", editor, nonempty)); Check(PixelHash(editor.ExportImage()) == before, "Old schema accepted new font or replaced document.");
            }
            var unsafeValues = new List<JsonNode?> { null, JsonValue.Create(false), JsonValue.Create(42), new JsonObject() }; unsafeValues.AddRange(InvalidFontNames.Select(name => (JsonNode?)JsonValue.Create(name)));
            for (var n = 0; n < unsafeValues.Count; n++) {
                var value = unsafeValues[n]; var path = MutateProject(project, "unsafe-font-" + n + ".snipflow", json => json["Annotations"]![0]!["TextFontFamily"] = value?.DeepClone());
                var editor = ArrowEditor(FontText("", "Keep document")); var before = PixelHash(editor.ExportImage()); var revision = editor.Revision;
                Rejected(() => Static(StoreType, "RestoreProject", editor, path)); Check(PixelHash(editor.ExportImage()) == before && editor.Revision == revision, "Unsafe font project replaced open document.");
            }
            var nontext = MutateProject(project, "nontext-font.snipflow", json => json["Annotations"]![0]!["Tool"] = "Rectangle"); var target = ArrowEditor(Arrow(new Point(20, 80), new Point(280, 80))); var targetPixels = PixelHash(target.ExportImage()); Rejected(() => Static(StoreType, "RestoreProject", target, nontext)); Check(PixelHash(target.ExportImage()) == targetPixels, "Nontext font replaced document.");
            var invalidItem = FontText(PreferredFontFamily); Set(invalidItem, "Tool", AnnotationTool.Rectangle); var invalidSave = Path.Combine(root, "must-not-publish-nontext-font.snipflow"); Rejected(() => Static(StoreType, "SaveProject", ArrowEditor(invalidItem), invalidSave)); Check(!File.Exists(invalidSave), "Nontext font published.");
        });

        Case("real font picker programmatic value and language synchronization emit no user edits", () => {
            var picker = Activator.CreateInstance(FontPickerType)!; var edits = 0; EventHandler observer = (_, _) => edits++;
            FontPickerType.GetEvent("FontChanged")!.AddEventHandler(picker, observer);
            foreach (var value in new[] { "", PreferredFontFamily, MissingFontFamily, "" }) { Set(picker, "Value", value); Call(picker, "RefreshLanguage"); Check((string)Get(picker, "Value")! == value, "Font picker lost requested selection."); }
            Check(edits == 0, "Programmatic picker synchronization emitted a user preference edit.");
            foreach (var invalid in InvalidFontNames) { Rejected(() => Set(picker, "Value", invalid)); Check((string)Get(picker, "Value")! == "" && edits == 0, "Invalid picker value changed state or notified preferences."); }
            var combo = (ComboBox)FontPickerType.GetFields(Any).Single(field => field.FieldType == typeof(ComboBox)).GetValue(picker)!;
            Check(!combo.IsDropDownOpen && combo.Items.Count > 1, "Picker fixture has no installed choices or opened a popup.");
            var selectedFamily = (string)Get(combo.Items[1]!, "Family")!; combo.SelectedIndex = 1;
            Check(edits == 1 && (string)Get(picker, "Value")! == selectedFamily, "Real ComboBox selection did not emit exactly one preference edit.");
            combo.SelectedIndex = 1; Call(picker, "RefreshLanguage"); Check(edits == 1, "Same selection or refresh emitted another preference edit.");
            var inputGuard = Product.GetType("SnipFlow.MainWindow", true)!.GetMethod("IsFontPickerInput", Any, null, new[] { typeof(bool), typeof(KeyModifiers) }, null);
            Check(inputGuard is { IsStatic: true } && inputGuard.ReturnType == typeof(bool), "Pure font-picker shortcut guard is missing.");
            var routing = new[] { (KeyModifiers.None, true), (KeyModifiers.Shift, true), (KeyModifiers.Alt, true), (KeyModifiers.Control, false), (KeyModifiers.Control | KeyModifiers.Shift, false), (KeyModifiers.Meta, false) };
            foreach (var choosing in new[] { false, true }) foreach (var (modifiers, pickerInput) in routing) {
                Check((bool)inputGuard!.Invoke(null, new object[] { choosing, modifiers })! == (choosing && pickerInput), "Font-picker guard swallowed document modifiers or released picker input: " + modifiers);
            }
            Check(TopLevel.GetTopLevel((Control)picker) is null && Application.Current!.ApplicationLifetime is null, "Picker fixture created a native host.");
            FontPickerType.GetEvent("FontChanged")!.RemoveEventHandler(picker, observer);
        });
    }

    static void LastToolCases()
    {
        var root = Path.Combine(Output, "last-tool"); Directory.CreateDirectory(root); var path = Path.Combine(root, "settings.json"); var style = Style("Circle", "Diamond", "Small", "Large", "Dash");
        Case("last tool roundtrips fixed names and legacy numeric or malformed values preserve all other preferences", () => {
            Check((AnnotationTool)Get(Activator.CreateInstance(SettingsType)!, "LastAnnotationTool")! == AnnotationTool.Pen, "Legacy last tool default changed.");
            foreach (var tool in ToolSamples.Select(sample => sample.Tool).Prepend(AnnotationTool.Select)) {
                var settings = NewPreferenceSettings(style); Set(settings, "LastAnnotationTool", tool); WritePreferences(path, settings); var json = PreferenceJson(path);
                Check(json["LastAnnotationTool"]!.GetValue<string>() == tool.ToString(), "Last tool writer did not use fixed enum name."); Check((AnnotationTool)Get(ReadPreferences(path), "LastAnnotationTool")! == tool, "Last tool did not roundtrip.");
                json["LastAnnotationTool"] = (int)tool; File.WriteAllText(path, json.ToJsonString()); Check((AnnotationTool)Get(ReadPreferences(path), "LastAnnotationTool")! == tool, "Defined numeric last tool did not load.");
            }
            WritePreferences(path, NewPreferenceSettings(style));
            var invalid = new JsonNode?[] { null, JsonValue.Create(false), JsonValue.Create("Image"), JsonValue.Create("Unknown"), JsonValue.Create(999), new JsonObject(), new JsonArray() };
            for (var n = -1; n < invalid.Length; n++) {
                var json = PreferenceJson(path); if (n < 0) json.Remove("LastAnnotationTool"); else json["LastAnnotationTool"] = invalid[n]?.DeepClone(); var bad = Path.Combine(root, "invalid-" + n + ".json"); File.WriteAllText(bad, json.ToJsonString());
                var loaded = ReadPreferences(bad); Check((AnnotationTool)Get(loaded, "LastAnnotationTool")! == AnnotationTool.Pen, "Invalid last tool did not default."); CheckTextSizeCompanions(loaded, style); Check((double)Get(loaded, "TextSize")! == PreferredTextSize && FontOf(loaded) == PreferredFontFamily, "Last tool fallback lost text defaults.");
            }
            var original = File.ReadAllText(path).Trim(); var duplicate = Path.Combine(root, "duplicate.json"); File.WriteAllText(duplicate, "{\"LastAnnotationTool\":999," + original[1..]); Check((AnnotationTool)Get(ReadPreferences(duplicate), "LastAnnotationTool")! == AnnotationTool.Text, "Valid last duplicate was lost.");
            File.WriteAllText(duplicate, original[..^1] + ",\"LastAnnotationTool\":999}"); Check((AnnotationTool)Get(ReadPreferences(duplicate), "LastAnnotationTool")! == AnnotationTool.Pen, "Invalid last duplicate did not default.");
        });
        Case("invalid last tool writes are atomic and leave lazy Current unloaded", () => {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Current already loaded."); var before = Hash(File.ReadAllBytes(path));
            foreach (var tool in new[] { AnnotationTool.Image, (AnnotationTool)999, (AnnotationTool)(-1) }) {
                var settings = NewPreferenceSettings(style); Set(settings, "LastAnnotationTool", tool); Rejected(() => WritePreferences(path, settings)); Check(Hash(File.ReadAllBytes(path)) == before && !File.Exists(path + ".tmp"), "Invalid last tool changed settings publication.");
                var uncreated = Path.Combine(root, "must-not-create", "settings.json"); Rejected(() => WritePreferences(uncreated, settings)); Check(!Directory.Exists(Path.GetDirectoryName(uncreated)), "Invalid last tool created a directory.");
            }
            Check(ReferenceEquals(lazy, SettingsLazy()) && !LazyCreated(lazy), "Last-tool IO initialized Current.");
        });
    }
}

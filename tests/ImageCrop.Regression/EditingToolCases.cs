using System.Collections;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using SnipFlow.Editor;
using SnipFlow.Services;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static AnnotationTool ShapeTool => Enum.Parse<AnnotationTool>("Shape");
    static Type ShapeKindType => Product.GetType("SnipFlow.Editor.ShapeKind", true)!;
    static object Kind(string name) => Enum.Parse(ShapeKindType, name);
    static readonly string[] CommonShapes = { "RoundedRectangle", "Triangle", "RightTriangle", "Diamond", "Pentagon", "Hexagon", "Star", "Heart", "Plus", "Cloud", "Callout", "RightArrow", "LeftArrow", "UpArrow", "DownArrow", "DoubleArrow", "Trapezoid", "Parallelogram" };
    static object VectorShape(string kind, bool filled = false, int orientation = 0)
    {
        var item = Activator.CreateInstance(ItemType, true)!;
        Set(item, "Tool", ShapeTool); Set(item, "Start", new Point(60, 60)); Set(item, "End", new Point(240, 160));
        Set(item, "Color", Colors.Red); Set(item, "Width", 3.0);
        Set(item, "ShapeKind", Kind(kind)); Set(item, "ShapeFilled", filled); Set(item, "ShapeOrientation", orientation);
        return item;
    }
    static void ShapeFields(object item, string kind, bool filled, int orientation, string message)
    {
        Check(Get(item, "ShapeKind")!.ToString() == kind && (bool)Get(item, "ShapeFilled")! == filled && (int)Get(item, "ShapeOrientation")! == orientation, message);
    }
    static List<Button> PaletteButtons(object palette) => ((Control)palette).GetLogicalDescendants().OfType<Button>().ToList();
    static Delegate ObserveEvent(object control, string name, Action<object> observer)
    {
        var changed = control.GetType().GetEvent(name)!;
        Delegate handler = changed.EventHandlerType == typeof(EventHandler)
            ? new EventHandler((_, args) => observer(args))
            : (Delegate)typeof(Program).GetMethod(nameof(CreateStyleObserver), Any)!.MakeGenericMethod(changed.EventHandlerType!.GetGenericArguments()[0]).Invoke(null, new object[] { observer })!;
        changed.AddEventHandler(control, handler); return handler;
    }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void EditingToolCases()
    {
        Case("editing tools contract exposes common vector shapes and standalone palettes", () => {
            Check(Enum.TryParse<AnnotationTool>("Shape", out _), "Common vector shape tool is missing (expected failure on 0.8.11).");
            foreach (var name in CommonShapes) Check(Enum.IsDefined(ShapeKindType, Kind(name)), "Missing common shape " + name);
            Check(ItemType.GetProperty("ShapeOrientation", Any)?.PropertyType == typeof(int), "Shape orientation contract missing.");
            foreach (var name in new[] { "ArrowPresetBar", "ShapePicker" }) Check(Product.GetType("SnipFlow.Editor." + name) is not null, name + " missing.");
        });
        if (!Enum.TryParse<AnnotationTool>("Shape", out _)) return;

        Case("vector shape clone copy and equality include kind fill and orientation", () => {
            var original = VectorShape("Callout", true, 5); var clone = Call(original, "Clone")!;
            ShapeFields(clone, "Callout", true, 5, "Clone lost shape fields."); Check((bool)Call(original, "ContentEquals", clone)!, "Clone differs.");
            foreach (var field in new[] { "ShapeKind", "ShapeFilled", "ShapeOrientation" }) {
                var changed = Call(original, "Clone")!;
                Set(changed, field, field == "ShapeKind" ? Kind("Star") : field == "ShapeFilled" ? false : 6);
                Check(!(bool)Call(original, "ContentEquals", changed)!, "Equality ignores " + field);
                Call(clone, "CopyFrom", changed); Check((bool)Call(clone, "ContentEquals", changed)!, "CopyFrom ignores " + field);
            }
            Call(clone, "Offset", new Vector(7, 11)); ShapeFields(clone, "Callout", true, 6, "Move lost shape style.");
        });

        Case("all common shapes paint distinct geometry with selectable painted pixels", () => {
            var hashes = new HashSet<string>();
            foreach (var kind in CommonShapes) foreach (var filled in new[] { false, true }) {
                var item = VectorShape(kind, filled); using var image = RenderItem(item, 320, 240); var pixels = Pixels(image);
                Check(OpaqueCount(pixels) > 100, "Shape has no substantial paint: " + kind);
                if (filled) hashes.Add(PixelHash(image));
                var probes = 0;
                for (var y = 54; y < 168; y += 4) for (var x = 54; x < 248; x += 4)
                    if (At(pixels, x, y).A > 220) { Check((bool)Call(item, "HitTest", new Point(x + .5, y + .5), 1.0)!, "Paint is not selectable: " + kind + " " + filled + " @ " + x + "," + y); probes++; }
                Check(probes > 5, "Raster fixture did not reach a painted region.");
            }
            Check(hashes.Count == CommonShapes.Length, "Shape choices reuse identical filled geometry.");
        });
        Case("shape hit testing follows fill and outline rather than the rectangular bounds", () => {
            var outline = VectorShape("Triangle"); var filled = VectorShape("Triangle", true);
            var center = new Point(150, 120); var emptyCorner = new Point(65, 65); var edge = new Point(150, 60);
            Check(!(bool)Call(outline, "HitTest", center, .1)! && (bool)Call(filled, "HitTest", center, .1)!, "Fill does not control interior selection.");
            Check(!(bool)Call(outline, "HitTest", emptyCorner, .1)! && !(bool)Call(filled, "HitTest", emptyCorner, .1)!, "Shape uses a bounding-box hit target.");
            Check((bool)Call(outline, "HitTest", edge, .2)!, "Outline edge is not selectable.");
        });

        Case("shape factories retain independent defaults and defaults restore never edits selection", () => {
            var editor = ArrowEditor(VectorShape("Triangle", false, 3)); var before = PixelHash(editor.ExportImage()); var revision = editor.Revision;
            Call(editor, "RestoreShapeOptions", Kind("Star"), true); Call(editor, "RestoreToolAppearance", ShapeTool, ToolAppearance("#C04090", 6.25));
            ShapeFields(First(editor), "Triangle", false, 3, "Restoring defaults edited selection."); Check(editor.Revision == revision && PixelHash(editor.ExportImage()) == before, "Defaults restore mutated document.");
            var item = Call(editor, "CreateShapeAnnotation", ShapeTool, new Point(10, 10))!;
            ShapeFields(item, "Star", true, 0, "Factory ignored shape defaults."); CheckItemAppearance(item, Color.Parse("#C04090"), 6.25);
            foreach (var tool in new[] { AnnotationTool.Arrow, AnnotationTool.Rectangle, AnnotationTool.Ellipse, AnnotationTool.Text }) {
                editor.SetTool(tool); ShapeFields(Call(editor, "CreateShapeAnnotation", ShapeTool, new Point(5, 5))!, "Star", true, 0, "Switching tools erased shape defaults.");
            }
        });

        Case("selected vector shapes move resize and undo as editable objects", () => {
            var editor = ArrowEditor(VectorShape("Star", true, 5)); var original = Bounds(First(editor)); var before = PixelHash(editor.ExportImage());
            editor.NudgeSelection(8, 12); EqualRect(Bounds(First(editor)), original.Translate(new Vector(8, 12)), "Nudge failed.");
            ShapeFields(First(editor), "Star", true, 5, "Nudge lost shape data."); editor.Undo(); Check(PixelHash(editor.ExportImage()) == before, "Move undo lost original geometry.");
            Call(editor, "BeginTransform", Enum.Parse(HandleType, "BottomRight"), original.BottomRight); Call(editor, "UpdateTransform", original.BottomRight + new Vector(35, 20)); Call(editor, "CompleteGesture");
            EqualRect(Bounds(First(editor)), new Rect(original.X, original.Y, original.Width + 35, original.Height + 20), "Resize failed.");
            ShapeFields(First(editor), "Star", true, 5, "Resize lost orientation."); var resized = PixelHash(editor.ExportImage()); editor.Undo(); Check(PixelHash(editor.ExportImage()) == before, "Resize undo failed."); editor.Redo(); Check(PixelHash(editor.ExportImage()) == resized, "Resize redo failed.");
            editor.DuplicateSelection(); Check(Items(editor).Count == 2, "Shape duplication failed."); ShapeFields(Items(editor)[1]!, "Star", true, 5, "Duplicate lost shape properties.");
        });
        Case("shape style selection changes are undoable and retain future defaults", () => {
            var editor = ArrowEditor(VectorShape("Triangle", false, 3), VectorShape("Star", true, 5), Arrow(new Point(20, 210), new Point(290, 210)));
            var before = PixelHash(editor.ExportImage()); Set(editor, "ShapeKind", Kind("Cloud"));
            ShapeFields(Items(editor)[0]!, "Cloud", false, 3, "Kind changed fill/orientation."); ShapeFields(Items(editor)[1]!, "Cloud", true, 5, "Kind skipped mixed selection.");
            Check((AnnotationTool)Get(Items(editor)[2]!, "Tool")! == AnnotationTool.Arrow && UndoDepth(editor) == 1, "Shape edit changed nonshape or produced multiple undo steps.");
            editor.Undo(); Check(PixelHash(editor.ExportImage()) == before && Get(editor, "ShapeKind")!.ToString() == "Cloud", "Undo replaced future shape default."); editor.Redo();
            Set(editor, "ShapeFilled", true); Check((bool)Get(Items(editor)[0]!, "ShapeFilled")! && (bool)Get(Items(editor)[1]!, "ShapeFilled")!, "Fill skipped selected shape.");
            Call(editor, "SelectItem", Items(editor)[0]); Check(Get(editor, "SelectedShapeKind")!.ToString() == "Cloud" && (bool)Get(editor, "SelectedShapeFilled")!, "Selection panel values missing.");
            var invalid = Enum.ToObject(ShapeKindType, 999); var revision = editor.Revision; Rejected(() => Set(editor, "ShapeKind", invalid)); Rejected(() => Call(editor, "RestoreShapeOptions", invalid, false));
            Check(editor.Revision == revision && Get(editor, "ShapeKind")!.ToString() == "Cloud" && (bool)Get(editor, "ShapeFilled")!, "Invalid kind changed defaults/document.");
            editor.LoadImage(SnipFlow.Imaging.ImageTools.Solid(320, 240, Colors.White)); ShapeFields(Call(editor, "CreateShapeAnnotation", ShapeTool, default(Point))!, "Cloud", true, 0, "New image lost future shape format.");
        });

        Case("D4 canvas rotations and flips preserve asymmetric nonuniform shape geometry", () => {
            var commandType = Product.GetType("SnipFlow.Editor.ImageTransformCommand", true)!; var orientationType = Product.GetType("SnipFlow.Editor.ImageOrientation", true)!;
            foreach (var command in new[] { "RotateRight", "RotateLeft", "FlipHorizontal", "FlipVertical" }) foreach (var originalOrientation in new[] { 0, 1, 4, 7 }) {
                var editor = ArrowEditor(VectorShape("Callout", true, originalOrientation)); using var before = editor.ExportImage();
                var operation = Static(orientationType, "ForCommand", Enum.Parse(commandType, command))!;
                using var expected = (Avalonia.Media.Imaging.Bitmap)Static(orientationType, "TransformBitmap", before, operation)!;
                Call(editor, "ApplyImageTransform", Enum.Parse(commandType, command), true); using var actual = editor.ExportImage();
                Check(actual.PixelSize == expected.PixelSize, "Rotation dimensions wrong."); var a = Pixels(actual); var b = Pixels(expected); var different = 0;
                for (var index = 0; index < a.Pixels.Length; index += 4) if (Math.Abs(a.Pixels[index] - b.Pixels[index]) + Math.Abs(a.Pixels[index + 1] - b.Pixels[index + 1]) + Math.Abs(a.Pixels[index + 2] - b.Pixels[index + 2]) > 50) different++;
                Check(different < a.Width * a.Height * .008, "Shape orientation does not track its real canvas transform: " + command + "/" + originalOrientation + " (" + different + " pixels).");
                editor.Undo(); Check(PixelHash(editor.ExportImage()) == PixelHash(before), "Transform undo lost shape.");
            }
        });

        ShapePreferenceCases(); ShapeProjectCases(); EditingPaletteCases(); CollageNavigationCases();
        Case("ClearDocument clears content and undo while retaining every remembered format", () => {
            var editor = ArrowEditor(VectorShape("Heart", true, 2)); RestoreToolPreferences(editor); Call(editor, "RestoreShapeOptions", Kind("Cloud"), true); Call(editor, "RestoreToolAppearance", ShapeTool, ToolAppearance("#123456", 8.5));
            Call(editor, "RestoreTextSize", PreferredTextSize); Call(editor, "RestoreTextFontFamily", PreferredFontFamily); var style = Style("Circle", "Diamond", "Small", "Large", "Dash"); Set(editor, "ArrowStyle", style);
            editor.NudgeSelection(2, 3); var changed = 0; var selected = 0; editor.Changed += (_, _) => changed++; editor.SelectionChanged += (_, _) => selected++;
            Call(editor, "ClearDocument"); Check(!editor.HasImage && !editor.IsCollage && Items(editor).Count == 0 && !editor.CanUndo && !editor.CanRedo, "Cleared document retained content/history.");
            Check(changed == 1 && selected == 1, "ClearDocument did not refresh document and selection exactly once."); CheckAllToolAppearances(editor); CheckToolAppearance(editor, ShapeTool, "#123456", 8.5);
            Check(Get(editor, "ShapeKind")!.ToString() == "Cloud" && (bool)Get(editor, "ShapeFilled")! && editor.TextSize == PreferredTextSize && (string)Get(editor, "TextFontFamily")! == PreferredFontFamily, "ClearDocument erased remembered defaults."); EqualStyle(Get(editor, "ArrowStyle"), style, "ClearDocument erased arrow options.");
        });
    }

    static void ShapePreferenceCases()
    {
        var root = Path.Combine(Output, "shape-settings"); Directory.CreateDirectory(root); var path = Path.Combine(root, "preferences.json");
        Case("shape preferences persist kind fill color width and last tool independently", () => {
            var lazy = SettingsLazy(); Check(!LazyCreated(lazy), "Live preferences loaded before isolated IO."); var settings = NewPreferenceSettings(Style());
            Set(settings, "ShapeKind", Kind("Heart")); Set(settings, "ShapeFilled", true); Set(settings, "LastAnnotationTool", ShapeTool); Call(settings, "SetToolAppearance", ShapeTool, ToolAppearance("#ab12cd", 11.75));
            WritePreferences(path, settings); var loaded = ReadPreferences(path); Check(Get(loaded, "ShapeKind")!.ToString() == "Heart" && (bool)Get(loaded, "ShapeFilled")! && (AnnotationTool)Get(loaded, "LastAnnotationTool")! == ShapeTool, "Shape settings failed roundtrip."); CheckToolAppearance(loaded, ShapeTool, "#AB12CD", 11.75); CheckAllToolAppearances(loaded);
            var editor = new EditorView(); Call(editor, "RestoreShapeOptions", Get(loaded, "ShapeKind"), Get(loaded, "ShapeFilled")); Call(editor, "RestoreToolAppearance", ShapeTool, Call(loaded, "GetToolAppearance", ShapeTool)); ShapeFields(Call(editor, "CreateShapeAnnotation", ShapeTool, default(Point))!, "Heart", true, 0, "Settings do not reach actual factory.");
            Check(!LazyCreated(lazy), "Shape IO loaded live Current.");
        });
        Case("invalid shape preference fields fall back independently and writes reject atomically", () => {
            var defaults = Activator.CreateInstance(SettingsType)!; var canonical = PreferenceJson(path); var original = Hash(File.ReadAllBytes(path));
            foreach (var field in new[] { "ShapeKind", "ShapeFilled" }) foreach (var bad in new JsonNode?[] { null, JsonValue.Create(999), JsonValue.Create("triangle"), new JsonArray() }) {
                var json = canonical.DeepClone().AsObject(); json[field] = bad?.DeepClone(); var badPath = Path.Combine(root, "bad.json"); File.WriteAllText(badPath, json.ToJsonString()); var loaded = ReadPreferences(badPath);
                Check(Equals(Get(loaded, field), Get(defaults, field)), "Invalid field did not default: " + field); CheckToolAppearance(loaded, ShapeTool, "#AB12CD", 11.75);
                var other = field == "ShapeKind" ? "ShapeFilled" : "ShapeKind"; Check(Equals(Get(loaded, other), other == "ShapeFilled" ? true : Kind("Heart")), "Invalid shape field erased its sibling.");
            }
            var invalid = NewPreferenceSettings(Style()); Set(invalid, "ShapeKind", Enum.ToObject(ShapeKindType, 999)); Rejected(() => WritePreferences(path, invalid)); Check(Hash(File.ReadAllBytes(path)) == original, "Invalid shape write replaced settings.");
        });
    }

    static void ShapeProjectCases()
    {
        var path = Path.Combine(Output, "shape-schema9.snipflow");
        Case("schema 9 roundtrips every shape field and editable rendered pixels", () => {
            var shapes = CommonShapes.Select((kind, n) => VectorShape(kind, n % 2 == 0, n % 8)).ToArray(); var editor = ArrowEditor(shapes); var before = PixelHash(editor.ExportImage()); Static(StoreType, "SaveProject", editor, path);
            using var archive = ZipFile.OpenRead(path); var manifest = ReadManifest(archive); Check((int)manifest["SchemaVersion"]! == 9, "Current writer did not use schema9.");
            for (var n = 0; n < shapes.Length; n++) { var shape = manifest["Annotations"]![n]!["Shape"]!.AsObject(); Check(shape.Count == 3 && (string?)shape["Kind"] == CommonShapes[n] && (bool)shape["Filled"]! == (n % 2 == 0) && (int)shape["Orientation"]! == n % 8, "Manifest shape fields differ."); }
            var loaded = new EditorView(); Static(StoreType, "RestoreProject", loaded, path); Check(Items(loaded).Count == shapes.Length && PixelHash(loaded.ExportImage()) == before && !loaded.HasEdits, "Shape project render roundtrip differs.");
            for (var n = 0; n < shapes.Length; n++) ShapeFields(Items(loaded)[n]!, CommonShapes[n], n % 2 == 0, n % 8, "Restored shape fields differ.");
        });
        Case("schemas 1 through 8 retain legacy annotations without inventing vector shapes", () => {
            var rectangle = Activator.CreateInstance(ItemType, true)!; Set(rectangle, "Tool", AnnotationTool.Rectangle); Set(rectangle, "Start", new Point(20, 25)); Set(rectangle, "End", new Point(100, 120)); Set(rectangle, "Color", Colors.Red); Set(rectangle, "Width", 4.0);
            var editor = ArrowEditor(rectangle); var seed = Path.Combine(Output, "legacy-no-shape.snipflow"); Static(StoreType, "SaveProject", editor, seed); var before = PixelHash(editor.ExportImage());
            for (var schema = 1; schema <= 8; schema++) { var legacy = MutateProject(seed, "legacy-vector-schema" + schema + ".snipflow", json => json["SchemaVersion"] = schema); var loaded = new EditorView(); Static(StoreType, "RestoreProject", loaded, legacy); Check(PixelHash(loaded.ExportImage()) == before && (AnnotationTool)Get(First(loaded), "Tool")! == AnnotationTool.Rectangle, "Legacy schema " + schema + " changed."); }
        });
        var mutations = new (string Name, Action<JsonObject> Mutation)[] {
            ("missing", json => json["Annotations"]![0]!.AsObject().Remove("Shape")),
            ("null", json => json["Annotations"]![0]!["Shape"] = null),
            ("kind-name", json => json["Annotations"]![0]!["Shape"]!["Kind"] = "triangle"),
            ("kind-number", json => json["Annotations"]![0]!["Shape"]!["Kind"] = 1),
            ("fill-type", json => json["Annotations"]![0]!["Shape"]!["Filled"] = "true"),
            ("orientation-range", json => json["Annotations"]![0]!["Shape"]!["Orientation"] = 8),
            ("orientation-negative", json => json["Annotations"]![0]!["Shape"]!["Orientation"] = -1),
            ("extra-field", json => json["Annotations"]![0]!["Shape"]!["Unknown"] = true),
            ("non-shape", json => json["Annotations"]![0]!["Tool"] = "Rectangle"),
            ("legacy-shape", json => json["SchemaVersion"] = 8)
        };
        foreach (var mutation in mutations) Case("malformed shape project rejects atomically: " + mutation.Name, () => {
            var invalid = MutateProject(path, "bad-shape-" + mutation.Name + ".snipflow", mutation.Mutation); var editor = ArrowEditor(VectorShape("Cloud", true)); editor.NudgeSelection(4, 5); var before = PixelHash(editor.ExportImage()); var revision = editor.Revision;
            Rejected(() => Static(StoreType, "RestoreProject", editor, invalid)); Check(editor.Revision == revision && editor.CanUndo && PixelHash(editor.ExportImage()) == before, "Rejected project replaced document/history."); ShapeFields(First(editor), "Cloud", true, 0, "Rejected project changed shape properties.");
        });
    }

    static void EditingPaletteCases()
    {
        Case("arrow presets suppress sync events and each explicit click preserves endpoint sizes", () => {
            var bar = Activator.CreateInstance(Product.GetType("SnipFlow.Editor.ArrowPresetBar", true)!)!; var events = new List<object>(); ObserveEvent(bar, "StyleChanged", args => events.Add(args));
            Set(bar, "Value", Style("Circle", "Diamond", "Small", "Large", "Dot")); foreach (var language in new[] { "en-US", "zh-TW" }) { I18n.SetLanguage(language); Call(bar, "RefreshLanguage"); } Check(events.Count == 0, "Programmatic/language sync emitted a preset event.");
            var expected = new Dictionary<string, (string Start, string End, string Line)> { ["single"] = ("None", "Arrow", "Solid"), ["double"] = ("Arrow", "Arrow", "Solid"), ["line"] = ("None", "None", "Solid"), ["open"] = ("None", "Open", "Solid"), ["double-open"] = ("Open", "Open", "Solid"), ["circle-end"] = ("None", "Circle", "Solid"), ["diamond-end"] = ("None", "Diamond", "Solid"), ["dashed-single"] = ("None", "Arrow", "Dash"), ["dashed-double"] = ("Arrow", "Arrow", "Dash"), ["dotted-single"] = ("None", "Arrow", "Dot") };
            var buttons = PaletteButtons(bar); Check(buttons.Count == expected.Count, "Missing common arrow preset."); var count = 0;
            foreach (var preset in expected) { var button = buttons.Single(button => Equals(button.Tag, preset.Key)); Click(button); Check(events.Count == ++count, "Preset click did not emit once."); EqualStyle(Get(bar, "Value"), Style(preset.Value.Start, preset.Value.End, "Small", "Large", preset.Value.Line), "Preset changed size or wrong options: " + preset.Key); EqualStyle(Get(events[^1], "Value"), Get(bar, "Value")!, "Preset event value differs."); Click(button); Check(events.Count == ++count, "Repeated explicit preset intent was suppressed."); }
        });
        Case("arrow preset application preserves per-item sizes with mixed selection and one undo", () => {
            var a = StyledArrow(Style("Circle", "Diamond", "Small", "Large", "Dot")); var b = StyledArrow(Style("None", "Open", "Large", "Small", "Solid"), new Point(50, 180), new Point(250, 180)); var shape = VectorShape("Heart", true); var editor = ArrowEditor(a, b, shape); var before = PixelHash(editor.ExportImage());
            Call(editor, "ApplyArrowPreset", Style("Arrow", "Arrow", "Medium", "Medium", "Dash")); EqualStyle(Get(Items(editor)[0]!, "ArrowStyle"), Style("Arrow", "Arrow", "Small", "Large", "Dash"), "Preset replaced first sizes."); EqualStyle(Get(Items(editor)[1]!, "ArrowStyle"), Style("Arrow", "Arrow", "Large", "Small", "Dash"), "Preset replaced second sizes."); ShapeFields(Items(editor)[2]!, "Heart", true, 0, "Preset edited non-arrow."); editor.Undo(); Check(PixelHash(editor.ExportImage()) == before && !editor.CanUndo, "Preset is not one undoable edit.");
        });
        Case("shape palette suppresses sync events and independently selects kind and fill", () => {
            var picker = Activator.CreateInstance(Product.GetType("SnipFlow.Editor.ShapePicker", true)!)!; var shapes = 0; var fills = 0; ObserveEvent(picker, "ShapeChanged", _ => shapes++); ObserveEvent(picker, "FillChanged", _ => fills++);
            Set(picker, "Value", Kind("Heart")); Set(picker, "Filled", true); foreach (var language in new[] { "en-US", "zh-TW" }) { I18n.SetLanguage(language); Call(picker, "RefreshLanguage"); } Check(shapes == 0 && fills == 0, "Palette sync emitted a user event.");
            var buttons = PaletteButtons(picker); foreach (var kind in CommonShapes) { var before = shapes; Click(buttons.Single(button => Equals(button.Tag, Kind(kind)))); Check(shapes == before + 1 && Get(picker, "Value")!.Equals(Kind(kind)) && (bool)Get(picker, "Filled")!, "Shape click changed fill or emitted incorrectly."); }
            var toggle = ((Control)picker).GetLogicalDescendants().OfType<CheckBox>().Single(); toggle.IsChecked = false; Check(fills == 1 && shapes == CommonShapes.Length && !(bool)Get(picker, "Filled")!, "Fill edit changed selected shape or notified incorrectly.");
        });
    }

    static void CollageNavigationCases()
    {
        var navigationType = Product.GetType("SnipFlow.Editor.CollageNavigation", true)!;
        Case("shared history routing permits collage double-click while ordinary selection stays safe", () => {
            foreach (var recording in new[] { false, true }) {
                Check((bool)Static(navigationType, "ShouldOpenHistory", true, 2, recording)!, "Double-click cannot leave collage for history.");
                Check((bool)Static(navigationType, "ShouldOpenHistory", true, 1, recording)! == recording, "Single history selection opened another image in collage.");
                Check((bool)Static(navigationType, "ShouldOpenHistory", false, 1, recording)!, "Ordinary history selection stopped opening.");
                foreach (var clicks in new[] { 0, 3 }) Check(!(bool)Static(navigationType, "ShouldOpenHistory", true, clicks, recording)!, "Unexpected click count opens a document.");
            }
        });
        Case("leaving collage restores the original image and editable annotations", () => {
            foreach (var blank in new[] { false, true }) {
                var editor = ArrowEditor(VectorShape("Callout", true, 7), StyledArrow(Style("Circle", "Open", "Small", "Large", "Dash"))); editor.NudgeSelection(3, 4); var original = PixelHash(editor.ExportImage()); var dirty = editor.HasEdits;
                var navigation = Activator.CreateInstance(navigationType, true)!; Call(navigation, "Enter", editor, blank);
                Check(editor.IsCollage && (bool)Get(navigation, "HasReturnDocument")!, "Entering collage lost original document snapshot.");
                Call(navigation, "Enter", editor, true); Check((bool)Get(navigation, "HasReturnDocument")!, "Repeated collage entry discarded return document.");
                Check((bool)Call(navigation, "Exit", editor)! && !editor.IsCollage && editor.HasEdits == dirty, "Exit did not restore original document state.");
                Check(Items(editor).Count == 2 && PixelHash(editor.ExportImage()) == original, "Exit lost original image/annotation pixels."); ShapeFields(First(editor), "Callout", true, 7, "Exit flattened original vector annotations.");
                Check(!(bool)Get(navigation, "HasReturnDocument")! && !(bool)Call(navigation, "Exit", editor)!, "Exit retained a stale return document.");
            }
        });
        Case("collage exit without a source and forgotten navigation return to an empty editor", () => {
            foreach (var forget in new[] { false, true }) {
                var editor = forget ? ArrowEditor(VectorShape("Star", true)) : new EditorView(); var navigation = Activator.CreateInstance(navigationType, true)!;
                Call(editor, "RestoreShapeOptions", Kind("Heart"), true); Call(navigation, "Enter", editor, true); if (forget) Call(navigation, "Forget");
                Check(!(bool)Call(navigation, "Exit", editor)! && !editor.HasImage && !editor.IsCollage && Items(editor).Count == 0, "Source-free collage left an inaccessible document.");
                Check(Get(editor, "ShapeKind")!.ToString() == "Heart" && (bool)Get(editor, "ShapeFilled")!, "Navigation erased format defaults.");
            }
        });
        Case("collage transition suppresses stale same-revision save callbacks before document identity changes", () => {
            var editor = ArrowEditor(VectorShape("Callout", true)); editor.NudgeSelection(3, 4);
            var navigation = Activator.CreateInstance(navigationType, true)!; var transitionCallbacks = 0; var staleMarks = 0; var oldSavedRevision = -1;
            editor.Changed += (_, _) => {
                if ((bool)Get(navigation, "IsTransitioning")!) { transitionCallbacks++; return; }
                // This is the identity-sensitive part of MainWindow.QueueAutoSave: a matching
                // revision means saved only when callbacks belong to the same document.
                if (staleMarks == 0 && !editor.IsCollage && editor.Revision == oldSavedRevision) { staleMarks++; editor.MarkSaved(); }
            };
            editor.SelectionChanged += (_, _) => { if ((bool)Get(navigation, "IsTransitioning")!) transitionCallbacks++; };
            Call(navigation, "Enter", editor, false); Check(!(bool)Get(navigation, "IsTransitioning")!, "Enter left transition guard active.");
            // RestoreSession starts a document at revision 1, independently of its previous identity.
            oldSavedRevision = 1; editor.MarkSaved(); var callbacksBeforeExit = transitionCallbacks;
            Call(navigation, "Exit", editor);
            Check(transitionCallbacks > callbacksBeforeExit && !((bool)Get(navigation, "IsTransitioning")!), "Exit did not guard synchronous editor callbacks or did not release guard.");
            Check(staleMarks == 0 && editor.HasEdits && editor.Revision == oldSavedRevision, "A stale collage saved-revision marked the returned dirty original as saved.");
        });
    }

    static int RunEditingToolsOnly()
    {
        var lazy = SettingsLazy(); EditingToolCases();
        Case("editing tools never load live settings or create a native application lifetime", () => { Check(!LazyCreated(lazy), "Editing tools loaded live settings."); Check(Application.Current is CropTestApplication && Application.Current.ApplicationLifetime is null, "Editing tools created native lifetime."); });
        var report = new { guard = "SNIPFLOW-EDITING-TOOLS-012", timestamp = DateTimeOffset.Now, passed, failed, productAssembly = Product.Location, productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)), safety = "Offscreen product objects, explicit fixture IO and synthetic Button.Click events only. No Window, MainWindow, Popup opening, desktop input, clipboard, capture, registry, installation, or deployment.", tests = Results };
        File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"Editing tools: {passed} passed, {failed} failed. {Path.Combine(Output, "results.json")}"); return failed == 0 ? 0 : 1;
    }
}

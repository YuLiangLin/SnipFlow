using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static Type SettingsType => Product.GetType("SnipFlow.Services.UserSettings", true)!;
    static Type SettingsStoreType => Product.GetType("SnipFlow.Services.SettingsStore", true)!;
    static object NewPreferenceSettings(object style)
    {
        var settings = Activator.CreateInstance(SettingsType)!;
        Set(settings, "ArrowStyle", style);
        Set(settings, "ArrowColor", "#a1b2c3");
        Set(settings, "ArrowStrokeWidth", 7.25);
        Set(settings, "Language", "en-US");
        Set(settings, "Theme", "light");
        Set(settings, "HistoryLimit", 137);
        Set(settings, "CopyAfterCapture", false);
        Set(settings, "StartWithWindows", true);
        Set(settings, "CaptureSaveDirectory", Path.Combine(Output, "preference-captures"));
        Set(settings, "RecentColors", new List<string> { "#123456", "#ABCDEF" });
        return settings;
    }

    static void CheckOtherPreferences(object settings)
    {
        Check((string)Get(settings, "Language")! == "en-US", "Style loading lost language.");
        Check((string)Get(settings, "Theme")! == "light", "Style loading lost theme.");
        Check((int)Get(settings, "HistoryLimit")! == 137, "Style loading lost history limit.");
        Check(!(bool)Get(settings, "CopyAfterCapture")!, "Style loading lost copy preference.");
        Check((bool)Get(settings, "StartWithWindows")!, "Isolated loading refreshed startup registration.");
        Check(((IEnumerable<string>)Get(settings, "RecentColors")!).SequenceEqual(new[] { "#123456", "#ABCDEF" }), "Style loading lost recent colors.");
    }

    static object ReadPreferences(string path) => Static(SettingsStoreType, "ReadSettingsFile", path)!;
    static void WritePreferences(string path, object settings) => Static(SettingsStoreType, "WriteSettingsFile", path, settings);
    static JsonObject PreferenceJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    static object SettingsLazy() => SettingsStoreType.GetFields(Any).Single(field =>
        field.IsStatic && field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>)
        && field.FieldType.GetGenericArguments()[0] == SettingsType).GetValue(null)!;
    static bool LazyCreated(object lazy) => (bool)Get(lazy, "IsValueCreated")!;
    static Dictionary<string, string> StyleNames(object style) => StyleFields.ToDictionary(field => field, field => Get(style, field)!.ToString()!);

    static void ArrowPreferenceCases()
    {
        var root = Path.Combine(Output, "arrow-preferences");
        Directory.CreateDirectory(root);
        var style = Style("Diamond", "Circle", "Large", "Small", "Dot");
        var roundtrip = Path.Combine(root, "roundtrip.json");

        Case("arrow preferences roundtrip all five options and retain other preferences", () => {
            WritePreferences(roundtrip, NewPreferenceSettings(style));
            var json = PreferenceJson(roundtrip);
            var encoded = json["ArrowStyle"]!.AsObject();
            Check(encoded.Count == 5, "Preference style should contain exactly five options.");
            foreach (var field in StyleFields)
                Check(encoded[field]!.GetValue<string>() == Get(style, field)!.ToString(), "Preference enum is not a fixed name: " + field);
            var loaded = ReadPreferences(roundtrip);
            EqualStyle(Get(loaded, "ArrowStyle"), style, "Five-option settings roundtrip differs.");
            CheckOtherPreferences(loaded);
            Check((string)Get(loaded, "CaptureSaveDirectory")! == (string)Get(NewPreferenceSettings(style), "CaptureSaveDirectory")!, "Style loading lost capture directory.");

            // Existing settings may contain defined numeric enum values; the writer normalizes them to names.
            foreach (var field in StyleFields) encoded[field] = Convert.ToInt32(Get(style, field));
            var numeric = Path.Combine(root, "defined-numeric.json");
            File.WriteAllText(numeric, json.ToJsonString());
            var numericSettings = ReadPreferences(numeric);
            EqualStyle(Get(numericSettings, "ArrowStyle"), style, "Defined numeric preference enums did not load.");
            WritePreferences(numeric, numericSettings);
            foreach (var field in StyleFields)
                Check(PreferenceJson(numeric)["ArrowStyle"]![field]!.GetValue<string>() == Get(style, field)!.ToString(), "Numeric preference was not normalized on save.");
        });

        Case("legacy settings without arrow options use the compatible default", () => {
            var json = PreferenceJson(roundtrip); json.Remove("ArrowStyle");
            var path = Path.Combine(root, "legacy-missing-style.json"); File.WriteAllText(path, json.ToJsonString());
            var loaded = ReadPreferences(path);
            EqualStyle(Get(loaded, "ArrowStyle"), Style(), "Legacy settings did not receive default single-arrow options.");
            CheckOtherPreferences(loaded);
        });

        Case("invalid arrow preferences fall back without discarding unrelated settings", () => {
            var mutations = new (string Name, Action<JsonObject> Change)[] {
                ("null", json => json["ArrowStyle"] = null),
                ("scalar", json => json["ArrowStyle"] = "Arrow"),
                ("array", json => json["ArrowStyle"] = new JsonArray()),
                ("kind", json => json["ArrowStyle"]!["StartHead"] = "Chevron"),
                ("numeric", json => json["ArrowStyle"]!["EndHead"] = 999),
                ("size", json => json["ArrowStyle"]!["StartSize"] = "Giant"),
                ("null-size", json => json["ArrowStyle"]!["EndSize"] = null),
                ("line", json => json["ArrowStyle"]!["LineStyle"] = "Wavy")
            };
            foreach (var mutation in mutations) {
                var json = PreferenceJson(roundtrip); mutation.Change(json);
                var path = Path.Combine(root, "invalid-" + mutation.Name + ".json"); File.WriteAllText(path, json.ToJsonString());
                var loaded = ReadPreferences(path);
                EqualStyle(Get(loaded, "ArrowStyle"), Style(), "Invalid style did not fall back: " + mutation.Name);
                CheckOtherPreferences(loaded);
            }
            // Preserve the old JSON reader's last-value behavior for duplicate root keys.
            var duplicate = Path.Combine(root, "duplicate-style.json");
            var original = File.ReadAllText(roundtrip);
            File.WriteAllText(duplicate, "{\"ArrowStyle\":null," + original.TrimStart()[1..]);
            var duplicateSettings = ReadPreferences(duplicate);
            EqualStyle(Get(duplicateSettings, "ArrowStyle"), style, "Duplicate root style did not use the final value.");
            CheckOtherPreferences(duplicateSettings);
        });

        Case("isolated settings IO leaves lazy Current and other files untouched", () => {
            var lazy = SettingsLazy(); var initialized = LazyCreated(lazy);
            var folder = Path.Combine(root, "isolated"); Directory.CreateDirectory(folder);
            var sentinel = Path.Combine(folder, "unrelated.txt"); File.WriteAllText(sentinel, "keep-existing-preferences");
            var sentinelHash = Hash(File.ReadAllBytes(sentinel));
            var path = Path.Combine(folder, "settings.json");
            var notifications = 0; EventHandler observer = (_, _) => notifications++;
            var changed = SettingsStoreType.GetEvent("Changed")!; changed.AddEventHandler(null, observer);
            try {
                WritePreferences(path, NewPreferenceSettings(style));
                EqualStyle(Get(ReadPreferences(path), "ArrowStyle"), style, "Isolated file did not roundtrip.");
                var missing = Path.Combine(folder, "missing.json");
                var missingRejected = false;
                try { ReadPreferences(missing); }
                catch (FileNotFoundException) { missingRejected = true; }
                Check(missingRejected, "The file codec silently accepted a missing path.");
                Check(!File.Exists(missing), "Read created an unrequested settings file.");
                Check(ReferenceEquals(SettingsLazy(), lazy) && LazyCreated(lazy) == initialized, "Path IO initialized or replaced global Current.");
                Check(notifications == 0, "Isolated file IO notified global settings listeners.");
                Check(Hash(File.ReadAllBytes(sentinel)) == sentinelHash, "Isolated IO changed another file.");
                Check(Directory.GetFiles(folder).Select(Path.GetFileName).Order().SequenceEqual(new[] { "settings.json", "unrelated.txt" }), "Isolated IO left an unexpected file or temp artifact.");
            }
            finally { changed.RemoveEventHandler(null, observer); }
        });

        Case("fresh processes reload saved arrow options into the real drawing factory", () => {
            var initialHash = Hash(File.ReadAllBytes(roundtrip));
            for (var n = 1; n <= 2; n++) {
                var output = Path.Combine(root, "fresh-process-" + n);
                var report = RunArrowPreferenceProbe(roundtrip, output);
                Check(report["mode"]!.GetValue<string>() == "arrow-preference-probe" && !report["fullRegressionRun"]!.GetValue<bool>(), "Probe was misreported as a complete regression run.");
                Check(report["success"]!.GetValue<bool>(), "Fresh process failed: " + report["error"]);
                Check(report["processId"]!.GetValue<int>() != Environment.ProcessId, "Restart probe did not run in a new process.");
                Check(!report["lazyCurrentBefore"]!.GetValue<bool>() && !report["lazyCurrentAfter"]!.GetValue<bool>(), "Fresh process read live Current.");
                Check(report["productAssemblySha256"]!.GetValue<string>() == Hash(File.ReadAllBytes(Product.Location)), "Probe loaded a different product assembly.");
                foreach (var field in StyleFields) {
                    var expected = Get(style, field)!.ToString();
                    foreach (var stage in new[] { "settingsStyle", "editorStyle", "factoryStyle" })
                        Check(report[stage]![field]!.GetValue<string>() == expected, "Restart lost " + field + " at " + stage);
                }
                foreach (var stage in new[] { "settingsAppearance", "editorAppearance", "factoryAppearance" }) {
                    Check(report[stage]!["color"]!.GetValue<string>() == "#A1B2C3", "Restart lost arrow color at " + stage);
                    Check(report[stage]!["strokeWidth"]!.GetValue<double>() == 7.25, "Restart lost arrow width at " + stage);
                }
            }
            Check(Hash(File.ReadAllBytes(roundtrip)) == initialHash, "Restart probes wrote the supplied settings file.");
        });

        Case("selection restore undo and panel synchronization do not emit user preference edits", () => {
            var settingsHash = Hash(File.ReadAllBytes(roundtrip));
            var editor = new EditorView(); Set(editor, "ArrowStyle", style);
            var panel = Activator.CreateInstance(Product.GetType("SnipFlow.Editor.ArrowStylePanel", true)!)!;
            var edits = 0; var observer = ObserveStylePanel(panel, _ => edits++);
            editor.SelectionChanged += (_, _) => Set(panel, "Value", Get(editor, "SelectedArrowStyle") ?? Get(editor, "ArrowStyle"));
            Set(panel, "Value", style);
            var array = Array.CreateInstance(ItemType, 1); array.SetValue(StyledArrow(Style("Open", "Triangle", "Small", "Large", "Dash")), 0);
            var board = ImageTools.Solid(320, 240, Colors.White);
            Call(editor, "RestoreSession", board, array, false, true);
            editor.SelectAll();
            Set(editor, "StrokeWidth", 9.0); Check(UndoDepth(editor) == 1, "Fixture did not create an undoable document edit.");
            editor.Undo(); editor.Redo();
            Call(editor, "RestoreSession", board, array, false, true);
            Call(panel, "RefreshLanguage");
            Check(edits == 0, "Document synchronization emitted a user style edit.");
            EqualStyle(Get(editor, "ArrowStyle"), style, "Document operations overwrote the remembered drawing default.");
            EqualStyle(Get(Call(editor, "CreateShapeAnnotation", AnnotationTool.Arrow, new Point(30, 30))!, "ArrowStyle"), style, "New drawing lost the remembered default after restore/undo.");
            Check(Hash(File.ReadAllBytes(roundtrip)) == settingsHash, "Document synchronization rewrote the saved preference.");
            GC.KeepAlive(observer);
        });
    }

    static JsonObject RunArrowPreferenceProbe(string settingsPath, string output)
    {
        Directory.CreateDirectory(output);
        // Reuse the actual parent host: global dotnet for packaged Windows checks,
        // or the self-contained harness apphost on Linux without a global SDK/runtime.
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The current test host path is unavailable.");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--arrow-preference-probe"); start.ArgumentList.Add(settingsPath);
        start.ArgumentList.Add("--output"); start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated settings probe.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        Check(process.WaitForExit(30_000), "Isolated settings probe exceeded its bounded wait.");
        Task.WaitAll(stdout, stderr);
        File.WriteAllText(Path.Combine(output, "process.log"), stdout.Result + stderr.Result);
        File.WriteAllText(Path.Combine(output, "process-metadata.json"), JsonSerializer.Serialize(new { processId = process.Id, exitCode = process.ExitCode, executable = host, harness = Assembly.GetExecutingAssembly().Location }, new JsonSerializerOptions { WriteIndented = true }));
        Check(process.ExitCode == 0, "Isolated settings probe exit " + process.ExitCode + ": " + stdout.Result + stderr.Result);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(output, "arrow-preference-probe.json")))!.AsObject();
    }

    static int WriteArrowPreferenceProbe(string settingsPath)
    {
        object report;
        var success = false;
        try {
            settingsPath = Path.GetFullPath(settingsPath);
            var lazy = SettingsLazy(); var before = LazyCreated(lazy);
            var fileHash = Hash(File.ReadAllBytes(settingsPath));
            var settings = ReadPreferences(settingsPath); var style = Get(settings, "ArrowStyle")!;
            var color = Color.Parse((string)Get(settings, "ArrowColor")!); var width = (double)Get(settings, "ArrowStrokeWidth")!;
            var editor = new EditorView(); Set(editor, "ArrowStyle", style); Call(editor, "RestoreArrowAppearance", color, width);
            var arrow = Call(editor, "CreateShapeAnnotation", AnnotationTool.Arrow, new Point(30, 30))!;
            EqualStyle(Get(editor, "ArrowStyle"), style, "Fresh editor default differs.");
            EqualStyle(Get(arrow, "ArrowStyle"), style, "Fresh drawing factory differs.");
            CheckArrowAppearance(editor, color, width);
            CheckItemAppearance(arrow, color, width);
            var after = LazyCreated(lazy);
            Check(!before && !after, "Isolated probe initialized global Current.");
            Check(Hash(File.ReadAllBytes(settingsPath)) == fileHash, "Probe modified the supplied settings file.");
            success = true;
            report = new {
                mode = "arrow-preference-probe", fullRegressionRun = false, success, processId = Environment.ProcessId,
                productAssembly = Product.Location, productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)),
                settingsPath, settingsSha256 = fileHash, lazyCurrentBefore = before, lazyCurrentAfter = after,
                settingsStyle = StyleNames(style), editorStyle = StyleNames(Get(editor, "ArrowStyle")!), factoryStyle = StyleNames(Get(arrow, "ArrowStyle")!),
                settingsAppearance = new { color = ColorHex(color), strokeWidth = width },
                editorAppearance = new { color = ColorHex((Color)Get(editor, "ArrowColor")!), strokeWidth = (double)Get(editor, "ArrowStrokeWidth")! },
                factoryAppearance = new { color = ColorHex((Color)Get(arrow, "Color")!), strokeWidth = (double)Get(arrow, "Width")! },
                safety = "Fresh console process; supplied settings path only. No Current, registry, Window, MainWindow, OS input, or live settings write."
            };
        }
        catch (Exception ex) { report = new { mode = "arrow-preference-probe", fullRegressionRun = false, success, error = Unwrap(ex).ToString() }; }
        var path = Path.Combine(Output, "arrow-preference-probe.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine("Arrow preference probe: " + (success ? "PASS " : "FAIL ") + path);
        return success ? 0 : 1;
    }
}

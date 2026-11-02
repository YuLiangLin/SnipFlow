using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnipFlow.Editor;
using SnipFlow.Imaging;
using SnipFlow.Services;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static Type? DocumentFilesType => Product.GetType("SnipFlow.Services.DocumentFileStore");

    static void SaveHistoryDocument(EditorView editor, string pngPath, string? historyPath)
    {
        var snapshot = Call(editor, "CaptureSession")!;
        var rendered = editor.ExportImage();
        SaveHistorySnapshot(snapshot, rendered, pngPath, historyPath);
    }

    static void SaveHistorySnapshot(object snapshot, Bitmap rendered, string pngPath, string? historyPath)
    {
        if (DocumentFilesType is { } store) Static(store, "Save", snapshot, rendered, pngPath, historyPath, Path.GetDirectoryName(historyPath ?? pngPath)!);
        else
        {
            // Literal 0.8.9 single-screenshot autosave call site: no editable project,
            // and the raw capture in History remains unchanged.
            CaptureFileStore.Save(rendered, pngPath);
        }
    }

    static void OpenHistoryDocument(EditorView editor, string imagePath)
    {
        if (DocumentFilesType is { } store) Static(store, "Open", editor, imagePath);
        else editor.LoadImage(HistoryStore.Read(imagePath)); // Literal 0.8.9 OpenImage/LoadDocument path.
    }

    static TextBox BeginHistoryText(EditorView editor, string text)
    {
        Call(editor, "BeginTextEdit", (object?)null, new Rect(20, 30, 270, 100), (object?)null);
        var box = (TextBox)typeof(EditorView).GetField("_textEditor", Any)!.GetValue(editor)!;
        box.Text = text;
        return box;
    }

    static string HistoryFixturePath(string directory) => Path.Combine(directory, "20261102_010203_004_" + Guid.NewGuid().ToString("N") + ".png");

    static void EditHistoryText(EditorView editor, string text)
    {
        Check(Items(editor).Count == 1 && (AnnotationTool)Get(First(editor), "Tool")! == AnnotationTool.Text, "Expected one editable text annotation.");
        Call(editor, "BeginTextEdit", First(editor), (object?)null, (object?)null);
        ((TextBox)typeof(EditorView).GetField("_textEditor", Any)!.GetValue(editor)!).Text = text;
        editor.CommitTextEdit();
    }

    static void CheckHistoryText(EditorView editor, string text, string message)
    {
        Check(Items(editor).Count == 1 && (AnnotationTool)Get(First(editor), "Tool")! == AnnotationTool.Text && (string)Get(First(editor), "Text")! == text, message);
    }

    static void ExpectHistoryFailure(Action action)
    {
        var rejected = false;
        try { action(); }
        catch (Exception e) when (Unwrap(e) is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException) { rejected = true; }
        Check(rejected, "Invalid or unpublishable document unexpectedly succeeded.");
    }

    static void HistoryAnnotationCases()
    {
        var root = Path.Combine(Output, "history-annotations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var history = Path.Combine(root, "History"); Directory.CreateDirectory(history);
        Case("SNIPFLOW-HISTORY-ANNOTATION-010 annotated A survives capture B and reopening A as editable text", () => {
            var a = HistoryFixturePath(history); var b = HistoryFixturePath(history);
            var outputA = Path.Combine(root, "saved-A.png"); var outputB = Path.Combine(root, "saved-B.png");
            // EditorView and captured sessions share these source bitmaps. Unattached
            // editors retain render caches beyond this case, so bitmap ownership must
            // follow the editor's process lifetime rather than this method's scope.
            var sourceA = ImageTools.Solid(320, 240, Colors.White);
            var sourceB = ImageTools.Solid(320, 240, Colors.LightBlue);
            ImageTools.SavePng(sourceA, a); ImageTools.SavePng(sourceB, b);
            var capturedAt = new DateTime(2026, 11, 2, 1, 2, 3, DateTimeKind.Utc); File.SetLastWriteTimeUtc(a, capturedAt);
            var editor = new EditorView(); editor.LoadImage(sourceA);
            BeginHistoryText(editor, "Annotation for screenshot A"); editor.CommitTextEdit();
            var expected = PixelHash(editor.ExportImage());
            Check(expected != PixelHash(sourceA), "Fixture text did not change screenshot A pixels.");
            SaveHistoryDocument(editor, outputA, a);
            editor.LoadImage(sourceB); BeginHistoryText(editor, "Annotation for screenshot B"); editor.CommitTextEdit();
            SaveHistoryDocument(editor, outputB, b);
            OpenHistoryDocument(editor, a);
            Check(PixelHash(editor.ExportImage()) == expected,
                "Reopening screenshot A after annotating B lost A's visible annotation (raw capture was loaded instead of its saved document).");
            CheckHistoryText(editor, "Annotation for screenshot A", "A's annotation is flattened, duplicated, or no longer editable.");
            Check(PixelHash(HistoryStore.Read(a)) == expected && File.GetLastWriteTimeUtc(a) == capturedAt, "History thumbnail source did not update or capture ordering changed after editing.");
            var fresh = new EditorView(); OpenHistoryDocument(fresh, b);
            CheckHistoryText(fresh, "Annotation for screenshot B", "Reopening B confused the two documents.");
            var probe = RunHistoryOpenProbe(a, Path.Combine(root, "restart-probe"));
            Check((int)probe["processId"]! != Environment.ProcessId && (bool)probe["lazyCurrentBefore"]! == false && (bool)probe["lazyCurrentAfter"]! == false, "Restart probe reused current process or loaded live settings.");
            Check((string)probe["pixelHash"]! == expected && probe["texts"]!.AsArray().Count == 1 && (string)probe["texts"]![0]! == "Annotation for screenshot A", "Fresh process lost annotation text or pixels.");
        });
        Case("single screenshot autosave without history writes an editable project and plain legacy PNG still opens", () => {
            var path = Path.Combine(root, "no-history.png");
            var source = ImageTools.Solid(320, 240, Colors.White);
            var editor = new EditorView(); editor.LoadImage(source); BeginHistoryText(editor, "No history enabled"); editor.CommitTextEdit();
            var pixels = PixelHash(editor.ExportImage()); SaveHistoryDocument(editor, path, null);
            Check(File.Exists(Path.ChangeExtension(path, ".snipflow")), "Single screenshot autosave omitted its editable project.");
            var restored = new EditorView(); OpenHistoryDocument(restored, path);
            CheckHistoryText(restored, "No history enabled", "No-history autosave flattened the text.");
            Check(PixelHash(restored.ExportImage()) == pixels && !restored.IsCollage, "No-history reopen changed screenshot pixels or document kind.");
            var legacy = Path.Combine(root, "legacy-only.png"); ImageTools.SavePng(source, legacy); OpenHistoryDocument(restored, legacy);
            Check(Items(restored).Count == 0 && PixelHash(restored.ExportImage()) == PixelHash(source), "Legacy PNG without sidecar did not open normally.");
        });
        Case("saving pending text commits the real text draft and reopening permits edit undo and resave", () => {
            var path = Path.Combine(root, "pending-text.png");
            var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.White));
            BeginHistoryText(editor, "Pending draft survives");
            Check(Items(editor).Count == 0 && editor.IsTextEditing, "Fixture text was already committed.");
            SaveHistoryDocument(editor, path, null);
            Check(!editor.IsTextEditing, "Snapshot save did not commit pending text.");
            var reopened = new EditorView(); OpenHistoryDocument(reopened, path); CheckHistoryText(reopened, "Pending draft survives", "Pending text was omitted from autosave.");
            var original = PixelHash(reopened.ExportImage()); EditHistoryText(reopened, "Edited after reopen");
            Check(reopened.CanUndo, "Reopened annotation cannot be edited with undo.");
            SaveHistoryDocument(reopened, path, null);
            var latest = new EditorView(); OpenHistoryDocument(latest, path); CheckHistoryText(latest, "Edited after reopen", "Second save did not replace latest text.");
            reopened.Undo(); SaveHistoryDocument(reopened, path, null); OpenHistoryDocument(latest, path);
            CheckHistoryText(latest, "Pending draft survives", "Undo and autosave left stale later text.");
            Check(PixelHash(latest.ExportImage()) == original, "Undo-save-reopen changed original annotation pixels.");
        });
        Case("autosave writes the captured immutable document even if the editor changes before disk IO", () => {
            var path = Path.Combine(root, "immutable-snapshot.png");
            var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.White));
            BeginHistoryText(editor, "Snapshot text"); editor.CommitTextEdit();
            var snapshot = Call(editor, "CaptureSession")!; var rendered = editor.ExportImage(); var expected = PixelHash(rendered);
            EditHistoryText(editor, "Later editor text"); SaveHistorySnapshot(snapshot, rendered, path, null);
            var reopened = new EditorView(); OpenHistoryDocument(reopened, path); CheckHistoryText(reopened, "Snapshot text", "Save consulted mutable editor state instead of the snapshot.");
            Check(PixelHash(reopened.ExportImage()) == expected, "Snapshot project and rendered PNG describe different revisions.");
            CheckHistoryText(editor, "Later editor text", "Saving a previous snapshot changed the current editor.");
        });
        Case("autosave preserves image crop drafts and saves an undo back to the complete source", () => {
            var path = Path.Combine(root, "crop-undo.png"); var source = Quadrants();
            var editor = Editor(source, null); DraftBottomRightQuarter(editor); SaveHistoryDocument(editor, path, null);
            Check(!Cropping(editor), "Autosave left pending crop active."); var cropped = PixelHash(editor.ExportImage());
            var reopened = new EditorView(); OpenHistoryDocument(reopened, path); EqualRect(Crop(First(reopened)), new Rect(.5, .5, .5, .5), "Autosave lost pending crop viewport.");
            Check(PixelHash(reopened.ExportImage()) == cropped, "Autosave changed cropped output pixels.");
            editor.Undo(); SaveHistoryDocument(editor, path, null); OpenHistoryDocument(reopened, path);
            Check(Crop(First(reopened)) is null && PixelHash((Bitmap)Get(First(reopened), "Image")!) == PixelHash(source), "Autosaving undo lost full embedded source pixels.");
        });
        Case("invalid editable sidecar rejects atomically and retains the current annotated document", () => {
            var path = Path.Combine(root, "corrupt.png"); ImageTools.SavePng(ImageTools.Solid(320, 240, Colors.White), path);
            File.WriteAllText(Path.ChangeExtension(path, ".snipflow"), "not a SnipFlow project");
            var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.LightBlue)); BeginHistoryText(editor, "Keep current text"); editor.CommitTextEdit();
            var expected = PixelHash(editor.ExportImage()); var revision = editor.Revision;
            ExpectHistoryFailure(() => OpenHistoryDocument(editor, path));
            CheckHistoryText(editor, "Keep current text", "Corrupt project discarded current annotation.");
            Check(editor.Revision == revision && PixelHash(editor.ExportImage()) == expected, "Corrupt sidecar changed current document state.");
        });
        Case("PNG publication failure retains a readable complete editable project and current editor", () => {
            var path = Path.Combine(root, "blocked-png.png");
            var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.White)); BeginHistoryText(editor, "Before IO failure"); editor.CommitTextEdit(); SaveHistoryDocument(editor, path, null);
            File.Delete(path); Directory.CreateDirectory(path); // Fixture-owned path only; force a portable file-publication error.
            EditHistoryText(editor, "Latest recoverable text"); var current = PixelHash(editor.ExportImage());
            ExpectHistoryFailure(() => SaveHistoryDocument(editor, path, null));
            Check(PixelHash(editor.ExportImage()) == current, "Failed file IO changed editor annotation.");
            var reopened = new EditorView(); Static(StoreType, "RestoreProject", reopened, Path.ChangeExtension(path, ".snipflow"));
            var text = (string)Get(First(reopened), "Text")!;
            Check(text is "Before IO failure" or "Latest recoverable text", "Failed PNG write corrupted or discarded the last complete editable project.");
        });
        Case("history delete and clear remove owned editable sidecars while preserving unrelated files", () => {
            var directory = Path.Combine(root, "cleanup-history"); Directory.CreateDirectory(directory);
            var first = HistoryFixturePath(directory); var second = HistoryFixturePath(directory);
            var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.White)); BeginHistoryText(editor, "History cleanup"); editor.CommitTextEdit();
            ImageTools.SavePng(editor.ExportImage(), first); ImageTools.SavePng(editor.ExportImage(), second);
            SaveHistoryDocument(editor, Path.Combine(root, "cleanup-output-1.png"), first); SaveHistoryDocument(editor, Path.Combine(root, "cleanup-output-2.png"), second);
            var unrelatedPng = Path.Combine(directory, "user-picture.png"); var unrelatedProject = Path.Combine(directory, "user-project.snipflow");
            File.WriteAllText(unrelatedPng, "unrelated PNG"); File.WriteAllText(unrelatedProject, "unrelated project");
            var item = new HistoryItem(first, "fixture", "fixture", null);
            Check((bool)Static(typeof(HistoryStore), "Delete", item, directory)!, "History delete did not remove fixture.");
            Check(!File.Exists(first) && !File.Exists(Path.ChangeExtension(first, ".snipflow")), "Deleting history left editable sidecar behind.");
            var result = (HistoryClearResult)Static(typeof(HistoryStore), "Clear", directory)!;
            Check(result.DeletedCount == 1 && result.FailedCount == 0 && !File.Exists(second) && !File.Exists(Path.ChangeExtension(second, ".snipflow")), "Clearing history failed to remove its owned document pair.");
            Check(File.ReadAllText(unrelatedPng) == "unrelated PNG" && File.ReadAllText(unrelatedProject) == "unrelated project", "History cleanup changed unrelated files.");
        });
        Case("delayed autosave cannot recreate deleted history cards or write outside its explicit history scope", () => {
            var directory = Path.Combine(root, "removed-history"); Directory.CreateDirectory(directory);
            var removed = HistoryFixturePath(directory); var output = Path.Combine(root, "removed-output.png");
            var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.White)); BeginHistoryText(editor, "Keep saved document only"); editor.CommitTextEdit();
            SaveHistoryDocument(editor, output, removed);
            Check(!File.Exists(removed) && !File.Exists(Path.ChangeExtension(removed, ".snipflow")), "Delayed save recreated a deleted history card.");
            var reopened = new EditorView(); OpenHistoryDocument(reopened, output); CheckHistoryText(reopened, "Keep saved document only", "Deleting history also discarded the independently saved document.");
            var outsider = Path.Combine(root, "outside-history.png"); ImageTools.SavePng(editor.ExportImage(), outsider); var before = Hash(File.ReadAllBytes(outsider));
            var snapshot = Call(editor, "CaptureSession")!;
            ExpectHistoryFailure(() => Static(typeof(HistoryStore), "UpdateDocument", snapshot, editor.ExportImage(), outsider, directory));
            Check(Hash(File.ReadAllBytes(outsider)) == before && !File.Exists(Path.ChangeExtension(outsider, ".snipflow")), "History update wrote outside the allowed directory or accepted a non-history filename.");
        });
    }

    static JsonObject RunHistoryOpenProbe(string imagePath, string output)
    {
        Directory.CreateDirectory(output);
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Current test host unavailable.");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--history-annotation-open-probe"); start.ArgumentList.Add(imagePath); start.ArgumentList.Add("--output"); start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch history reopen probe.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("History reopen probe exceeded 60 seconds."); }
        var outputText = stdout.GetAwaiter().GetResult(); var errorText = stderr.GetAwaiter().GetResult();
        Check(process.ExitCode == 0, "Fresh history reopen probe failed: " + outputText + errorText);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(output, "history-open-probe.json")))!.AsObject();
    }

    static int WriteHistoryOpenProbe(string imagePath)
    {
        var lazy = SettingsLazy(); var before = LazyCreated(lazy);
        var editor = new EditorView(); OpenHistoryDocument(editor, imagePath);
        var report = new { processId = Environment.ProcessId, productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)), lazyCurrentBefore = before, lazyCurrentAfter = LazyCreated(lazy), pixelHash = PixelHash(editor.ExportImage()), texts = Items(editor).Cast<object>().Where(item => (AnnotationTool)Get(item, "Tool")! == AnnotationTool.Text).Select(item => (string)Get(item, "Text")!).ToArray() };
        File.WriteAllText(Path.Combine(Output, "history-open-probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return before || LazyCreated(lazy) ? 1 : 0;
    }

    static int RunHistoryAnnotationOnly()
    {
        var lazy = SettingsLazy();
        Check(!LazyCreated(lazy), "History regression unexpectedly started with loaded live settings.");
        HistoryAnnotationCases();
        Case("history annotation regression never loads live settings or creates a native host", () => {
            Check(!LazyCreated(lazy), "History document IO loaded SettingsStore.Current.");
            Check(Application.Current is CropTestApplication && Application.Current.ApplicationLifetime is null, "History tests created a native lifetime.");
        });
        var report = new {
            guard = "SNIPFLOW-HISTORY-ANNOTATION-010", timestamp = DateTimeOffset.Now, passed, failed,
            productAssembly = Product.Location, productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)),
            productionDocumentService = DocumentFilesType is not null,
            safety = "Offscreen EditorView only; explicitly supplied paths beneath the output directory; no MainWindow, OS input, capture, clipboard, live settings, registry, installation, or deployment.",
            baseline = "When DocumentFileStore is absent, the fixture executes the literal 0.8.9 screenshot call sites: CaptureFileStore.Save(rendered, generatedPng), then HistoryStore.Read(rawHistoryPng) + Editor.LoadImage.",
            tests = Results
        };
        File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"History annotation regression: {passed} passed, {failed} failed. {Path.Combine(Output, "results.json")}");
        return failed == 0 ? 0 : 1;
    }
}

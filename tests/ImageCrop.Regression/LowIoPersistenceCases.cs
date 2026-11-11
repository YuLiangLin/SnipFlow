using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using SnipFlow.Editor;
using SnipFlow.Imaging;
using SnipFlow.Services;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    // Calibrated filesystem observation measures actual side effects instead of inspecting
    // implementation names. Each barrier is a new, empty, fixture-owned directory.
    sealed class CreatedPathProbe : IDisposable
    {
        readonly FileSystemWatcher watcher;
        readonly string directory, prefix;
        readonly ConcurrentQueue<string> created = new();
        readonly ConcurrentQueue<Exception> errors = new();
        static readonly ConcurrentDictionary<string, byte> markerPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, ManualResetEventSlim> barriers = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        internal CreatedPathProbe(string directory, string filter, string prefix, bool recursive = false)
        {
            this.directory = Path.GetFullPath(directory); this.prefix = prefix;
            watcher = new FileSystemWatcher(this.directory, filter)
            {
                IncludeSubdirectories = recursive, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024
            };
            watcher.Created += (_, e) =>
            {
                if (barriers.TryGetValue(e.FullPath, out var barrier)) barrier.Set();
                else if (!markerPaths.ContainsKey(e.FullPath)) created.Enqueue(e.FullPath);
            };
            watcher.Error += (_, e) => errors.Enqueue(e.GetException());
            watcher.EnableRaisingEvents = true;
            Synchronize();
        }

        internal void Synchronize()
        {
            var marker = Path.Combine(directory, prefix + "probe-" + Guid.NewGuid().ToString("N"));
            markerPaths.TryAdd(marker, 0);
            using var signal = new ManualResetEventSlim();
            Check(barriers.TryAdd(marker, signal), "Filesystem observation barrier collided.");
            try
            {
                Directory.CreateDirectory(marker);
                Check(signal.Wait(TimeSpan.FromSeconds(5)), "Filesystem watcher did not observe its calibration/barrier directory.");
                Check(errors.IsEmpty, "Filesystem watcher overflowed or failed: " + string.Join("; ", errors.Select(e => e.Message)));
            }
            finally
            {
                if (Directory.Exists(marker)) Directory.Delete(marker); // Own, newly created empty directory only.
                barriers.TryRemove(marker, out _);
            }
        }

        internal string[] Snapshot()
        {
            Synchronize();
            return created.ToArray();
        }

        public void Dispose() => watcher.Dispose();
    }

    static void LowIoPersistenceCases(bool patternOnly = false)
    {
        var root = Path.Combine(Output, "low-io", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Case("SNIPFLOW-LOW-IO-011 saving a project creates one atomic file without PNG or manifest scratch directories", () =>
        {
            var target = Path.Combine(root, "direct-save.snipflow");
            var observations = RunLowIoSaveProbe(target, Path.Combine(root, "writer-probe"));
            var temporaryDirectories = observations["temporaryProjectDirectories"]!.AsArray();
            Check(temporaryDirectories.Count == 0, "Project saving created " + temporaryDirectories.Count + " temporary project directory/directories containing disk PNG/manifest staging; expected direct ZIP output.");
            Check((int)observations["atomicProjectTemporaryFiles"]! == 1, "Project saving did not use exactly one destination-local atomic temporary file.");
            Check(observations["scratchCreatedPaths"]!.AsArray().Count == 0, "Project saving materialized scratch files outside the atomic destination file.");
            Check(File.Exists(target), "Project was not published.");
        });
        if (patternOnly) return;
        Case("direct ZIP preserves editable text cropped source images and mosaic payloads", () =>
        {
            var source = Quadrants(); var editor = Editor(source, new Rect(.25, .25, .5, .5));
            BeginHistoryText(editor, "Text in a streamed project"); editor.CommitTextEdit();
            var mosaic = Activator.CreateInstance(ItemType, true)!;
            Set(mosaic, "Tool", AnnotationTool.Mosaic); Set(mosaic, "Start", new Point(10, 150)); Set(mosaic, "End", new Point(70, 210));
            Set(mosaic, "Width", 6.0); Set(mosaic, "Mosaic", ImageTools.Solid(60, 60, Colors.Purple));
            Items(editor).Add(mosaic);
            var path = Path.Combine(root, "mixed-streamed.snipflow"); var pixels = PixelHash(editor.ExportImage());
            Static(StoreType, "SaveProject", editor, path);
            using (var archive = ZipFile.OpenRead(path))
            {
                var names = archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
                Check(names.SequenceEqual(new[] { "image-0000.png", "manifest.json", "mosaic-0002.png", "original.png" }), "Streamed ZIP changed the project file layout.");
                var manifest = ReadManifest(archive);
                Check((int)manifest["SchemaVersion"]! == 8 && manifest["Annotations"]!.AsArray().Count == 3, "Streamed project lost schema compatibility or annotations.");
            }
            var reopened = new EditorView(); Static(StoreType, "RestoreProject", reopened, path);
            Check(reopened.IsCollage && Items(reopened).Count == 3 && PixelHash(reopened.ExportImage()) == pixels, "Streamed project changed render pixels or editable objects.");
            EqualRect(Crop(First(reopened)), new Rect(.25, .25, .5, .5), "Streamed project lost its source viewport.");
            Check(PixelHash((Avalonia.Media.Imaging.Bitmap)Get(First(reopened), "Image")!) == PixelHash(source), "Streamed project lost hidden original source pixels.");
            Check((string)Get(Items(reopened)[1]!, "Text")! == "Text in a streamed project" && Get(Items(reopened)[2]!, "Mosaic") is not null, "Text or mosaic payload did not roundtrip.");
        });
        Case("streamed writes preserve prior projects on validation failure and clean failed publication temporary files", () =>
        {
            var editor = Editor(Quadrants(), null); var path = Path.Combine(root, "atomic-existing.snipflow");
            Static(StoreType, "SaveProject", editor, path); var before = Hash(File.ReadAllBytes(path));
            Set(First(editor), "ImageCrop", new Rect(0, 0, 0, .5)); Rejected(() => Static(StoreType, "SaveProject", editor, path));
            Check(Hash(File.ReadAllBytes(path)) == before, "Rejected streamed snapshot replaced an existing project.");
            Set(First(editor), "ImageCrop", null);
            var blocked = Path.Combine(root, "blocked-project.snipflow"); Directory.CreateDirectory(blocked);
            ExpectHistoryFailure(() => Static(StoreType, "SaveProject", editor, blocked));
            Check(Directory.GetFiles(root, ".snipflow-*.tmp").Length == 0, "Failed project publication left its atomic temporary file behind.");
            Check(Hash(File.ReadAllBytes(path)) == before, "Failure changed an unrelated existing project.");
        });
        Case("streamed entry limits reject overflowing array span and byte writes before forwarding bytes", () =>
        {
            var limitedType = Product.GetType("SnipFlow.Services.EditorSessionStore+LimitedWriteStream", true)!;
            using var destination = new MemoryStream();
            var limited = (Stream)Activator.CreateInstance(limitedType, Any, null, new object[] { destination, 5L }, null)!;
            limited.Write(new byte[] { 1, 2 }, 0, 2); limited.Write(new byte[] { 3, 4 }.AsSpan()); limited.WriteByte(5);
            Check((long)Get(limited, "BytesWritten")! == 5 && destination.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "Entry limiter lost permitted writes or count.");
            Rejected(() => limited.Write(new byte[] { 6 }, 0, 1));
            Rejected(() => limited.Write(new byte[] { 6 }.AsSpan())); Rejected(() => limited.WriteByte(6));
            Check((long)Get(limited, "BytesWritten")! == 5 && destination.Length == 5, "Rejected bytes reached the archive stream.");
            limited.Dispose(); destination.WriteByte(9);
            Check(destination.Length == 6, "Disposing the entry limit wrapper closed the owner stream.");
        });
        Case("direct archive writing enforces both per-entry and total uncompressed byte budgets", () =>
        {
            using var bytes = new MemoryStream(); using var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true);
            var writer = StoreType.GetMethod("WriteProjectEntry", Any)!;
            var totalLimit = (long)StoreType.GetField("MaxProjectBytes", Any)!.GetRawConstantValue()!;
            var successful = new object[] { archive, "allowed.bin", 10L, (Action<Stream>)(stream => stream.Write(new byte[] { 1, 2, 3 })), totalLimit - 3 };
            writer.Invoke(null, successful); Check((long)successful[4] == totalLimit, "Aggregate byte counter did not include successful entry bytes.");
            var aggregateOverflow = new object[] { archive, "over-total.bin", 10L, (Action<Stream>)(stream => stream.WriteByte(4)), totalLimit };
            Rejected(() => writer.Invoke(null, aggregateOverflow)); Check((long)aggregateOverflow[4] == totalLimit, "Failed total-budget write advanced the counter.");
            var entryOverflow = new object[] { archive, "over-entry.bin", 2L, (Action<Stream>)(stream => stream.Write(new byte[] { 1, 2, 3 })), 0L };
            Rejected(() => writer.Invoke(null, entryOverflow)); Check((long)entryOverflow[4] == 0, "Failed entry-budget write advanced the counter.");
        });
        HistoryPruningCases(root);
    }

    static JsonObject RunLowIoSaveProbe(string target, string output)
    {
        Directory.CreateDirectory(output);
        var temporaryRoot = Path.Combine(output, "temporary"); Directory.CreateDirectory(temporaryRoot);
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Current test host unavailable.");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--low-io-save-probe"); start.ArgumentList.Add(target); start.ArgumentList.Add("--output"); start.ArgumentList.Add(output);
        start.Environment["TEMP"] = temporaryRoot; start.Environment["TMP"] = temporaryRoot; start.Environment["TMPDIR"] = temporaryRoot;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch isolated save probe.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Isolated save probe exceeded 60 seconds."); }
        Check(process.ExitCode == 0, "Isolated save probe failed: " + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "filesystem-observations.json")))!.AsObject();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Check(string.Equals(Path.TrimEndingDirectorySeparator((string)result["temporaryRoot"]!), temporaryRoot, comparison), "Save probe did not use its dedicated temporary directory.");
        Check((int)result["processId"]! != Environment.ProcessId && !(bool)result["lazyCurrentBefore"]! && !(bool)result["lazyCurrentAfter"]!, "Save probe reused the current process or loaded live settings.");
        return result;
    }

    static int WriteLowIoSaveProbe(string target)
    {
        var lazy = SettingsLazy(); var before = LazyCreated(lazy);
        var editor = new EditorView(); editor.LoadImage(ImageTools.Solid(320, 240, Colors.White));
        BeginHistoryText(editor, "Direct project persistence"); editor.CommitTextEdit();
        using var scratch = new CreatedPathProbe(Path.GetTempPath(), "*", "io-", recursive: true);
        using var destination = new CreatedPathProbe(Path.GetDirectoryName(target)!, "*", "io-", recursive: true);
        Static(StoreType, "SaveProject", editor, target);
        var scratchPaths = scratch.Snapshot(); var destinationPaths = destination.Snapshot();
        var observations = new
        {
            operation = "EditorSessionStore.SaveProject(EditorView, absolute project path)", processId = Environment.ProcessId,
            productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)), lazyCurrentBefore = before, lazyCurrentAfter = LazyCreated(lazy),
            temporaryRoot = Path.GetFullPath(Path.GetTempPath()), scratchCreatedPaths = scratchPaths,
            temporaryProjectDirectories = scratchPaths.Where(path => Path.GetFileName(path).StartsWith("snipflow-project-", StringComparison.Ordinal)).ToArray(),
            destinationCreatedPaths = destinationPaths,
            atomicProjectTemporaryFiles = destinationPaths.Count(path => Path.GetFileName(path).StartsWith(".snipflow-", StringComparison.Ordinal) && path.EndsWith(".tmp", StringComparison.Ordinal)),
            watcherMethod = "Dedicated child TEMP/TMP/TMPDIR; real Created events calibrated before and after save using own empty barrier directories; no parent environment changes."
        };
        File.WriteAllText(Path.Combine(Output, "filesystem-observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        return before || LazyCreated(lazy) ? 1 : 0;
    }

    static string[] SeedHistory(string directory, int count)
    {
        Directory.CreateDirectory(directory);
        var image = ImageTools.Solid(32, 24, Colors.White); var result = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(directory, "20200101_000000_000_" + Guid.NewGuid().ToString("N") + ".png");
            ImageTools.SavePng(image, path); File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index));
            File.WriteAllText(Path.ChangeExtension(path, ".snipflow"), "fixture-owned history companion"); result.Add(path);
        }
        return result.ToArray();
    }

    static void HistoryPruningCases(string root)
    {
        Case("new capture prunes at most two oldest history pairs from an existing backlog", () =>
        {
            var directory = Path.Combine(root, "history-backlog"); var seeded = SeedHistory(directory, 13);
            var added = (string)Static(typeof(HistoryStore), "Add", ImageTools.Solid(32, 24, Colors.LightBlue), directory, 10)!;
            Check(File.Exists(added) && Directory.GetFiles(directory, "*.png").Length == 12, "Automatic history cleanup was unbounded or did not make progress.");
            Check(!File.Exists(seeded[0]) && !File.Exists(seeded[1]) && !File.Exists(Path.ChangeExtension(seeded[0], ".snipflow")) && !File.Exists(Path.ChangeExtension(seeded[1], ".snipflow")), "History cleanup did not remove the two oldest complete pairs.");
            Check(seeded.Skip(2).All(File.Exists), "History cleanup removed newer captures while oldest ones remained.");
            var next = (string)Static(typeof(HistoryStore), "Add", ImageTools.Solid(32, 24, Colors.LightBlue), directory, 10)!;
            Check(File.Exists(next) && Directory.GetFiles(directory, "*.png").Length == 11 && !File.Exists(seeded[2]) && !File.Exists(seeded[3]), "Bounded cleanup did not continue reducing the backlog on the next capture.");
        });
        Case("ordinary history retention keeps the configured limit and ignores unrelated files", () =>
        {
            var directory = Path.Combine(root, "history-normal"); var seeded = SeedHistory(directory, 10);
            var outsider = Path.Combine(directory, "user-picture.png"); File.WriteAllText(outsider, "unrelated user file");
            var added = (string)Static(typeof(HistoryStore), "Add", ImageTools.Solid(32, 24, Colors.LightBlue), directory, 10)!;
            Check(!File.Exists(seeded[0]) && seeded.Skip(1).All(File.Exists) && File.Exists(added), "Normal retention did not replace exactly the oldest capture.");
            Check(Directory.GetFiles(directory, "*.png").Length == 11 && File.ReadAllText(outsider) == "unrelated user file", "Retention counted or changed an unrelated PNG filename.");
        });
        Case("automatic history cleanup stops after the first protected companion failure", () =>
        {
            var directory = Path.Combine(root, "history-protected"); var seeded = SeedHistory(directory, 13);
            var protectedCompanion = Path.ChangeExtension(seeded[0], ".snipflow"); File.Delete(protectedCompanion); Directory.CreateDirectory(protectedCompanion);
            var added = (string)Static(typeof(HistoryStore), "Add", ImageTools.Solid(32, 24, Colors.LightBlue), directory, 10)!;
            Check(File.Exists(added) && seeded.All(File.Exists) && Directory.GetFiles(directory, "*.png").Length == 14, "Automatic cleanup continued deleting other captures after a protected oldest companion failed.");
            Check(Directory.Exists(protectedCompanion), "Cleanup removed a protected companion directory.");
            Check(seeded.Skip(1).All(path => File.Exists(Path.ChangeExtension(path, ".snipflow"))), "Cleanup changed another history companion after the protected oldest pair failed.");
        });
    }

    static int RunLowIoOnly(bool patternOnly = false)
    {
        var lazy = SettingsLazy();
        Check(!LazyCreated(lazy), "Low-IO regression unexpectedly loaded live settings.");
        LowIoPersistenceCases(patternOnly);
        Case("low-IO regression keeps live settings unloaded and creates no native host", () =>
        {
            Check(!LazyCreated(lazy), "Low-IO save loaded SettingsStore.Current.");
            Check(Application.Current is CropTestApplication && Application.Current.ApplicationLifetime is null, "Low-IO tests created a native application lifetime.");
        });
        var report = new
        {
            guard = "SNIPFLOW-LOW-IO-011", timestamp = DateTimeOffset.Now, passed, failed,
            productAssembly = Product.Location, productAssemblySha256 = Hash(File.ReadAllBytes(Product.Location)),
            safety = "Offscreen editor and supplied fixture paths only; watches directory creation events with calibrated barriers, no MainWindow, live settings, OS input, antivirus change, application installation, or executable launch.",
            limitations = "Observes filesystem activity and persistence behavior; does not prove antivirus acceptance or a specific vendor's detection trigger.", tests = Results
        };
        File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Low-IO regression: {passed} passed, {failed} failed. {Path.Combine(Output, "results.json")}");
        return failed == 0 ? 0 : 1;
    }
}

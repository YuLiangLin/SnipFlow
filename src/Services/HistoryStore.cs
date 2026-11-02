using System.Globalization;
using SnipFlow.Editor;

namespace SnipFlow.Services;
public enum HistoryKind { Screenshot, Recording }
public sealed record HistoryItem(string Path, string Title, string Detail, Bitmap? Thumbnail)
{
    public HistoryKind Kind { get; init; }
    public bool IsRecording => Kind == HistoryKind.Recording;
    public DateTimeOffset RecordedAtUtc { get; init; }
    public string? RecordingId { get; init; }
    public TimeSpan? Duration { get; init; }
}
public sealed record HistoryClearResult(int DeletedCount, int FailedCount);

public static class HistoryStore
{
    static readonly object Gate = new();

    public static IReadOnlyList<HistoryItem> Load()
    {
        FileInfo[] files;
        lock (Gate)
        {
            var directory = GetSafeDirectory(create: true);
            files = GetHistoryFiles(directory).OrderByDescending(f => f.LastWriteTimeUtc).Take(200).ToArray();
        }
        var items = new List<HistoryItem>();
        foreach (var file in files)
        {
            try
            {
                if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                var thumb = ImageTools.LoadThumbnail(file.FullName, 240);
                items.Add(new(file.FullName, file.LastWriteTime.ToString("MM/dd  HH:mm:ss"), $"{Math.Max(1, file.Length / 1024):N0} KB · PNG", thumb)
                { RecordedAtUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException) { SettingsStore.Log(ex); }
        }
        return items;
    }
    public static string? Add(Bitmap image)
    {
        if (!SettingsStore.Current.KeepHistory) return null;
        // Encode outside the lock; only publishing and pruning the history files are serialized.
        using var encoded = new MemoryStream(ImageTools.EncodePng(image), writable: false);
        lock (Gate)
        {
            if (!SettingsStore.Current.KeepHistory) return null;
            var directory = GetSafeDirectory(create: true);
            var path = Path.Combine(directory, $"{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}.png");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoded.CopyTo(stream);
            foreach (var file in GetHistoryFiles(directory).OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Math.Clamp(SettingsStore.Current.HistoryLimit, 10, 200)))
            {
                try { DeleteFile(directory, file.FullName); }
                catch (Exception ex) when (IsFileOperationError(ex)) { SettingsStore.Log(ex); }
            }
            return path;
        }
    }

    public static bool Delete(HistoryItem item)
        => Delete(item, SettingsStore.HistoryDirectory);

    internal static bool Delete(HistoryItem item, string directory)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsRecording) throw new IOException(I18n.T("錄影紀錄只能從影片歷史中移除。"));
        lock (Gate) return DeleteFile(GetSafeDirectory(directory, create: false), item.Path);
    }

    public static HistoryClearResult Clear()
        => ClearCore(SettingsStore.HistoryDirectory, logErrors: true);

    internal static HistoryClearResult Clear(string historyDirectory)
        => ClearCore(historyDirectory, logErrors: false);

    static HistoryClearResult ClearCore(string historyDirectory, bool logErrors)
    {
        lock (Gate)
        {
            var directory = GetSafeDirectory(historyDirectory, create: false);
            if (!TryGetAttributes(directory, out _)) return new(0, 0);
            int deleted = 0, failed = 0;
            foreach (var file in GetHistoryFiles(directory))
            {
                try { if (DeleteFile(directory, file.FullName)) deleted++; }
                catch (Exception ex) when (IsFileOperationError(ex))
                {
                    failed++;
                    if (logErrors) SettingsStore.Log(ex);
                    else System.Diagnostics.Trace.WriteLine(ex);
                }
            }
            return new(deleted, failed);
        }
    }

    internal static string? TryGetDocumentPath(string path)
        => TryGetDocumentPath(path, SettingsStore.HistoryDirectory);

    internal static string? TryGetDocumentPath(string path, string directory)
    {
        if (!Path.IsPathFullyQualified(path) || !IsHistoryFileName(Path.GetFileName(path))) return null;
        var target = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(target), root, PlatformInfo.PathComparison)) return null;
        lock (Gate)
        {
            ValidateDirectoryChain(root);
            if (!TryGetAttributes(target, out var attributes)) return null;
            RejectNonFile(attributes);
            return target;
        }
    }

    internal static void UpdateDocument(EditorSessionSnapshot snapshot, Bitmap rendered, string path)
        => UpdateDocument(snapshot, rendered, path, SettingsStore.HistoryDirectory);

    internal static void UpdateDocument(EditorSessionSnapshot snapshot, Bitmap rendered, string path, string historyDirectory)
    {
        lock (Gate)
        {
            var directory = GetSafeDirectory(historyDirectory, create: false);
            var target = ValidateHistoryPath(directory, path);
            // Deletion and pruning share Gate. A delayed autosave must never recreate a removed card.
            if (!TryGetAttributes(target, out var attributes)) return;
            RejectNonFile(attributes);
            var project = Path.ChangeExtension(target, ".snipflow");
            ValidateSidecar(project);
            var capturedAt = File.GetLastWriteTimeUtc(target);
            EditorSessionStore.SaveProject(snapshot, project);
            CaptureFileStore.Save(rendered, target);
            // History order, age and retention refer to capture time rather than the latest edit.
            File.SetLastWriteTimeUtc(target, capturedAt);
        }
    }

    static FileInfo[] GetHistoryFiles(string directory) => new DirectoryInfo(directory)
        .GetFiles("*", SearchOption.TopDirectoryOnly)
        .Where(file => IsHistoryFileName(file.Name)).ToArray();

    static bool IsHistoryFileName(string name)
    {
        if (!Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length == 52 && stem[19] == '_'
            && DateTime.TryParseExact(stem.AsSpan(0, 19), "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && Guid.TryParseExact(stem.AsSpan(20), "N", out _);
    }

    static string GetSafeDirectory(bool create) => GetSafeDirectory(SettingsStore.HistoryDirectory, create);

    static string GetSafeDirectory(string path, bool create)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException(I18n.T("歷史檔案不在允許的資料夾內。"));
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        ValidateDirectoryChain(directory);
        if (create)
        {
            Directory.CreateDirectory(directory);
            ValidateDirectoryChain(directory);
        }
        return directory;
    }

    static void ValidateDirectoryChain(string directory)
    {
        // An ancestor junction can redirect an otherwise well-formed history path too.
        for (DirectoryInfo? current = new(directory); current != null; current = current.Parent)
        {
            if (!TryGetAttributes(current.FullName, out var attributes)) continue;
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(I18n.T("歷史資料夾不是一般資料夾，已停止操作。"));
        }
    }

    static bool DeleteFile(string directory, string path)
    {
        var target = ValidateHistoryPath(directory, path);
        ValidateDirectoryChain(directory);
        if (!TryGetAttributes(target, out var attributes)) return false;
        RejectNonFile(attributes);
        var project = Path.ChangeExtension(target, ".snipflow");
        var hasProject = ValidateSidecar(project);
        // Keep the editable project if the PNG cannot be removed. If companion removal fails,
        // report the failure while leaving that recoverable project intact.
        File.Delete(target);
        if (hasProject) File.Delete(project);
        return true;
    }

    static string ValidateHistoryPath(string directory, string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException(I18n.T("歷史檔案不在允許的資料夾內。"));
        var target = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(target), directory, PlatformInfo.PathComparison)
            || Path.GetFileName(target).Contains(':'))
            throw new IOException(I18n.T("歷史檔案不在允許的資料夾內。"));
        if (!IsHistoryFileName(Path.GetFileName(target)))
            throw new IOException(I18n.T("歷史清理僅能刪除一般 PNG 歷史副本。"));
        return target;
    }

    static void RejectNonFile(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException(I18n.T("歷史清理僅能刪除一般 PNG 歷史副本。"));
    }

    static bool ValidateSidecar(string path)
    {
        if (!TryGetAttributes(path, out var attributes)) return false;
        RejectNonFile(attributes);
        return true;
    }

    static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { attributes = default; return false; }
        catch (DirectoryNotFoundException) { attributes = default; return false; }
    }

    static bool IsFileOperationError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    public static Bitmap Read(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".snipvideo", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(I18n.T("影片不能以圖片方式開啟。"));
        return ImageTools.Load(Path.GetFullPath(path));
    }
    public static void SavePng(Bitmap image, string path) => ImageTools.SavePng(image, path);
}

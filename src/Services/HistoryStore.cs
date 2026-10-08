using System.Globalization;

namespace SnipFlow.Services;
public sealed record HistoryItem(string Path, string Title, string Detail, BitmapSource Thumbnail);
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
                var thumb = new BitmapImage(); thumb.BeginInit(); thumb.CacheOption = BitmapCacheOption.OnLoad;
                thumb.DecodePixelWidth = 240; thumb.UriSource = new Uri(file.FullName); thumb.EndInit(); thumb.Freeze();
                items.Add(new(file.FullName, file.LastWriteTime.ToString("MM/dd  HH:mm:ss"), $"{Math.Max(1, file.Length / 1024):N0} KB · PNG", thumb));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { SettingsStore.Log(ex); }
        }
        return items;
    }
    public static string? Add(BitmapSource image)
    {
        if (!SettingsStore.Current.KeepHistory) return null;
        // Encode outside the lock; only publishing and pruning the history files are serialized.
        using var encoded = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image, null, null, null)); encoder.Save(encoded);
        encoded.Position = 0;
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
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (Gate) return DeleteFile(GetSafeDirectory(create: false), item.Path);
    }

    public static HistoryClearResult Clear()
    {
        lock (Gate)
        {
            var directory = GetSafeDirectory(create: false);
            if (!TryGetAttributes(directory, out _)) return new(0, 0);
            int deleted = 0, failed = 0;
            foreach (var file in GetHistoryFiles(directory))
            {
                try { if (DeleteFile(directory, file.FullName)) deleted++; }
                catch (Exception ex) when (IsFileOperationError(ex)) { failed++; SettingsStore.Log(ex); }
            }
            return new(deleted, failed);
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

    static string GetSafeDirectory(bool create)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SettingsStore.HistoryDirectory));
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
        if (!Path.IsPathFullyQualified(path))
            throw new IOException(I18n.T("歷史檔案不在允許的資料夾內。"));
        var target = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(target), directory, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(target).Contains(':'))
            throw new IOException(I18n.T("歷史檔案不在允許的資料夾內。"));
        if (!IsHistoryFileName(Path.GetFileName(target)))
            throw new IOException(I18n.T("歷史清理僅能刪除一般 PNG 歷史副本。"));
        ValidateDirectoryChain(directory);
        if (!TryGetAttributes(target, out var attributes)) return false;
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException(I18n.T("歷史清理僅能刪除一般 PNG 歷史副本。"));
        File.Delete(target);
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
    public static BitmapSource Read(string path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze(); return image;
    }
    public static void SavePng(BitmapSource image, string path)
    {
        // Explicit metadata avoids accessing a frozen frame's thread-bound decoder.
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image, null, null, null));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}

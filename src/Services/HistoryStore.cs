namespace SnipFlow.Services;
public sealed record HistoryItem(string Path, string Title, string Detail, BitmapSource Thumbnail);

public static class HistoryStore
{
    public static IReadOnlyList<HistoryItem> Load()
    {
        Directory.CreateDirectory(SettingsStore.HistoryDirectory);
        var items = new List<HistoryItem>();
        foreach (var file in new DirectoryInfo(SettingsStore.HistoryDirectory).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(200))
        {
            try
            {
                var thumb = new BitmapImage(); thumb.BeginInit(); thumb.CacheOption = BitmapCacheOption.OnLoad;
                thumb.DecodePixelWidth = 240; thumb.UriSource = new Uri(file.FullName); thumb.EndInit(); thumb.Freeze();
                items.Add(new(file.FullName, file.LastWriteTime.ToString("MM/dd  HH:mm:ss"), $"{Math.Max(1, file.Length / 1024):N0} KB · PNG", thumb));
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException) { SettingsStore.Log(ex); }
        }
        return items;
    }
    public static string? Add(BitmapSource image)
    {
        if (!SettingsStore.Current.KeepHistory) return null;
        Directory.CreateDirectory(SettingsStore.HistoryDirectory);
        var path = Path.Combine(SettingsStore.HistoryDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        SavePng(image, path);
        foreach (var file in new DirectoryInfo(SettingsStore.HistoryDirectory).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Math.Clamp(SettingsStore.Current.HistoryLimit, 10, 200))) file.Delete();
        return path;
    }
    public static BitmapSource Read(string path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze(); return image;
    }
    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}

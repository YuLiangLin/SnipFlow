namespace SnipFlow.Services;

/// <summary>Writes each document to its own PNG, replacing it atomically after edits.</summary>
public static class CaptureFileStore
{
    public static string CreatePath(string directory) => Path.Combine(
        SettingsStore.NormalizeCaptureSaveDirectory(directory),
        $"SnipFlow_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");

    public static void Save(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            HistoryStore.SavePng(image, temporaryPath);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            // Only remove the temporary file created by this write.
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

using SnipFlow.Editor;

namespace SnipFlow.Services;

/// <summary>Keeps the rendered image and the editable document together across capture switches.</summary>
internal static class DocumentFileStore
{
    internal static void Save(EditorSessionSnapshot snapshot, Bitmap rendered, string pngPath, string? historyPath)
        => Save(snapshot, rendered, pngPath, historyPath,
            historyPath is null ? null : SettingsStore.HistoryDirectory);

    // The explicit history scope is also used by isolated regression fixtures.
    internal static void Save(EditorSessionSnapshot snapshot, Bitmap rendered, string pngPath,
        string? historyPath, string? historyDirectory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        if (!Path.IsPathFullyQualified(pngPath)
            || !Path.GetExtension(pngPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The rendered document path must be an absolute PNG path.", nameof(pngPath));
        if (historyPath is not null) ArgumentException.ThrowIfNullOrWhiteSpace(historyDirectory);
        var path = Path.GetFullPath(pngPath);
        // Publish the editable source first. A PNG write failure still leaves a complete project.
        EditorSessionStore.SaveProject(snapshot, Path.ChangeExtension(path, ".snipflow"));
        CaptureFileStore.Save(rendered, path);
        if (historyPath is not null)
            HistoryStore.UpdateDocument(snapshot, rendered, historyPath, historyDirectory!);
    }

    internal static void Open(EditorView editor, string imagePath)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var path = Path.GetFullPath(imagePath);
        var project = Path.ChangeExtension(path, ".snipflow");
        // An invalid or inaccessible project must be reported rather than silently losing edits.
        try { _ = File.GetAttributes(project); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            editor.LoadImage(HistoryStore.Read(path));
            return;
        }
        EditorSessionStore.RestoreProject(editor, project);
    }
}

using Avalonia.Platform.Storage;

namespace SnipFlow.Ui;

/// <summary>
/// Blocking file pickers built on the platform storage provider (Windows shell dialogs, or the
/// xdg-desktop-portal / GTK file chooser on Linux). Filters use the WPF syntax
/// <c>"Name|*.png;*.jpg|All files|*.*"</c>, so translated filter strings stay unchanged.
/// </summary>
public static class FileDialogs
{
    public static IReadOnlyList<FilePickerFileType> ParseFilter(string? filter)
    {
        var result = new List<FilePickerFileType>();
        if (string.IsNullOrWhiteSpace(filter)) return result;
        var parts = filter.Split('|');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            result.Add(new FilePickerFileType(parts[i].Trim()) { Patterns = patterns });
        }
        return result;
    }

    /// <summary>Returns the chosen local paths, or null when cancelled.</summary>
    public static string[]? OpenFiles(TopLevel? owner, string? title, string? filter, bool allowMultiple = false,
        string? initialDirectory = null)
    {
        var provider = Provider(owner);
        if (provider is null) return null;
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = ParseFilter(filter),
            SuggestedStartLocation = Folder(provider, initialDirectory),
        };
        var files = Modal.Wait(provider.OpenFilePickerAsync(options));
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        return paths.Length == 0 ? null : paths;
    }

    public static string? OpenFile(TopLevel? owner, string? title, string? filter, string? initialDirectory = null)
        => OpenFiles(owner, title, filter, allowMultiple: false, initialDirectory)?.FirstOrDefault();

    /// <summary>Returns the chosen local path, or null when cancelled. Adds the default extension when missing.</summary>
    public static string? SaveFile(TopLevel? owner, string? title, string? filter, string? suggestedFileName,
        string? defaultExtension = null, string? initialDirectory = null)
    {
        var provider = Provider(owner);
        if (provider is null) return null;
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension?.TrimStart('.'),
            FileTypeChoices = ParseFilter(filter),
            ShowOverwritePrompt = true,
            SuggestedStartLocation = Folder(provider, initialDirectory),
        };
        var file = Modal.Wait(provider.SaveFilePickerAsync(options));
        var path = file?.TryGetLocalPath();
        if (path is null) return null;
        if (!string.IsNullOrEmpty(defaultExtension) && string.IsNullOrEmpty(Path.GetExtension(path)))
            path += defaultExtension.StartsWith('.') ? defaultExtension : "." + defaultExtension;
        return path;
    }

    /// <summary>Returns the chosen local folder, or null when cancelled.</summary>
    public static string? PickFolder(TopLevel? owner, string? title, string? initialDirectory = null)
    {
        var provider = Provider(owner);
        if (provider is null) return null;
        var folders = Modal.Wait(provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = Folder(provider, initialDirectory),
        }));
        return folders.Select(folder => folder.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    static IStorageProvider? Provider(TopLevel? owner)
    {
        owner ??= Modal.DefaultOwner();
        if (owner is null && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            owner = desktop.MainWindow;
        return owner?.StorageProvider;
    }

    static IStorageFolder? Folder(IStorageProvider provider, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        try { return Modal.Wait(provider.TryGetFolderFromPathAsync(directory)); }
        catch (Exception) { return null; }
    }
}

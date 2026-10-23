using SnipFlow.Services;

namespace SnipFlow.Video;

/// <remarks>
/// Path identity and volume checks are platform specific: Windows uses file handles
/// (GetFinalPathNameByHandle, file index) and Linux uses /proc/self/fd and statx (device + inode).
/// </remarks>
internal static partial class VideoFileGuard
{
    internal static void ValidateProjectSource(string source)
    {
        if (!Path.IsPathFullyQualified(source) || IsRemoteLocation(source))
            throw new InvalidOperationException(I18n.T("影片專案需連結本機影片，請先開啟原始 MP4。"));
        var full = Path.GetFullPath(source);
        var parents = new List<string>();
        for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
            parents.Add(parent.FullName);
        // Inspect ancestors from the volume down, before following any child path.
        // On Linux a symbolic link reports ReparsePoint, like a Windows junction.
        foreach (var path in parents.AsEnumerable().Reverse().Append(full))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(I18n.T("影片專案需連結本機影片，請先開啟原始 MP4。"));
        }
    }

    /// <summary>The final, link-free path of an existing file, resolved through an open handle.</summary>
    internal static string ResolveFile(string path)
    {
        using var handle = File.OpenHandle(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return FinalPath(handle);
    }

    internal static string ValidateOutput(string destination, string source, string extension, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var output = Path.GetFullPath(destination);
        // A colon selects an NTFS alternate data stream on Windows; it is an ordinary character on Linux.
        if (!Path.GetExtension(output).Equals(extension, StringComparison.OrdinalIgnoreCase)
            || (PlatformInfo.IsWindows && Path.GetFileName(output).Contains(':')))
            throw new ArgumentException(I18n.F("檔案副檔名必須是 {0}。", extension));
        if (output.Equals(Path.GetFullPath(source), PlatformInfo.PathComparison)
            || (File.Exists(output) && SameFile(output, source)))
            throw new IOException(I18n.T("請另存新檔，原始影片不會被覆寫。"));
        if (!overwrite && File.Exists(output))
            throw new IOException(I18n.T("檔案已存在，請選擇其他檔名。"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        return output;
    }

    private static bool SameFile(string first, string second)
    {
        using var a = File.OpenHandle(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = File.OpenHandle(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return FileIdentity(a) == FileIdentity(b);
    }

    internal static string CreatePartial(string output, string extension)
    {
        var path = Path.Combine(Path.GetDirectoryName(output)!, $".snipflow-video-{Guid.NewGuid():N}.partial{extension}");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return path;
    }

    internal static void DeleteOwnedFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SettingsStore.Log(error); }
    }

    /// <summary>True for UNC/network shares (Windows) or network file systems (Linux NFS, SMB, SSHFS…).</summary>
    private static partial bool IsRemoteLocation(string path);

    private static partial string FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle);

    /// <summary>Volume and file number of an open file; equal values mean the same file (including hard links).</summary>
    private static partial (ulong Volume, ulong File) FileIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle);
}

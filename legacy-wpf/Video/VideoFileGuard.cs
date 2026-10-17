using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SnipFlow.Services;

namespace SnipFlow.Video;

internal static class VideoFileGuard
{
    internal static void ValidateProjectSource(string source)
    {
        if (!Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(source)!).DriveType == DriveType.Network)
            throw new InvalidOperationException(I18n.T("影片專案需連結本機影片，請先開啟原始 MP4。"));
        var full = Path.GetFullPath(source);
        var parents = new List<string>();
        for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
            parents.Add(parent.FullName);
        // Inspect ancestors from the volume down, before following any child path.
        foreach (var path in parents.AsEnumerable().Reverse().Append(full))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(I18n.T("影片專案需連結本機影片，請先開啟原始 MP4。"));
        }
    }

    internal static string ResolveFile(string path)
    {
        using var handle = File.OpenHandle(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var full = buffer.ToString();
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + full[8..];
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
    }

    internal static string ValidateOutput(string destination, string source, string extension, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var output = Path.GetFullPath(destination);
        if (!Path.GetExtension(output).Equals(extension, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(output).Contains(':'))
            throw new ArgumentException(I18n.F("檔案副檔名必須是 {0}。", extension));
        if (output.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
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
        if (!GetFileInformationByHandle(a, out var x) || !GetFileInformationByHandle(b, out var y))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return x.VolumeSerialNumber == y.VolumeSerialNumber && x.FileIndexHigh == y.FileIndexHigh && x.FileIndexLow == y.FileIndexLow;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationTimeLow, CreationTimeHigh, AccessTimeLow, AccessTimeHigh,
            WriteTimeLow, WriteTimeHigh, VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}

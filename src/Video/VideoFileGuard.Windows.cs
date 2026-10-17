using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SnipFlow.Video;

internal static partial class VideoFileGuard
{
    private static partial bool IsRemoteLocation(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network;

    private static partial string FinalPath(SafeFileHandle handle)
    {
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

    private static partial (ulong Volume, ulong File) FileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
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

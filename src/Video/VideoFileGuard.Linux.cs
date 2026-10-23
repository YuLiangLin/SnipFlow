using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SnipFlow.Video;

internal static partial class VideoFileGuard
{
    // File systems whose files live on another machine. gvfsd-fuse backs GNOME's SMB/SFTP/WebDAV mounts.
    private static readonly HashSet<string> RemoteFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfs", "nfs4", "cifs", "smb3", "smbfs", "ncpfs", "9p", "afs", "ceph", "glusterfs", "lustre", "davfs",
        "fuse.sshfs", "fuse.gvfsd-fuse", "fuse.rclone", "fuse.s3fs", "fuse.davfs2", "fuse.glusterfs", "fuse.cephfs",
    };

    private static partial bool IsRemoteLocation(string path)
    {
        var full = Path.GetFullPath(path);
        DriveInfo? mount = null;
        try
        {
            // The longest mount point that contains the path is the file system that stores it.
            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.Name;
                var contains = root == "/" || full.Equals(root, StringComparison.Ordinal)
                    || full.StartsWith(Path.TrimEndingDirectorySeparator(root) + "/", StringComparison.Ordinal);
                if (contains && (mount is null || root.Length > mount.Name.Length)) mount = drive;
            }
            if (mount is null) return false;
            var format = mount.DriveFormat;
            return mount.DriveType == DriveType.Network || RemoteFileSystems.Contains(format)
                || format.StartsWith("nfs", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unreadable mount table cannot prove the file is local.
            SettingsStore.Log(error);
            return true;
        }
    }

    private static partial string FinalPath(SafeFileHandle handle)
    {
        // /proc/self/fd/N names the file this handle opened, with every symbolic link resolved.
        var descriptor = (int)handle.DangerousGetHandle();
        string? target = null;
        try { target = new FileInfo($"/proc/self/fd/{descriptor}").LinkTarget; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SettingsStore.Log(error); }
        if (target is null || !target.StartsWith('/'))
            throw new IOException(I18n.T("找不到原始影片，請確認檔案仍在原位置。"));
        if (target.EndsWith(" (deleted)", StringComparison.Ordinal) && !File.Exists(target))
            throw new FileNotFoundException(I18n.T("找不到原始影片，請確認檔案仍在原位置。"), target);
        return target;
    }

    private static partial (ulong Volume, ulong File) FileIdentity(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        // statx has one layout on every Linux architecture, unlike struct stat.
        if (Statx((int)handle.DangerousGetHandle(), "", AtEmptyPath, StatxIno, buffer) != 0)
            throw new IOException(I18n.F("無法確認影片檔案：{0}", Marshal.GetLastPInvokeErrorMessage()));
        var inode = BitConverter.ToUInt64(buffer, 32);
        var deviceMajor = BitConverter.ToUInt32(buffer, 136);
        var deviceMinor = BitConverter.ToUInt32(buffer, 140);
        return (((ulong)deviceMajor << 32) | deviceMinor, inode);
    }

    private const int AtEmptyPath = 0x1000;
    private const uint StatxIno = 0x100;

    [DllImport("libc.so.6", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);
}

using System.Globalization;
using System.Runtime.InteropServices;

namespace SnipFlow.Platform.Linux.Recording;

/// <summary>
/// .NET passes only standard input, output and error to a child process, but the kernel also hands over every
/// descriptor that lacks close-on-exec. Native libraries in SnipFlow's process create some without it (for
/// example GStreamer's bus and clock wake-up sockets, created while a Wayland screen cast is read), so before
/// ffmpeg starts every inherited descriptor is marked close-on-exec. Only <c>exec</c> is affected; the descriptors
/// stay open and usable in SnipFlow.
/// </summary>
internal static class InheritedDescriptors
{
    private const int GetFlags = 1, SetFlags = 2, CloseOnExec = 1;

    internal static void MarkCloseOnExec()
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries("/proc/self/fd"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var descriptor)
                || descriptor <= 2)
                continue;
            var flags = fcntl(descriptor, GetFlags, 0);
            if (flags >= 0 && (flags & CloseOnExec) == 0)
                _ = fcntl(descriptor, SetFlags, flags | CloseOnExec);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command, int argument);
}

using System.Runtime.InteropServices;

namespace SnipFlow.Platform.Linux.Wayland;

/// <summary>
/// Minimal GStreamer bindings (libgstreamer-1.0, libgstapp-1.0) for reading PipeWire screen-cast
/// frames in-process. Running the pipeline in SnipFlow avoids handing the portal's PipeWire file
/// descriptor to a child process. Also usable by other modules that build their own pipelines.
/// </summary>
internal static class GStreamer
{
    private const string Gst = "libgstreamer-1.0.so.0";
    private const string GstApp = "libgstapp-1.0.so.0";
    private const string GstVideo = "libgstvideo-1.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    public const int StateNull = 1, StatePaused = 3, StatePlaying = 4;
    public const int StateChangeFailure = 0;
    public const int MessageEos = 1, MessageError = 2;
    public const int MapRead = 1;
    public const ulong Second = 1_000_000_000UL;

    private static readonly object InitGate = new();
    private static bool _initialized;

    /// <summary>
    /// Initializes GStreamer and checks that the elements in <paramref name="elements"/> exist.
    /// Throws an <see cref="InvalidOperationException"/> with a translated install hint otherwise.
    /// </summary>
    public static void EnsureAvailable(params string[] elements)
    {
        lock (InitGate)
        {
            if (!_initialized)
            {
                try
                {
                    if (!gst_init_check(IntPtr.Zero, IntPtr.Zero, out var error))
                    {
                        var message = ErrorMessage(error);
                        throw new InvalidOperationException(I18n.T("無法啟動 GStreamer。") + " " + message);
                    }
                    // Probe the app and video libraries too, so a missing package fails here.
                    _ = gst_app_sink_get_type();
                    _ = gst_video_meta_api_get_type();
                }
                catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
                {
                    throw new InvalidOperationException(I18n.T("需要 GStreamer 才能讀取 Wayland 螢幕畫面。請安裝：sudo apt install gstreamer1.0-pipewire gstreamer1.0-plugins-base"), exception);
                }
                _initialized = true;
            }
        }
        foreach (var element in elements)
        {
            var factory = gst_element_factory_find(element);
            if (factory == IntPtr.Zero)
                throw new InvalidOperationException(element == "pipewiresrc"
                    ? I18n.T("缺少 GStreamer 的 PipeWire 元件。請安裝：sudo apt install gstreamer1.0-pipewire")
                    : I18n.F("缺少 GStreamer 元件 {0}。請安裝：sudo apt install gstreamer1.0-plugins-base gstreamer1.0-plugins-good", element));
            gst_object_unref(factory);
        }
    }

    /// <summary>Reads and frees a GError, returning its message.</summary>
    public static string ErrorMessage(IntPtr error)
    {
        if (error == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? ""; }
        finally { g_error_free(error); }
    }

    /// <summary>Pops a pending ERROR or EOS message without waiting; returns null when the pipeline is healthy.</summary>
    public static string? PollFailure(IntPtr bus)
    {
        if (bus == IntPtr.Zero) return null;
        var message = gst_bus_timed_pop_filtered(bus, 0, MessageError | MessageEos);
        if (message == IntPtr.Zero) return null;
        try
        {
            var type = Marshal.ReadInt32(message, MessageTypeOffset);
            if ((type & MessageError) == 0) return I18n.T("畫面串流已結束。");
            gst_message_parse_error(message, out var error, out var debug);
            if (debug != IntPtr.Zero) g_free(debug);
            return ErrorMessage(error);
        }
        finally { gst_mini_object_unref(message); }
    }

    // GstMessage { GstMiniObject (64 bytes on LP64); GstMessageType type; ... }
    private static readonly int MessageTypeOffset = Marshal.SizeOf<GstMiniObject>();

    [StructLayout(LayoutKind.Sequential)]
    private struct GstMiniObject
    {
        public nint Type;
        public int RefCount, LockState;
        public uint Flags;
        public IntPtr Copy, Dispose, Free;
        public uint PrivUint;
        public IntPtr PrivPointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GstMapInfo
    {
        public IntPtr Memory;
        public int Flags;
        public IntPtr Data;
        public nuint Size;
        public nuint MaxSize;
        public IntPtr User0, User1, User2, User3;
        public IntPtr Reserved0, Reserved1, Reserved2, Reserved3;
    }

    /// <summary>Row stride of plane 0 from the buffer's GstVideoMeta, or 0 when the buffer has none.</summary>
    public static int VideoMetaStride(IntPtr buffer)
    {
        var meta = gst_buffer_get_meta(buffer, gst_video_meta_api_get_type());
        // GstVideoMeta: GstMeta (16) buffer (8) flags format id width height n_planes (6 × 4) offset[4] (32) stride[4].
        return meta == IntPtr.Zero ? 0 : Marshal.ReadInt32(meta, 80);
    }

    /// <summary>Byte offset of plane 0 from the buffer's GstVideoMeta, or 0.</summary>
    public static long VideoMetaOffset(IntPtr buffer)
    {
        var meta = gst_buffer_get_meta(buffer, gst_video_meta_api_get_type());
        return meta == IntPtr.Zero ? 0 : Marshal.ReadInt64(meta, 48);
    }

    [DllImport(Gst)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);
    [DllImport(Gst)] public static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out IntPtr error);
    [DllImport(Gst)] public static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Gst)] public static extern int gst_element_set_state(IntPtr element, int state);
    [DllImport(Gst)] public static extern int gst_element_get_state(IntPtr element, out int state, out int pending, ulong timeout);
    [DllImport(Gst)] public static extern IntPtr gst_element_get_bus(IntPtr element);
    [DllImport(Gst)] public static extern IntPtr gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Gst)] public static extern IntPtr gst_bus_timed_pop_filtered(IntPtr bus, ulong timeout, int types);
    [DllImport(Gst)] public static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
    [DllImport(Gst)] public static extern IntPtr gst_mini_object_ref(IntPtr miniObject);
    [DllImport(Gst)] public static extern void gst_mini_object_unref(IntPtr miniObject);
    [DllImport(Gst)] public static extern void gst_object_unref(IntPtr gstObject);
    [DllImport(Gst)] public static extern IntPtr gst_sample_get_buffer(IntPtr sample);
    [DllImport(Gst)] public static extern IntPtr gst_sample_get_caps(IntPtr sample);
    [DllImport(Gst)] public static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);
    [DllImport(Gst)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool gst_structure_get_int(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);
    [DllImport(Gst)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool gst_buffer_map(IntPtr buffer, out GstMapInfo info, int flags);
    [DllImport(Gst)] public static extern void gst_buffer_unmap(IntPtr buffer, ref GstMapInfo info);
    [DllImport(Gst)] private static extern IntPtr gst_buffer_get_meta(IntPtr buffer, nint api);
    [DllImport(GstApp)] public static extern IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeout);
    [DllImport(GstApp)] private static extern nint gst_app_sink_get_type();
    [DllImport(GstVideo)] private static extern nint gst_video_meta_api_get_type();
    [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
    [DllImport(GLib)] private static extern void g_free(IntPtr memory);
}

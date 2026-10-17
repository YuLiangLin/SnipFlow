using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SnipFlow.Services;

/// <summary>Owns the main HWND's Shell identity without replacing Avalonia's native icons.</summary>
/// <remarks>
/// SnipFlow disables built-in COM interop (BuiltInComInteropSupport=false), so the Shell interfaces
/// below use source-generated COM wrappers instead of [ComImport] runtime callable wrappers.
/// </remarks>
internal sealed unsafe partial class TaskbarIdentityService
{
    // Matches Velopack's packId=SnipFlow shortcut identity, including portable fallback.
    const string DefaultAppId = "velopack.SnipFlow";
    static readonly Guid AppProperties = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static readonly PropertyKey[] Keys = { new(AppProperties, 2), new(AppProperties, 3), new(AppProperties, 4), new(AppProperties, 5) };
    static readonly PropertyKey LinkTarget = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    static readonly StrategyBasedComWrappers ComWrappers = new();
    internal IReadOnlyList<string> NotificationPaths { get; private set; } = Array.Empty<string>();

    internal bool TryApply(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        IPropertyStore? store = null;
        var previous = new PropVariant[Keys.Length];
        bool haveSnapshot = false;
        try
        {
            var launcher = StartupService.ResolveLauncher();
            var executable = StartupService.ResolveExistingFile(Environment.ProcessPath!);
            var icon = StartupService.ResolveExistingFile(Path.Combine(AppContext.BaseDirectory, "Assets", "SnipFlow-v3.ico"));
            var (appId, shortcut) = ReadOwnedShortcut(executable, launcher);
            var values = new[] { $"\"{launcher}\"", icon + ",0", "SnipFlow", appId ?? DefaultAppId };
            var iid = typeof(IPropertyStore).GUID;
            Check(SHGetPropertyStoreForWindow(handle, in iid, out var pointer), "SHGetPropertyStoreForWindow");
            store = Wrap<IPropertyStore>(pointer) ?? throw new COMException("The window property store is unavailable.");
            for (int index = 0; index < Keys.Length; index++)
            {
                var key = Keys[index];
                Check(store.GetValue(ref key, out previous[index]), "Read the previous taskbar property");
            }
            haveSnapshot = true;
            // ID is last: its SetValue triggers the Shell to refresh all relaunch data.
            // Window stores publish immediately; Commit is not required and has no effect.
            for (int index = 0; index < Keys.Length; index++) SetString(store, Keys[index], values[index]);
            if (appId == DefaultAppId && shortcut != null)
                RepairOwnedShortcut(shortcut, executable, launcher, icon);
            NotificationPaths = new[] { launcher, executable, icon, shortcut }.Where(path => path != null).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return true;
        }
        catch (Exception error)
        {
            // A store may already contain an identity provided by the installer.
            // Preserve every original value if any of the four writes fails.
            if (haveSnapshot && store != null)
                for (int index = 0; index < Keys.Length; index++)
                {
                    try
                    {
                        var key = Keys[index];
                        Check(store.SetValue(ref key, ref previous[index]), "Roll back the taskbar property");
                    }
                    catch (Exception rollback) { SettingsStore.Log(rollback); }
                }
            SettingsStore.Log(error);
            return false;
        }
        finally
        {
            for (int index = 0; index < previous.Length; index++) ClearVariant(ref previous[index]);
            ReleaseObject(store);
        }
    }

    // Called from WM_DESTROY while the HWND is still valid, not from Closed after
    // the platform window is gone. Shell-owned property allocations must be released.
    internal static void Clear(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return;
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            Check(SHGetPropertyStoreForWindow(handle, in iid, out var pointer), "SHGetPropertyStoreForWindow during destruction");
            store = Wrap<IPropertyStore>(pointer);
            if (store == null) return;
            foreach (var originalKey in Keys)
            {
                var key = originalKey; var empty = default(PropVariant);
                try { Check(store.SetValue(ref key, ref empty), "Clear the taskbar property"); }
                catch (Exception error) { SettingsStore.Log(error); }
                finally { ClearVariant(ref empty); }
            }
        }
        catch (Exception error) { SettingsStore.Log(error); }
        finally { ReleaseObject(store); }
    }

    static (string? AppId, string? Path) ReadOwnedShortcut(string executable, string launcher)
    {
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SnipFlow.lnk");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(shortcut); }
        catch (FileNotFoundException) { return (null, null); }
        catch (DirectoryNotFoundException) { return (null, null); }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return (null, null);
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            Check(SHGetPropertyStoreFromParsingName(shortcut, IntPtr.Zero, 0, in iid, out var pointer), "Read the SnipFlow shortcut");
            store = Wrap<IPropertyStore>(pointer) ?? throw new COMException("The shortcut property store is unavailable.");
            var target = ReadString(store, LinkTarget);
            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return (null, null);
            var resolved = StartupService.ResolveExistingFile(target);
            if (!resolved.Equals(executable, StringComparison.OrdinalIgnoreCase) && !resolved.Equals(launcher, StringComparison.OrdinalIgnoreCase))
                return (null, null);
            var appId = ReadString(store, Keys[^1]);
            if (string.IsNullOrWhiteSpace(appId) || appId.Length > 128 || appId.Any(value => char.IsWhiteSpace(value) || char.IsControl(value))) appId = null;
            return (appId, StartupService.ResolveExistingFile(shortcut));
        }
        finally { ReleaseObject(store); }
    }

    static void RepairOwnedShortcut(string shortcut, string executable, string launcher, string icon)
    {
        // This is the one installer shortcut, never the user's taskbar pins. Only the
        // installer ID and empty arguments permit writing. Unknown/custom links stay intact.
        if ((File.GetAttributes(shortcut) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
            || new FileInfo(shortcut).Length > 1_048_576) return;
        var original = File.ReadAllBytes(shortcut);
        IShellLink? link = null;
        string? temporary = null;
        try
        {
            link = OpenLink(shortcut);
            var data = ReadLink(link);
            if (data.AppId != DefaultAppId || data.Arguments.Length != 0 || !Path.IsPathFullyQualified(data.Target)) return;
            var target = StartupService.ResolveExistingFile(data.Target);
            if (!target.Equals(executable, StringComparison.OrdinalIgnoreCase) && !target.Equals(launcher, StringComparison.OrdinalIgnoreCase)) return;
            var workingDirectory = Path.GetDirectoryName(launcher)!;
            if (data.Target.Equals(launcher, StringComparison.OrdinalIgnoreCase) && data.Icon.Equals(icon, StringComparison.OrdinalIgnoreCase)
                && data.IconIndex == 0 && data.WorkingDirectory.Equals(workingDirectory, StringComparison.OrdinalIgnoreCase)) return;
            Check(link.SetPath(launcher), "Set the shortcut launcher");
            Check(link.SetIconLocation(icon, 0), "Set the shortcut icon");
            Check(link.SetWorkingDirectory(workingDirectory), "Set the shortcut working directory");

            var stagedPath = Path.Combine(Path.GetDirectoryName(shortcut)!, ".SnipFlow-" + Guid.NewGuid().ToString("N") + ".tmp");
            if (File.Exists(stagedPath)) throw new IOException("The temporary shortcut already exists.");
            temporary = stagedPath;
            Check(((IPersistLink)link).Save(temporary, false), "Stage the repaired shortcut");
            IShellLink? staged = null;
            try
            {
                staged = OpenLink(temporary);
                var saved = ReadLink(staged);
                if (saved.Target != launcher || saved.Icon != icon || saved.IconIndex != 0 || saved.WorkingDirectory != workingDirectory
                    || saved.AppId != DefaultAppId || saved.Arguments.Length != 0)
                    throw new IOException("The staged shortcut did not retain its target, icon, working directory or identity.");
            }
            finally { ReleaseObject(staged); }

            // Stage and verify before publishing. A failed save leaves the original link
            // unchanged; compare its bytes again immediately before replacement.
            if ((File.GetAttributes(shortcut) & FileAttributes.ReparsePoint) != 0
                || !File.ReadAllBytes(shortcut).AsSpan().SequenceEqual(original))
                throw new IOException("The shortcut changed while its repair was being prepared.");
            var backupDirectory = Path.Combine(SettingsStore.DataRoot, "TaskbarShortcutBackups");
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(backupDirectory, $"SnipFlow-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.lnk");
            using (var file = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(original); file.Flush(flushToDisk: true); }
            if ((File.GetAttributes(shortcut) & FileAttributes.ReparsePoint) != 0
                || !File.ReadAllBytes(shortcut).AsSpan().SequenceEqual(original))
                throw new IOException("The shortcut changed while its backup was being saved.");
            // Same-directory atomic replacement is the final operation. No partial write
            // touches the original, and the original binary remains in the backup folder.
            File.Move(temporary, shortcut, overwrite: true);
            temporary = null;
        }
        finally
        {
            ReleaseObject(link);
            if (temporary != null)
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) { SettingsStore.Log(error); }
        }
    }

    sealed record LinkData(string Target, string Arguments, string Icon, int IconIndex, string WorkingDirectory, string? AppId);

    static IShellLink OpenLink(string path)
    {
        IShellLink? link = null;
        try
        {
            var classId = new Guid("00021401-0000-0000-C000-000000000046");
            var iid = typeof(IShellLink).GUID;
            Check(CoCreateInstance(in classId, IntPtr.Zero, 1, in iid, out var pointer), "Create the Shell link reader");
            link = Wrap<IShellLink>(pointer) ?? throw new COMException("The Shell link reader is unavailable.");
            Check(((IPersistLink)link).Load(path, 0), "Load the shortcut");
            return link;
        }
        catch { ReleaseObject(link); throw; }
    }

    static LinkData ReadLink(IShellLink link)
    {
        const int capacity = 32_768;
        var path = new char[capacity]; var arguments = new char[capacity];
        var icon = new char[capacity]; var workingDirectory = new char[capacity];
        int iconIndex;
        fixed (char* pathBuffer = path, argumentBuffer = arguments, iconBuffer = icon, directoryBuffer = workingDirectory)
        {
            Check(link.GetPath(pathBuffer, capacity, IntPtr.Zero, 4), "Read the shortcut target"); // SLGP_RAWPATH.
            Check(link.GetArguments(argumentBuffer, capacity), "Read the shortcut arguments");
            Check(link.GetIconLocation(iconBuffer, capacity, out iconIndex), "Read the shortcut icon");
            Check(link.GetWorkingDirectory(directoryBuffer, capacity), "Read the shortcut working directory");
            return new(new string(pathBuffer), new string(argumentBuffer), new string(iconBuffer), iconIndex, new string(directoryBuffer),
                ReadString((IPropertyStore)link, Keys[^1]));
        }
    }

    static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        var value = default(PropVariant);
        try
        {
            Check(store.GetValue(ref key, out value), "Read the Shell string property");
            return value.Type == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally { ClearVariant(ref value); }
    }

    static void SetString(IPropertyStore store, PropertyKey key, string text)
    {
        var value = default(PropVariant);
        try
        {
            // InitPropVariantFromString is an SDK inline helper, not a DLL export.
            // VT_LPWSTR owns CoTaskMem, which PropVariantClear releases below.
            value.Pointer = Marshal.StringToCoTaskMemUni(text); value.Type = 31;
            Check(store.SetValue(ref key, ref value), "Set the taskbar identity property");
        }
        finally { ClearVariant(ref value); }
    }

    static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException(operation, result);
    }

    static void ClearVariant(ref PropVariant value)
    {
        try { Check(PropVariantClear(ref value), "PropVariantClear"); }
        catch (Exception error) { SettingsStore.Log(error); }
    }

    /// <summary>Wraps an owned interface pointer; the wrapper takes its own reference.</summary>
    static T? Wrap<T>(IntPtr pointer) where T : class
    {
        if (pointer == IntPtr.Zero) return null;
        try { return (T)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(pointer); }
    }

    static void ReleaseObject(object? value)
    {
        if (value is not ComObject com) return;
        try { com.FinalRelease(); }
        catch (Exception error) { SettingsStore.Log(error); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PropertyKey(Guid format, uint id)
    {
        internal readonly Guid Format = format;
        internal readonly uint Id = id;
    }

    // The application targets x64. PROPVARIANT has an 8-byte header and a
    // 16-byte union (including the counted-array shape) on that architecture.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal IntPtr Pointer;
    }

    [GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214F9-0000-0000-C000-000000000046")]
    internal unsafe partial interface IShellLink
    {
        [PreserveSig] int GetPath(char* path, int capacity, IntPtr findData, uint flags);
        [PreserveSig] int GetIdList(out IntPtr list);
        [PreserveSig] int SetIdList(IntPtr list);
        [PreserveSig] int GetDescription(char* description, int capacity);
        [PreserveSig] int SetDescription(string description);
        [PreserveSig] int GetWorkingDirectory(char* path, int capacity);
        [PreserveSig] int SetWorkingDirectory(string path);
        [PreserveSig] int GetArguments(char* arguments, int capacity);
        [PreserveSig] int SetArguments(string arguments);
        [PreserveSig] int GetHotkey(out ushort hotkey);
        [PreserveSig] int SetHotkey(ushort hotkey);
        [PreserveSig] int GetShowCommand(out int command);
        [PreserveSig] int SetShowCommand(int command);
        [PreserveSig] int GetIconLocation(char* path, int capacity, out int index);
        [PreserveSig] int SetIconLocation(string path, int index);
        [PreserveSig] int SetRelativePath(string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath(string path);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistLink
    {
        [PreserveSig] int GetClassId(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(string path, uint mode);
        [PreserveSig] int Save(string path, [MarshalAs(UnmanagedType.Bool)] bool remember);
        [PreserveSig] int SaveCompleted(string path);
        [PreserveSig] int GetCurrentFile(out IntPtr path);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(IntPtr window, in Guid iid, out IntPtr store);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, in Guid iid, out IntPtr store);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr window);
}

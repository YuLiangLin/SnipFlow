using System.ComponentModel;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SnipFlow.Services;

[SupportedOSPlatform("windows")]
public static partial class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "SnipFlow";
    const string ExecutableName = "SnipFlow.exe";

    static StartupRegistration ReadRegistrationCore()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = value == null ? RegistryValueKind.String : run!.GetValueKind(ValueName);
        return new(value, kind, IsBackgroundCommand(value as string));
    }

    static string CreateCommandCore(string launcher) => $"\"{launcher}\" --background";

    internal static string ResolveLauncher()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !string.Equals(Path.GetFileName(executable), ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(I18n.T("目前執行的是預覽程式，無法設定登入啟動。"));
        if (!File.Exists(executable)) throw MissingExecutable(executable);

        var directory = Path.GetDirectoryName(executable)!;
        if (string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase))
        {
            var root = Directory.GetParent(directory)?.FullName;
            var launcher = root == null ? null : Path.Combine(root, ExecutableName);
            if (launcher == null || !File.Exists(launcher)) throw MissingExecutable(launcher ?? executable);
            executable = launcher;
        }

        // Resolve the opened file rather than assuming a LocalAppData alias points
        // to the same location outside an MSIX host's filesystem context.
        executable = ResolveExistingFile(executable);
        if (!File.Exists(executable)) throw MissingExecutable(executable);
        return executable;
    }

    static void WriteCommandCore(string? command)
    {
        if (command == null)
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }

    static void RestoreCore(StartupRegistration previous)
    {
        if (previous.Value == null) WriteCommand(null);
        else
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(ValueName, previous.Value, previous.Kind is RegistryValueKind kind ? kind : RegistryValueKind.String);
        }
    }

    static bool IsBackgroundCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var text = command.Trim();
        string executable, arguments;
        if (text[0] == '"')
        {
            int quote = text.IndexOf('"', 1);
            if (quote < 2) return false;
            executable = text[1..quote]; arguments = text[(quote + 1)..].Trim();
        }
        else
        {
            int space = text.IndexOfAny(new[] { ' ', '\t' });
            if (space < 1) return false;
            executable = text[..space]; arguments = text[space..].Trim();
        }
        try
        {
            executable = Environment.ExpandEnvironmentVariables(executable);
            return arguments.Equals("--background", StringComparison.OrdinalIgnoreCase)
                && Path.IsPathFullyQualified(executable)
                && string.Equals(Path.GetFileName(executable), ExecutableName, StringComparison.OrdinalIgnoreCase)
                && File.Exists(executable);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    internal static string ResolveExistingFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(512);
        while (true)
        {
            uint length = GetFinalPathNameByHandle(file.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1)); continue;
            }
            var resolved = buffer.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
            else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
            return Path.GetFullPath(resolved);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}

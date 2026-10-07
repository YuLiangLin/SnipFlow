using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Velopack;

namespace SnipFlow.Services;

/// <summary>Converts a raw publish into an installed app after its editor has exited.</summary>
internal static class UpdateBootstrapper
{
    private const int RequestSchema = 1;
    private const int ParentExitTimeoutMilliseconds = 60_000;
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const string ExecutableName = "SnipFlow.exe";
    private const string InstallerName = "SnipFlow-win-Setup.exe";
    private const string InstanceMutexName = "Local\\SnipFlow.Desktop";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static string InstallRoot => NormalizePath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipFlow"));
    private static string UpdatesRoot => NormalizePath(Path.Combine(SettingsStore.DataRoot, "Updates"));
    private static string BootstrapRoot => Path.Combine(UpdatesRoot, "Bootstrap");
    private static string SessionsRoot => NormalizePath(Path.Combine(SettingsStore.DataRoot, "UpdateSessions"));

    internal static void ValidateInstallLocation()
    {
        var executable = CurrentExecutable();
        var installRoot = InstallRoot;
        var dataRoot = NormalizePath(SettingsStore.DataRoot);
        var captureDirectory = NormalizePath(SettingsStore.NormalizeCaptureSaveDirectory(SettingsStore.Current.CaptureSaveDirectory));
        if (IsWithin(executable, installRoot) || IsWithin(dataRoot, installRoot) || IsWithin(captureDirectory, installRoot))
            throw new InvalidOperationException("The app and its capture data must be outside the bootstrap installation directory.");
        RejectReparsePoints(installRoot);
        RejectReparsePoints(dataRoot);
        RejectReparsePoints(captureDirectory);
        RejectReparsePoints(executable);
    }

    internal static bool VerifyInstaller(string path, string sha256)
    {
        try
        {
            var expectedHash = ParseHash(sha256);
            using var stream = OpenInstaller(ValidateInstallerPath(path));
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expectedHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static void Start(string installerPath, string sha256, string[] restartArgs)
    {
        ValidateInstallLocation();
        var executable = CurrentExecutable();
        var validatedInstaller = ValidateInstallerPath(installerPath);
        ParseHash(sha256);
        var validatedArguments = ValidateRestartArguments(restartArgs);
        if (!VerifyInstaller(validatedInstaller, sha256))
            throw new InvalidDataException("The bootstrap installer failed SHA256 verification.");
        ValidateExistingInstallation(TargetVersion(validatedInstaller));
        CreateOwnedDirectory(UpdatesRoot);
        CreateOwnedDirectory(BootstrapRoot);
        using var parent = Process.GetCurrentProcess();
        var request = new BootstrapRequest
        {
            SchemaVersion = RequestSchema,
            InstallerPath = validatedInstaller,
            Sha256 = sha256,
            ParentProcessId = parent.Id,
            ParentStartTimeUtcTicks = parent.StartTime.ToUniversalTime().Ticks,
            OriginalExe = executable,
            RestartArgs = validatedArguments
        };
        var requestPath = Path.Combine(BootstrapRoot, Guid.NewGuid().ToString("N") + ".json");
        using (var stream = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, request, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        // Run this same executable before App.Main enters Velopack or the UI singleton.
        var start = HiddenStart(executable, Path.GetDirectoryName(executable)!);
        start.ArgumentList.Add("--apply-bootstrap");
        start.ArgumentList.Add(requestPath);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("The update helper could not start.");
    }

    internal static int Run(string requestPath)
    {
        BootstrapRequest? request = null;
        var parentExited = false;
        try
        {
            var validatedPath = ValidateRequestPath(requestPath);
            using var requestFile = new FileStream(validatedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            if (requestFile.Length is < 1 or > 65_536) throw InvalidRequest();
            request = JsonSerializer.Deserialize<BootstrapRequest>(requestFile, JsonOptions) ?? throw InvalidRequest();
            ValidateRequest(request);
            // A consumed request must never reinstall an old version or restore an old checkpoint.
            using (var claim = new FileStream(Path.ChangeExtension(validatedPath, ".used"), FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                claim.Flush(flushToDisk: true);
            parentExited = WaitForParentExit(request);
            if (!parentExited)
                throw new TimeoutException("The original SnipFlow process did not exit; the installer was not started.");
            ValidateInstallLocation();
            Install(request, Path.GetFileNameWithoutExtension(validatedPath));
            Launch(Path.Combine(InstallRoot, ExecutableName), request.RestartArgs);
            return 0;
        }
        catch (Exception ex)
        {
            SettingsStore.Log(new InvalidOperationException("SnipFlow bootstrap update failed.", ex));
            if (parentExited && request is not null)
            {
                try
                {
                    // The persisted editor checkpoint is also used when installation fails.
                    Launch(request.OriginalExe, request.RestartArgs.Concat(new[] { "--update-failed" }));
                }
                catch (Exception restartError) { SettingsStore.Log(restartError); }
            }
            return 1;
        }
    }

    private static void Install(BootstrapRequest request, string requestId)
    {
        using var mutex = new Mutex(true, InstanceMutexName, out var ownsMutex);
        try
        {
            if (!ownsMutex)
            {
                try { ownsMutex = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
            }
            if (!ownsMutex) throw new InvalidOperationException("Another SnipFlow process is still running; installation was not started.");
            EnsureNoInstalledProcesses();
            var installerPath = ValidateInstallerPath(request.InstallerPath);
            var version = TargetVersion(installerPath);
            ValidateExistingInstallation(version);
            // Keep the verified file open without write/delete sharing until Setup exits.
            using var installerFile = OpenInstaller(installerPath);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(installerFile), ParseHash(request.Sha256)))
                throw new InvalidDataException("The bootstrap installer changed after it was downloaded.");
            var logPath = Path.Combine(UpdatesRoot, "bootstrap-" + requestId + ".log");
            RejectReparsePoints(logPath);
            if (File.Exists(logPath) || Directory.Exists(logPath)) throw InvalidRequest();
            var start = HiddenStart(installerPath, UpdatesRoot);
            start.ArgumentList.Add("--silent");
            start.ArgumentList.Add("--installto");
            start.ArgumentList.Add(InstallRoot);
            start.ArgumentList.Add("--log");
            start.ArgumentList.Add(logPath);
            using var setup = Process.Start(start) ?? throw new InvalidOperationException("The bootstrap installer could not start.");
            setup.WaitForExit();
            if (setup.ExitCode != 0)
                throw new InvalidOperationException($"The bootstrap installer exited with code {setup.ExitCode}. See {logPath}.");
            if (ReadInstalledVersion() != version) throw new InvalidDataException("The installed version does not match the downloaded update.");
            ValidateInstalledFile(Path.Combine(InstallRoot, ExecutableName));
            ValidateInstalledFile(Path.Combine(InstallRoot, "current", ExecutableName));
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
        // Disposing the mutex before Launch lets the installed app acquire its singleton.
    }

    private static void ValidateRequest(BootstrapRequest request)
    {
        if (request.SchemaVersion != RequestSchema || request.ParentProcessId < 1
            || request.ParentProcessId == Environment.ProcessId
            || request.ParentStartTimeUtcTicks <= DateTime.MinValue.Ticks
            || request.ParentStartTimeUtcTicks > DateTime.UtcNow.AddMinutes(1).Ticks
            || !string.Equals(NormalizePath(request.OriginalExe), CurrentExecutable(), StringComparison.OrdinalIgnoreCase))
            throw InvalidRequest();
        ValidateInstallerPath(request.InstallerPath);
        ParseHash(request.Sha256);
        ValidateRestartArguments(request.RestartArgs);
    }

    private static bool WaitForParentExit(BootstrapRequest request)
    {
        Process parent;
        try { parent = Process.GetProcessById(request.ParentProcessId); }
        catch (ArgumentException) { return true; }
        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTimeUtcTicks) return true;
                if (!string.Equals(NormalizePath(parent.MainModule?.FileName ?? ""), request.OriginalExe, StringComparison.OrdinalIgnoreCase))
                    throw InvalidRequest();
                return parent.WaitForExit(ParentExitTimeoutMilliseconds);
            }
            catch (InvalidOperationException) when (parent.HasExited) { return true; }
            catch (Win32Exception) when (parent.HasExited) { return true; }
        }
    }

    private static void EnsureNoInstalledProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var isSnipFlow = false;
                try
                {
                    isSnipFlow = string.Equals(process.ProcessName, "SnipFlow", StringComparison.OrdinalIgnoreCase);
                    if (process.HasExited) continue;
                    var image = process.MainModule?.FileName;
                    if (image is not null && IsWithin(NormalizePath(image), InstallRoot))
                        throw new InvalidOperationException($"Process {process.Id} is still using the installation directory; installation was not started.");
                    if (image is null && isSnipFlow)
                        throw new InvalidOperationException("A running SnipFlow process could not be identified safely.");
                }
                catch (Exception ex) when (ex is Win32Exception or NotSupportedException or ArgumentException)
                {
                    if (isSnipFlow && !process.HasExited)
                        throw new InvalidOperationException("A running SnipFlow process could not be identified safely.", ex);
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
    }

    private static void ValidateExistingInstallation(SemanticVersion targetVersion)
    {
        RejectReparsePoints(InstallRoot);
        if (File.Exists(InstallRoot)) throw new InvalidDataException("The installation directory is occupied by a file.");
        if (!Directory.Exists(InstallRoot) || !Directory.EnumerateFileSystemEntries(InstallRoot).Any()) return;
        var installedVersion = ReadInstalledVersion();
        if (installedVersion > targetVersion)
            throw new InvalidDataException("Bootstrap installation cannot downgrade the existing SnipFlow installation.");
    }

    private static SemanticVersion ReadInstalledVersion()
    {
        var manifestPath = Path.Combine(InstallRoot, "current", "sq.version");
        ValidateInstalledFile(manifestPath);
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 65_536) throw InvalidRequest();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 65_536
        });
        var metadata = XDocument.Load(reader).Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
        string? Value(string name) => metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
        if (Value("id") != "SnipFlow" || Value("mainExe") != ExecutableName
            || !SemanticVersion.TryParse(Value("version") ?? "", out var version) || version.IsPrerelease)
            throw new InvalidDataException("The installation directory does not contain a recognized SnipFlow installation.");
        return version;
    }

    private static void ValidateInstalledFile(string path)
    {
        RejectReparsePoints(path);
        if (!File.Exists(path)) throw new InvalidDataException("The SnipFlow installation is incomplete.");
    }

    private static string ValidateInstallerPath(string path)
    {
        var normalized = NormalizePath(path);
        var versionDirectory = Path.GetDirectoryName(normalized);
        if (!string.Equals(Path.GetFileName(normalized), InstallerName, StringComparison.Ordinal)
            || versionDirectory is null
            || !string.Equals(Path.GetDirectoryName(versionDirectory), UpdatesRoot, StringComparison.OrdinalIgnoreCase))
            throw InvalidRequest();
        TargetVersion(normalized);
        RejectReparsePoints(normalized);
        return normalized;
    }

    private static SemanticVersion TargetVersion(string installerPath)
    {
        var value = Path.GetFileName(Path.GetDirectoryName(installerPath));
        if (!SemanticVersion.TryParse(value ?? "", out var version) || version.IsPrerelease
            || !string.Equals(version.ToString().Split('+')[0], value, StringComparison.Ordinal))
            throw InvalidRequest();
        return version;
    }

    private static FileStream OpenInstaller(string path)
    {
        RejectReparsePoints(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is > 0 and <= MaximumInstallerBytes) return stream;
        stream.Dispose();
        throw new InvalidDataException("The bootstrap installer has an invalid size.");
    }

    private static byte[] ParseHash(string sha256)
    {
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw InvalidRequest();
        return Convert.FromHexString(sha256);
    }

    private static string ValidateRequestPath(string path)
    {
        var normalized = NormalizePath(path);
        if (!string.Equals(Path.GetDirectoryName(normalized), BootstrapRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(normalized), ".json", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(normalized), "N", out _))
            throw InvalidRequest();
        RejectReparsePoints(normalized);
        return normalized;
    }

    private static string[] ValidateRestartArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0) return Array.Empty<string>();
        if (arguments.Length != 2 || arguments[0] != "--restore-update-session") throw InvalidRequest();
        var directory = NormalizePath(arguments[1]);
        if (!string.Equals(Path.GetDirectoryName(directory), SessionsRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw InvalidRequest();
        RejectReparsePoints(directory);
        RejectReparsePoints(Path.Combine(directory, "manifest.json"));
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "manifest.json"))) throw InvalidRequest();
        return new[] { "--restore-update-session", directory };
    }

    private static string CurrentExecutable()
    {
        var path = NormalizePath(Environment.ProcessPath ?? throw new InvalidOperationException("The application executable cannot be located."));
        if (!string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("Bootstrap updates require the published SnipFlow.exe application.");
        return path;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Replace('/', '\\');
        // Use ordinary local drive paths only; reject device paths, ADS and normalization tricks.
        if (!Path.IsPathFullyQualified(value) || value.Length < 3 || !char.IsAsciiLetter(value[0])
            || value[1] != ':' || value[2] != '\\' || value.AsSpan(2).Contains(':')
            || value.Split('\\').Skip(1).Any(segment => segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
            throw InvalidRequest();
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(normalized, Path.TrimEndingDirectorySeparator(value), StringComparison.OrdinalIgnoreCase))
            throw InvalidRequest();
        return normalized;
    }

    private static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoints(string path)
    {
        var normalized = NormalizePath(path);
        var current = Path.GetPathRoot(normalized)!;
        foreach (var segment in normalized[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Prepend(""))
        {
            if (segment.Length > 0) current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Bootstrap paths cannot pass through a symbolic link or junction.");
        }
    }

    private static void CreateOwnedDirectory(string path)
    {
        RejectReparsePoints(path);
        Directory.CreateDirectory(path);
        RejectReparsePoints(path);
    }

    private static ProcessStartInfo HiddenStart(string executable, string workingDirectory) => new(executable)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };

    private static void Launch(string executable, IEnumerable<string> arguments)
    {
        RejectReparsePoints(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The application could not be restarted.", executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("SnipFlow could not restart.");
    }

    private static InvalidDataException InvalidRequest() => new("The SnipFlow bootstrap request or path is invalid.");

    private sealed class BootstrapRequest
    {
        public required int SchemaVersion { get; init; }
        public required string InstallerPath { get; init; }
        public required string Sha256 { get; init; }
        public required int ParentProcessId { get; init; }
        public required long ParentStartTimeUtcTicks { get; init; }
        public required string OriginalExe { get; init; }
        public required string[] RestartArgs { get; init; }
    }
}

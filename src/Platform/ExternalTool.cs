using System.Diagnostics;
using System.Text;

namespace SnipFlow.Platform;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Finds and runs command-line tools that SnipFlow uses on Linux (ffmpeg, ffprobe, tesseract,
/// gsettings, gst-launch-1.0, ...). Arguments are passed as a list, never through a shell.
/// </summary>
public static class ExternalTool
{
    static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>Full path of <paramref name="name"/> from the app's tools folder or PATH, or null.</summary>
    public static string? Find(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            var found = Search(name);
            Cache[name] = found;
            return found;
        }
    }

    public static bool IsAvailable(string name) => Find(name) is not null;

    static string? Search(string name)
    {
        var candidates = PlatformInfo.IsWindows && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { name + ".exe", name } : new[] { name };
        var directories = new List<string> { Path.Combine(AppContext.BaseDirectory, "tools") };
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var directory in directories)
            foreach (var candidate in candidates)
            {
                try
                {
                    var path = Path.Combine(directory, candidate);
                    if (File.Exists(path) && (PlatformInfo.IsWindows || IsExecutable(path))) return path;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
            }
        return null;
    }

    static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    /// <summary>Runs a tool to completion, capturing text output. Cancellation kills the process tree.</summary>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, byte[]? standardInput = null, TimeSpan? timeout = null)
    {
        var (exit, output, error) = await RunCoreAsync(fileName, arguments, cancellationToken, standardInput, timeout, binaryOutput: false);
        return new ProcessResult(exit, Encoding.UTF8.GetString(output), error);
    }

    /// <summary>Runs a tool and returns its raw standard output bytes (for image or video frames).</summary>
    public static async Task<(int ExitCode, byte[] Output, string StandardError)> RunBinaryAsync(string fileName,
        IEnumerable<string> arguments, CancellationToken cancellationToken = default, byte[]? standardInput = null, TimeSpan? timeout = null)
        => await RunCoreAsync(fileName, arguments, cancellationToken, standardInput, timeout, binaryOutput: true);

    /// <summary>Creates a start description with UTF-8 redirected streams and no window.</summary>
    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments,
        bool redirectInput = false, bool redirectOutput = true, bool redirectError = true)
    {
        var info = new ProcessStartInfo(Find(fileName) ?? fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectError,
        };
        if (redirectOutput) info.StandardOutputEncoding = Encoding.UTF8;
        if (redirectError) info.StandardErrorEncoding = Encoding.UTF8;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    static async Task<(int, byte[], string)> RunCoreAsync(string fileName, IEnumerable<string> arguments,
        CancellationToken cancellationToken, byte[]? standardInput, TimeSpan? timeout, bool binaryOutput)
    {
        var path = Find(fileName) ?? throw new FileNotFoundException(I18n.F("找不到 {0}，請先安裝。", fileName), fileName);
        var info = CreateStartInfo(path, arguments, redirectInput: standardInput is not null);
        if (binaryOutput) info.StandardOutputEncoding = null;
        using var process = new Process { StartInfo = info };
        using var timeoutSource = timeout is { } limit ? new CancellationTokenSource(limit) : null;
        using var linked = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        process.Start();
        using var registration = linked.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var outputTask = ReadAllAsync(process.StandardOutput.BaseStream);
        var errorTask = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(standardInput, linked.Token);
                process.StandardInput.Close();
            }
            catch (IOException) { }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        var output = await outputTask;
        var error = await errorTask;
        if (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            throw new TimeoutException(I18n.F("{0} 沒有在時間內完成。", Path.GetFileName(path)));
        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, output, error);
    }

    static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}

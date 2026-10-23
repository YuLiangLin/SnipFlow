using System.Diagnostics;

namespace SnipFlow.Platform.Linux.Recording;

/// <summary>
/// The PulseAudio sources a recording reads (PipeWire answers through pipewire-pulse on Ubuntu 24.04):
/// system audio is the monitor of the default sink, the microphone is the default source.
/// </summary>
/// <param name="SystemSource">The monitor source of the default output (for example <c>alsa_output….monitor</c>), or null.</param>
/// <param name="MicrophoneSource">The default input source, or null.</param>
/// <param name="Notice">A translated note when sound was requested but the sound server is not reachable.</param>
internal sealed record PulseAudioInputs(string? SystemSource, string? MicrophoneSource, string? Notice)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    internal bool HasAudio => SystemSource is not null || MicrophoneSource is not null;

    /// <summary>
    /// Finds the requested sources. A missing <c>pactl</c> or missing devices throw an
    /// <see cref="InvalidOperationException"/> with an actionable message; an unreachable sound server
    /// records video only and reports <see cref="Notice"/>.
    /// </summary>
    internal static async Task<PulseAudioInputs> ResolveAsync(bool systemAudio, bool microphone,
        Func<string, string> translate, CancellationToken cancellationToken)
    {
        if (!systemAudio && !microphone)
            return new PulseAudioInputs(null, null, null);
        if (ExternalTool.Find("pactl") is null)
            throw new InvalidOperationException(translate("錄製聲音需要 pactl。請安裝：sudo apt install pulseaudio-utils，或關閉系統聲音與麥克風。"));

        var info = await RunAsync(cancellationToken, "info").ConfigureAwait(false);
        if (info is not { ExitCode: 0 })
            return new PulseAudioInputs(null, null,
                translate("找不到 PulseAudio 或 PipeWire 音訊服務，這次錄影不含聲音。"));

        var sources = await ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        string? system = null;
        string? input = null;
        if (systemAudio)
        {
            var sink = await DefaultDeviceAsync("get-default-sink", "Default Sink:", info.Output, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(sink) || !sources.Contains(sink + ".monitor"))
                throw new InvalidOperationException(translate("找不到系統聲音輸出裝置。請連接喇叭或耳機，或關閉系統聲音。"));
            system = sink + ".monitor";
        }
        if (microphone)
        {
            var source = await DefaultDeviceAsync("get-default-source", "Default Source:", info.Output, cancellationToken).ConfigureAwait(false);
            // Without a microphone the sound server falls back to a monitor of an output; that is not a microphone.
            if (string.IsNullOrEmpty(source) || source.EndsWith(".monitor", StringComparison.Ordinal) || !sources.Contains(source))
                throw new InvalidOperationException(translate("找不到麥克風。請連接麥克風並允許桌面應用程式存取，或關閉麥克風。"));
            input = source;
        }
        return new PulseAudioInputs(system, input, null);
    }

    /// <summary><c>pactl get-default-sink/-source</c> (pactl 15+), or the line in <c>pactl info</c> on older versions.</summary>
    private static async Task<string?> DefaultDeviceAsync(string command, string infoLabel, string info,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(cancellationToken, command).ConfigureAwait(false);
        var name = result is { ExitCode: 0 } ? result.Output.Trim() : null;
        if (!string.IsNullOrEmpty(name) && !name.StartsWith('@'))
            return name;
        foreach (var line in info.Split('\n'))
            if (line.StartsWith(infoLabel, StringComparison.Ordinal))
                return line[infoLabel.Length..].Trim();
        return null;
    }

    private static async Task<HashSet<string>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = await RunAsync(cancellationToken, "list", "short", "sources").ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
            return names;
        // index <TAB> name <TAB> driver <TAB> sample spec <TAB> state
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 2 && fields[1].Length > 0)
                names.Add(fields[1].Trim());
        }
        return names;
    }

    private sealed record CommandResult(int ExitCode, string Output);

    /// <summary>Runs pactl with the C locale so labels such as "Default Sink:" are not translated.</summary>
    private static async Task<CommandResult?> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var info = ExternalTool.CreateStartInfo("pactl", arguments, redirectInput: false, redirectOutput: true, redirectError: true);
        info.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return new CommandResult(process.ExitCode, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A sound server that does not answer within the timeout is treated as unreachable.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}

using SnipFlow.Capture;

namespace SnipFlow;

internal enum ShellCommandKind { Show, Capture, Scroll, Record, Open }

/// <summary>One request for the running SnipFlow, from its own command line or forwarded by another launch.</summary>
internal sealed record ShellCommand(ShellCommandKind Kind, CaptureMode? Mode = null, string? Path = null)
{
    /// <summary>The single-line pipe message: show, capture [region|window|monitor|all], scroll, record, open &lt;path&gt;.</summary>
    public string ToMessage() => Kind switch
    {
        ShellCommandKind.Capture => Mode is { } mode ? "capture " + ShellCommandLine.ModeName(mode) : "capture",
        ShellCommandKind.Scroll => "scroll",
        ShellCommandKind.Record => "record",
        ShellCommandKind.Open => "open " + Path,
        _ => "show",
    };

    public static bool TryParse(string? line, out ShellCommand command)
    {
        command = new(ShellCommandKind.Show);
        var text = line?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 33_000 || text.Any(char.IsControl)) return false;
        var space = text.IndexOf(' ');
        var verb = space < 0 ? text : text[..space];
        var argument = space < 0 ? "" : text[(space + 1)..].Trim();
        switch (verb)
        {
            case "show" when argument.Length == 0: return true;
            case "scroll" when argument.Length == 0: command = new(ShellCommandKind.Scroll); return true;
            case "record" when argument.Length == 0: command = new(ShellCommandKind.Record); return true;
            case "capture" when argument.Length == 0: command = new(ShellCommandKind.Capture); return true;
            case "capture" when ShellCommandLine.TryParseMode(argument, out var mode): command = new(ShellCommandKind.Capture, mode); return true;
            // Only absolute local paths are accepted; the sender resolves relative paths against its own directory.
            case "open" when argument.Length > 0 && System.IO.Path.IsPathFullyQualified(argument):
                command = new(ShellCommandKind.Open, Path: argument); return true;
            default: return false;
        }
    }
}

/// <summary>
/// SnipFlow's command line. Public options: <c>--background</c>, <c>--open &lt;file&gt;</c>, bare file paths
/// (the desktop entry's <c>%F</c>), <c>--capture [region|window|monitor|all]</c>, <c>--scroll</c> and <c>--record</c>.
/// <c>--restore-update-session &lt;dir&gt;</c> and <c>--update-failed</c> are used by the updater only.
/// </summary>
internal sealed class ShellCommandLine
{
    public bool Background { get; private init; }
    public bool UpdateFailed { get; private init; }
    public string? RestoreSession { get; private init; }
    public IReadOnlyList<ShellCommand> Commands { get; private init; } = Array.Empty<ShellCommand>();

    public static ShellCommandLine Parse(IReadOnlyList<string> args)
    {
        var commands = new List<ShellCommand>();
        bool background = false, updateFailed = false;
        string? restore = null;
        for (int index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--background": background = true; break;
                case "--update-failed": updateFailed = true; break;
                case "--restore-update-session" when index + 1 < args.Count: restore = args[++index]; break;
                case "--open" when index + 1 < args.Count:
                    if (ResolvePath(args[++index]) is { } opened) commands.Add(new(ShellCommandKind.Open, Path: opened));
                    break;
                case "--capture":
                    CaptureMode? mode = null;
                    if (index + 1 < args.Count && TryParseMode(args[index + 1], out var parsed)) { mode = parsed; index++; }
                    commands.Add(new(ShellCommandKind.Capture, mode));
                    break;
                case "--scroll": commands.Add(new(ShellCommandKind.Scroll)); break;
                case "--record": commands.Add(new(ShellCommandKind.Record)); break;
                default:
                    // Unknown options are ignored; anything else is a file to open, as from a file manager.
                    if (!argument.StartsWith('-') && ResolvePath(argument) is { } path) commands.Add(new(ShellCommandKind.Open, Path: path));
                    break;
            }
        }
        return new() { Background = background, UpdateFailed = updateFailed, RestoreSession = restore, Commands = commands };
    }

    /// <summary>The messages a second launch forwards to the running instance.</summary>
    public IReadOnlyList<string> ForwardedMessages()
    {
        if (Commands.Count > 0) return Commands.Select(command => command.ToMessage()).ToArray();
        // A login-start copy started while SnipFlow already runs has nothing to do.
        return Background ? Array.Empty<string>() : new[] { "show" };
    }

    internal static bool TryParseMode(string? text, out CaptureMode mode)
    {
        mode = CaptureMode.Region;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "region": mode = CaptureMode.Region; return true;
            case "window": mode = CaptureMode.Window; return true;
            case "monitor" or "screen": mode = CaptureMode.Monitor; return true;
            case "all" or "allmonitors": mode = CaptureMode.AllMonitors; return true;
            default: return false;
        }
    }

    internal static string ModeName(CaptureMode mode) => mode switch
    {
        CaptureMode.Window => "window",
        CaptureMode.Monitor => "monitor",
        CaptureMode.AllMonitors => "all",
        _ => "region",
    };

    static string? ResolvePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            // Desktop entries may pass file:// URIs (%U); file managers pass plain paths (%F).
            if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
                value = uri.LocalPath;
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

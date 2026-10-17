namespace SnipFlow.Services;

/// <summary>A snapshot of the current login-start entry. <see cref="Value"/>/<see cref="Kind"/> are platform data for <see cref="StartupService.Restore"/>.</summary>
public sealed record StartupRegistration(object? Value, object? Kind, bool IsEnabled);

/// <summary>
/// Start SnipFlow at login with <c>--background</c>. Windows uses the per-user Run registry value;
/// Linux uses an XDG autostart entry (<c>~/.config/autostart/snipflow.desktop</c>).
/// </summary>
public static partial class StartupService
{
    public static StartupRegistration ReadRegistration() => ReadRegistrationCore();

    /// <summary>The command written to the login entry.</summary>
    public static string CreateCommand() => CreateCommandCore(ResolveLauncher());

    /// <summary>Writes the command, or removes SnipFlow's entry when <paramref name="command"/> is null.</summary>
    public static void WriteCommand(string? command) => WriteCommandCore(command);

    /// <summary>Restores a snapshot from <see cref="ReadRegistration"/> after a failed change.</summary>
    public static void Restore(StartupRegistration previous) => RestoreCore(previous);

    static FileNotFoundException MissingExecutable(string path) => new(I18n.T("找不到可啟動的 SnipFlow 程式，登入啟動未變更。"), path);
}

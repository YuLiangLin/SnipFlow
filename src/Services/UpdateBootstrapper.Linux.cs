namespace SnipFlow.Services;

/// <summary>
/// Windows only: converts a raw publish into an installed app. Linux updates come from the AppImage's
/// Velopack feed or from the package manager, so there is nothing to bootstrap.
/// </summary>
internal static class UpdateBootstrapper
{
    internal static void ValidateInstallLocation() => throw new PlatformNotSupportedException();

    internal static bool VerifyInstaller(string path, string sha256) => false;

    internal static void Start(string installerPath, string sha256, string[] restartArgs) => throw new PlatformNotSupportedException();

    internal static int Run(string requestPath) => 1;
}

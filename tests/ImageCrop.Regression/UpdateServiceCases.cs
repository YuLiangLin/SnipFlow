using System.Net;
using System.Text.Json;
using SnipFlow.Services;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static void UpdateServiceCases()
    {
        Case("the Windows bootstrap path treats an equal public release as up to date", () =>
        {
            var current = new UpdateService().CurrentVersion;
            var updater = CheckBootstrapRelease(current);
            Check(updater.LatestVersion == current, "The updater did not read the equal public version.");
            Check(updater.State == UpdateState.UpToDate && !updater.CanDownload && !updater.ReadyToRestart,
                "The executable is already at this version, but the updater still offered the same installer.");
        });

        Case("the Windows bootstrap path still offers a newer public release", () =>
        {
            var current = Version.Parse(new UpdateService().CurrentVersion);
            var next = new Version(current.Major, current.Minor, current.Build + 1).ToString();
            var updater = CheckBootstrapRelease(next);
            Check(updater.LatestVersion == next && updater.State == UpdateState.Available && updater.CanDownload,
                "A genuinely newer stable installer was not offered.");
            Check(!updater.ReadyToRestart, "A check without downloading marked the installer ready.");
        });
    }

    static UpdateService CheckBootstrapRelease(string version)
    {
        using var client = new HttpClient(new StableReleaseResponse(version));
        var updater = (UpdateService)Activator.CreateInstance(typeof(UpdateService), Any,
            binder: null, args: [client, true], culture: null)!;
        Check(!updater.IsInstalled && !updater.IsManagedExternally, "Expected the Windows bootstrap path.");
        Task.Run(() => updater.CheckAsync(download: false)).GetAwaiter().GetResult();
        Check(updater.ErrorMessage is null, updater.ErrorMessage ?? "Unexpected error.");
        return updater;
    }

    sealed class StableReleaseResponse(string version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get
                && request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/YuLiangLin/SnipFlow/releases/latest",
                "Unexpected request to fetch the public release.");
            var body = JsonSerializer.Serialize(new
            {
                tag_name = "v" + version,
                draft = false,
                prerelease = false,
                assets = new[]
                {
                    new
                    {
                        name = "SnipFlow-win-Setup.exe",
                        browser_download_url = $"https://github.com/YuLiangLin/SnipFlow/releases/download/v{version}/SnipFlow-win-Setup.exe",
                        size = 1_024L,
                        digest = "sha256:" + new string('a', 64),
                    },
                },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

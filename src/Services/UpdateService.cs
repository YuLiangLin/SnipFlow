using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
namespace SnipFlow.Services;

public enum UpdateState { Idle, Checking, Available, Downloading, ReadyToRestart, UpToDate, Error }

public sealed class UpdateService
{
    const string DefaultRepositoryUrl = "https://github.com/YuLiangLin/SnipFlow";
    const string DefaultChannel = "win";
    const string LatestReleaseApi = "https://api.github.com/repos/YuLiangLin/SnipFlow/releases/latest";
    static readonly HttpClient releaseClient = CreateReleaseClient();
    readonly SemaphoreSlim gate = new(1, 1);
    readonly UpdateManager? manager;
    readonly SemanticVersion currentVersion;
    UpdateInfo? available;
    sealed record StatusMessage(string Key, object?[] Values);
    sealed record LatestRelease(SemanticVersion Version, string Tag, string? InstallerUrl);
    StatusMessage status = new("尚未檢查更新", Array.Empty<object?>());
    StatusMessage? error;

    public event EventHandler? Changed;
    public string Status { get { var current = status; return I18n.F(current.Key, current.Values); } }
    public string? ErrorMessage { get { var current = error; return current == null ? null : I18n.F(current.Key, current.Values); } }
    public UpdateState State { get; private set; }
    public bool IsBusy { get; private set; }
    public int? DownloadProgress { get; private set; }
    public bool IsInstalled { get; }
    public bool ReadyToRestart { get; private set; }
    public string RepositoryUrl { get; }
    public string CurrentVersion => currentVersion.ToString().Split('+')[0];
    public string? LatestVersion { get; private set; }
    public string ReleaseUrl { get; private set; } = DefaultRepositoryUrl + "/releases/latest";
    public string? InstallerUrl { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public bool CanDownload => !IsBusy && IsInstalled && available != null && !ReadyToRestart;
    public bool CanOpenInstaller => !IsBusy && InstallerUrl != null;

    public UpdateService()
    {
        RepositoryUrl = ReadRepositoryUrl();
        currentVersion = ReadCurrentVersion();
        // Preview hosts do not run App.Main, so they have no Velopack locator.
        // They can still check public releases without pretending to be installed.
        if (VelopackLocator.IsCurrentSet)
        {
            try
            {
                manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false),
                    new UpdateOptions { ExplicitChannel = DefaultChannel });
                IsInstalled = manager.IsInstalled && !manager.IsPortable;
                if (IsInstalled && manager.CurrentVersion is { } installedVersion) currentVersion = installedVersion;
                if (IsInstalled && manager.UpdatePendingRestart is { } pending && pending.Version > currentVersion)
                {
                    LatestVersion = pending.Version.ToString();
                    ReadyToRestart = true;
                    State = UpdateState.ReadyToRestart;
                    SetStatus("更新已下載，可重新啟動套用");
                }
            }
            catch (Exception ex)
            {
                manager = null;
                IsInstalled = false;
                SettingsStore.Log(ex);
                SetStatus("自動更新暫不可用，可下載安裝程式");
            }
        }
    }

    public async Task CheckAsync(bool download)
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (ReadyToRestart) { State = UpdateState.ReadyToRestart; SetStatus("更新已下載，可重新啟動套用"); return; }
            IsBusy = true; error = null; DownloadProgress = null;
            State = UpdateState.Checking;
            SetStatus("正在檢查 GitHub Releases…");
            var release = await ReadLatestReleaseAsync();
            LastCheckedAt = DateTimeOffset.Now;
            LatestVersion = release.Version.ToString();
            ReleaseUrl = RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(release.Tag);
            InstallerUrl = release.InstallerUrl;
            available = null;
            if (release.Version <= currentVersion)
            {
                State = UpdateState.UpToDate;
                SetStatus("目前已是最新版本");
                return;
            }
            if (!IsInstalled)
            {
                State = UpdateState.Available;
                SetStatus("發現新版本 {0}，請下載安裝程式", LatestVersion);
                return;
            }
            var candidate = await manager!.CheckForUpdatesAsync();
            // A published release can exist before its update feed finishes uploading.
            // Never silently install an older feed entry than the displayed release.
            if (candidate == null || candidate.TargetFullRelease.Version != release.Version)
            {
                State = UpdateState.Available;
                SetStatus("新版本 {0} 的更新套件尚未就緒，可下載安裝程式", LatestVersion);
                return;
            }
            available = candidate;
            State = UpdateState.Available;
            SetStatus("發現新版本 {0}", LatestVersion);
            if (download) await DownloadCoreAsync();
        }
        catch (Exception ex)
        {
            SetError(ex, State == UpdateState.Downloading ? "更新下載失敗 · 目前版本仍可使用" : "更新檢查失敗 · 請稍後重試");
        }
        finally { IsBusy = false; gate.Release(); NotifyChanged(); }
    }

    public async Task DownloadAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (!IsInstalled || available == null || ReadyToRestart) return;
            IsBusy = true; error = null;
            await DownloadCoreAsync();
        }
        catch (Exception ex) { SetError(ex, "更新下載失敗 · 目前版本仍可使用"); }
        finally { IsBusy = false; gate.Release(); NotifyChanged(); }
    }

    async Task DownloadCoreAsync()
    {
        State = UpdateState.Downloading; DownloadProgress = 0;
        SetStatus("正在下載 {0}…", LatestVersion);
        await manager!.DownloadUpdatesAsync(available!, progress =>
        {
            DownloadProgress = Math.Clamp(progress, 0, 100);
            SetStatus("正在下載更新 · {0}%", DownloadProgress);
        });
        DownloadProgress = 100; ReadyToRestart = true; State = UpdateState.ReadyToRestart;
        SetStatus("{0} 已下載 · 重新啟動以更新", LatestVersion);
    }

    public void OpenInstallerPage()
    {
        if (!CanOpenInstaller) return;
        try { Process.Start(new ProcessStartInfo(InstallerUrl!) { UseShellExecute = true }); }
        catch (Exception ex) { SetError(ex, "無法開啟下載連結，請稍後重試。"); }
    }

    public void ApplyAndRestart()
    {
        if (!IsInstalled || IsBusy || !ReadyToRestart) return;
        try
        {
            if (available != null) manager!.ApplyUpdatesAndRestart(available);
            else if (manager!.UpdatePendingRestart is { } pending) manager.ApplyUpdatesAndRestart(pending);
        }
        catch (Exception ex) { SetError(ex, "更新尚未套用"); throw; }
    }

    static string ReadRepositoryUrl()
    {
        // This official build always uses its embedded, trusted source. An absent,
        // malformed or edited adjacent JSON file cannot disable or redirect updates.
        try
        {
            using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("SnipFlow.update-source.json");
            if (stream == null) return DefaultRepositoryUrl;
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.GetProperty("RepositoryUrl").GetString() != DefaultRepositoryUrl ||
                root.GetProperty("Channel").GetString() != DefaultChannel)
                throw new InvalidDataException("The embedded update source does not match the official SnipFlow repository/channel.");
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
        return DefaultRepositoryUrl;
    }

    static SemanticVersion ReadCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(informationalVersion ?? "", out var parsed)) return parsed;
        return SemanticVersion.Parse(assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    static HttpClient CreateReleaseClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 1_048_576 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipFlow-Desktop/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    static async Task<LatestRelease> ReadLatestReleaseAsync()
    {
        using var response = await releaseClient.GetAsync(LatestReleaseApi);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
            !SemanticVersion.TryParse(tag.TrimStart('v', 'V'), out var version) || version.IsPrerelease)
            throw new InvalidDataException("GitHub returned an invalid stable SnipFlow release.");
        string? installerUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != "SnipFlow-win-Setup.exe") continue;
            var value = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                url.Host != "github.com" || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo) ||
                !url.AbsolutePath.StartsWith("/YuLiangLin/SnipFlow/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer URL is outside the official SnipFlow releases.");
            installerUrl = url.AbsoluteUri;
            break;
        }
        return new(version, tag, installerUrl);
    }

    void SetError(Exception ex, string fallbackKey)
    {
        SettingsStore.Log(ex);
        var key = ex switch
        {
            TaskCanceledException or TimeoutException => "連線逾時，請稍後重試。",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "GitHub 暫時限制更新檢查，請稍後重試。",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "尚未找到已發布版本，請稍後再試。",
            HttpRequestException => "無法連線至 GitHub，請檢查網路後重試。",
            JsonException or InvalidDataException => "更新資訊暫時無法讀取，請稍後重試。",
            _ => fallbackKey
        };
        State = UpdateState.Error; error = new(key, Array.Empty<object?>());
        SetStatus(key);
    }

    void SetStatus(string key, params object?[] values) { status = new(key, values); NotifyChanged(); }
    void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

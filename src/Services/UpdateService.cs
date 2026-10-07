using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
    readonly bool canBootstrap = string.Equals(Path.GetFileName(Environment.ProcessPath), "SnipFlow.exe", StringComparison.OrdinalIgnoreCase);
    UpdateInfo? available;
    LatestRelease? latestRelease;
    string? bootstrapInstallerPath;
    sealed record StatusMessage(string Key, object?[] Values);
    sealed record InstallerAsset(string Url, long Size, string Sha256);
    sealed record LatestRelease(SemanticVersion Version, string Tag, InstallerAsset? Installer);
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
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public bool CanDownload => !IsBusy && !ReadyToRestart && (IsInstalled ? available != null
        : canBootstrap && latestRelease?.Installer != null && latestRelease.Version >= currentVersion);
    public bool CanUpdateNow => !IsBusy && (ReadyToRestart || CanDownload);

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
                // Proper Velopack portable builds have their own updater and support in-place updates.
                IsInstalled = manager.IsInstalled;
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
                SetStatus("尚未檢查更新");
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
            latestRelease = release;
            LastCheckedAt = DateTimeOffset.Now;
            LatestVersion = release.Version.ToString();
            ReleaseUrl = RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(release.Tag);
            available = null;
            if (release.Version < currentVersion || release.Version == currentVersion && IsInstalled)
            {
                State = UpdateState.UpToDate;
                SetStatus("目前已是最新版本");
                return;
            }
            if (!IsInstalled)
            {
                if (!canBootstrap) { State = UpdateState.Available; SetStatus("此預覽程式無法套用更新。"); return; }
                if (release.Installer == null) throw new InvalidDataException("The release has no verified installer for automatic setup.");
                State = UpdateState.Available;
                SetStatus(release.Version > currentVersion ? "發現新版本 {0}" : "更新已可套用", LatestVersion);
                if (download) await DownloadCoreAsync();
                return;
            }
            var candidate = await manager!.CheckForUpdatesAsync();
            // A published release can exist before its update feed finishes uploading.
            // Never silently install an older feed entry than the displayed release.
            if (candidate == null || candidate.TargetFullRelease.Version != release.Version)
            {
                State = UpdateState.Available;
                SetStatus("更新套件尚未就緒，請稍後重試。");
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
            if (!CanDownload) return;
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
        void Progress(int progress)
        {
            DownloadProgress = Math.Clamp(progress, 0, 100);
            SetStatus("正在下載更新 · {0}%", DownloadProgress);
        }
        if (IsInstalled) await manager!.DownloadUpdatesAsync(available!, Progress);
        else await DownloadBootstrapAsync(Progress);
        DownloadProgress = 100; ReadyToRestart = true; State = UpdateState.ReadyToRestart;
        SetStatus("{0} 已下載 · 重新啟動以更新", LatestVersion);
    }

    async Task DownloadBootstrapAsync(Action<int> progress)
    {
        var release = latestRelease ?? throw new InvalidOperationException("No release is available.");
        var asset = release.Installer ?? throw new InvalidDataException("No verified installer is available.");
        UpdateBootstrapper.ValidateInstallLocation();
        var directory = Path.Combine(SettingsStore.DataRoot, "Updates", release.Version.ToString().Split('+')[0]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "SnipFlow-win-Setup.exe");
        if (File.Exists(path) && new FileInfo(path).Length == asset.Size && await Task.Run(() => UpdateBootstrapper.VerifyInstaller(path, asset.Sha256)))
        { bootstrapInstallerPath = path; progress(100); return; }
        var temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipFlow-Desktop/1.0");
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri == null || finalUri.Scheme != Uri.UriSchemeHttps || !finalUri.IsDefaultPort || !string.IsNullOrEmpty(finalUri.UserInfo)
                || !(finalUri.Host == "github.com" || finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The update download was redirected outside GitHub.");
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("The update download has an unexpected length.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("The update exceeded its declared size.");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    var percentage = (int)(received * 100 / asset.Size);
                    if (percentage != DownloadProgress) progress(percentage);
                }
                await output.FlushAsync(timeout.Token);
            }
            if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update did not match the release SHA-256.");
            File.Move(temporaryPath, path, true);
            bootstrapInstallerPath = path;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        if (IsBusy || !ReadyToRestart) return;
        restartArgs ??= Array.Empty<string>();
        try
        {
            if (IsInstalled)
                manager!.WaitExitThenApplyUpdates(available?.TargetFullRelease ?? manager.UpdatePendingRestart,
                    silent: true, restart: true, restartArgs: restartArgs);
            else
            {
                var asset = latestRelease?.Installer ?? throw new InvalidOperationException("No verified update is ready.");
                UpdateBootstrapper.Start(bootstrapInstallerPath ?? throw new InvalidOperationException("The update has not been downloaded."),
                    asset.Sha256, restartArgs);
            }
            Application.Current?.Shutdown();
        }
        catch (Exception ex) { SetError(ex, "更新尚未套用"); throw; }
    }

    internal void ReportRestartFailure()
    {
        State = UpdateState.Error;
        error = new("更新未完成，已保留目前版本與編輯。", Array.Empty<object?>());
        SetStatus(error.Key);
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
        InstallerAsset? installer = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != "SnipFlow-win-Setup.exe") continue;
            var value = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                url.Host != "github.com" || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo) ||
                !url.AbsolutePath.StartsWith("/YuLiangLin/SnipFlow/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer URL is outside the official SnipFlow releases.");
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? "" : "";
            if (size <= 0 || size > 512L * 1024 * 1024 || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                || digest.Length != 71 || digest[7..].Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("The release installer is missing valid size/SHA-256 metadata.");
            installer = new(url.AbsoluteUri, size, digest[7..]);
            break;
        }
        return new(version, tag, installer);
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

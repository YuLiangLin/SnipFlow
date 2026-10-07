using System.Text.Json;
using Velopack;
using Velopack.Sources;
namespace SnipFlow.Services;

public sealed class UpdateService
{
    UpdateManager? manager;
    UpdateInfo? available;
    readonly SemaphoreSlim gate = new(1, 1);
    public event EventHandler? Changed;
    sealed record StatusMessage(string Key, object?[] Values);
    StatusMessage status = new("尚未檢查更新", Array.Empty<object?>());
    public string Status { get { var current = status; return I18n.F(current.Key, current.Values); } }
    public bool ReadyToRestart { get; private set; }
    public string RepositoryUrl { get; }
    public string? LatestVersion => available?.TargetFullRelease.Version.ToString();
    public UpdateService()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
            RepositoryUrl = json.RootElement.GetProperty("RepositoryUrl").GetString() ?? "";
            if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com" || url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2)
            { SetStatus("更新來源尚未設定"); return; }
            manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            if (!manager.IsInstalled) { SetStatus("可攜版 · 請安裝 Setup 以啟用自動更新"); return; }
            ReadyToRestart = manager.UpdatePendingRestart != null;
            if (ReadyToRestart) SetStatus("更新已下載，可重新啟動套用");
        }
        catch (Exception ex) { RepositoryUrl = ""; SetStatus("讀取更新設定失敗"); SettingsStore.Log(ex); }
    }
    public async Task CheckAsync(bool download)
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (manager == null) { SetStatus("更新來源尚未設定"); return; }
            if (!manager.IsInstalled) { SetStatus("可攜版 · 請安裝 Setup 以啟用自動更新"); return; }
            if (ReadyToRestart) { SetStatus("更新已下載，可重新啟動套用"); return; }
            SetStatus("正在檢查 GitHub Releases…");
            available = await manager.CheckForUpdatesAsync();
            if (available == null) { SetStatus("目前已是最新版本"); return; }
            SetStatus("發現新版本 {0}", LatestVersion);
            if (!download) return;
            await DownloadCoreAsync();
        }
        catch (Exception ex) { SettingsStore.Log(ex); SetStatus("更新檢查失敗 · 請稍後重試"); }
        finally { gate.Release(); }
    }
    public async Task DownloadAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try { if (available != null && manager?.IsInstalled == true) await DownloadCoreAsync(); }
        catch (Exception ex) { SettingsStore.Log(ex); SetStatus("更新下載失敗 · 目前版本仍可使用"); }
        finally { gate.Release(); }
    }
    async Task DownloadCoreAsync()
    {
        SetStatus("正在下載 {0}…", LatestVersion);
        await manager!.DownloadUpdatesAsync(available!, p => SetStatus("正在下載更新 · {0}%", p));
        ReadyToRestart = true; SetStatus("{0} 已下載 · 重新啟動以更新", LatestVersion);
    }
    public void ApplyAndRestart()
    {
        if (manager?.IsInstalled != true || !ReadyToRestart) return;
        if (available != null) manager.ApplyUpdatesAndRestart(available);
        else if (manager.UpdatePendingRestart is { } pending) manager.ApplyUpdatesAndRestart(pending);
    }
    void SetStatus(string key, params object?[] values) { status = new(key, values); Changed?.Invoke(this, EventArgs.Empty); }
}

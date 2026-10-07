using System.IO.Pipes;
using System.Windows.Threading;
using Velopack;
using SnipFlow.Services;
namespace SnipFlow;

public partial class App : Application
{
    static Mutex? instance;
    readonly CancellationTokenSource shutdown = new();
    System.Windows.Forms.NotifyIcon? tray;
    System.Drawing.Icon? trayIcon;
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(4) };
    MainWindow? shell;
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-bootstrap")
        {
            Environment.ExitCode = UpdateBootstrapper.Run(args[1]); return;
        }
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        instance = new Mutex(true, "Local\\SnipFlow.Desktop", out var first);
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "SnipFlow.Desktop", PipeDirection.Out);
                pipe.Connect(1500); using var writer = new StreamWriter(pipe); writer.WriteLine("show");
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            instance.Dispose(); return;
        }
        try
        {
            I18n.SetLanguage(SettingsStore.Current.Language);
            // Apply at a fresh start only. A second launch must keep the existing editor intact.
            try
            {
                var updates = new UpdateService();
                if (updates.ReadyToRestart && !args.Contains("--update-failed")) { updates.ApplyAndRestart(args); return; }
            }
            catch (Exception ex) { SettingsStore.Log(ex); }
            var app = new App(); app.InitializeComponent(); app.Run();
        }
        finally { instance.ReleaseMutex(); instance.Dispose(); }
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, error) =>
        {
            SettingsStore.Log(error.Exception); error.Handled = true;
            MessageBox.Show(I18n.T("這個操作未完成，詳細資訊已寫入本機紀錄。\n") + error.Exception.Message, "SnipFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        shell = new MainWindow(); MainWindow = shell;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(I18n.T("框選截圖"), null, (_, _) => Dispatcher.BeginInvoke(() => shell.StartCaptureAsync(false)));
        menu.Items.Add(I18n.T("螢幕錄影"), null, (_, _) => Dispatcher.BeginInvoke(() => shell.StartRecordingAsync()));
        menu.Items.Add(I18n.T("開啟 SnipFlow"), null, (_, _) => Dispatcher.BeginInvoke(ShowShell));
        menu.Items.Add(I18n.T("結束"), null, (_, _) => Dispatcher.BeginInvoke(ExitRequested));
        I18n.Changed += TrayLanguageChanged;
        trayIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "SnipFlow-v3.ico"), 32, 32);
        tray = new System.Windows.Forms.NotifyIcon { Icon = trayIcon, Text = "SnipFlow", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowShell);
        shell.Show();
        if (e.Args.Contains("--background")) shell.Hide();
        var fileIndex = Array.IndexOf(e.Args, "--open");
        if (fileIndex >= 0 && e.Args.Length > fileIndex + 1 && File.Exists(e.Args[fileIndex + 1])) shell.OpenImage(e.Args[fileIndex + 1]);
        var sessionIndex = Array.IndexOf(e.Args, "--restore-update-session");
        if (sessionIndex >= 0 && e.Args.Length > sessionIndex + 1) shell.RestoreAfterUpdate(e.Args[sessionIndex + 1]);
        if (e.Args.Contains("--update-failed")) shell.Updates.ReportRestartFailure();
        _ = ListenAsync(shutdown.Token);
        updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(); updateTimer.Start();
        shell.ContentRendered += async (_, _) => { await Task.Delay(1500); await CheckUpdatesAsync(); };
    }
    void TrayLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (tray?.ContextMenuStrip is not { } menu) return;
        string[] keys = { "框選截圖", "螢幕錄影", "開啟 SnipFlow", "結束" };
        for (int index = 0; index < keys.Length; index++) menu.Items[index].Text = I18n.T(keys[index]);
    });
    async Task CheckUpdatesAsync()
    {
        if (shell != null && SettingsStore.Current.AutoCheckUpdates) await shell.Updates.CheckAsync(SettingsStore.Current.AutoDownloadUpdates);
    }
    async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream("SnipFlow.Desktop", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var buffer = new char[16]; var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (new string(buffer, 0, count).Trim() == "show") await Dispatcher.InvokeAsync(ShowShell);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex) { SettingsStore.Log(ex); await Task.Delay(500, cancellationToken); }
        }
    }
    public void ShowShell()
    {
        if (shell == null) return; shell.Show(); if (shell.WindowState == WindowState.Minimized) shell.WindowState = WindowState.Normal; shell.Activate();
    }
    public void ExitRequested()
    {
        if (shell?.PrepareToDiscard() == false) return;
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        shutdown.Cancel(); updateTimer.Stop(); I18n.Changed -= TrayLanguageChanged; tray?.Dispose(); trayIcon?.Dispose(); shell?.ReleaseHotkey(); shutdown.Dispose(); base.OnExit(e);
    }
}

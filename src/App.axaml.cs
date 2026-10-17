using System.IO.Pipes;
using System.Text;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Velopack;
using SnipFlow.Capture;
using SnipFlow.Services;
namespace SnipFlow;

public partial class App : Application
{
    static Mutex? instance;
    static ShellCommandLine startup = ShellCommandLine.Parse(Array.Empty<string>());
    readonly CancellationTokenSource shutdown = new();
    readonly List<(NativeMenuItem Item, string Key)> trayItems = new();
    TrayIcon? tray;
    DispatcherTimer? updateTimer;
    MainWindow? shell;
    bool reportingUnhandledException;

    /// <summary>Set once SnipFlow is exiting, so closing the main window no longer just hides it.</summary>
    public static bool IsShuttingDown { get; private set; }

    // Windows keeps the original session-local kernel names. On Linux a Unix socket in the private
    // runtime directory replaces the named pipe, and the mutex is per user rather than per login session,
    // because every terminal or desktop launcher starts its own session.
    static readonly Lazy<string> pipeName = new(CreatePipeName);
    static string PipeName => pipeName.Value;

    static string CreatePipeName()
    {
        if (PlatformInfo.IsWindows) return "SnipFlow.Desktop";
        try
        {
            // Unix socket paths are limited to 107 bytes; $XDG_RUNTIME_DIR is normally the short /run/user/<uid>.
            var path = Path.Combine(LinuxPaths.RuntimeDirectory, "SnipFlow.Desktop.sock");
            if (Encoding.UTF8.GetByteCount(path) <= 100) return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SettingsStore.Log(ex); }
        // Otherwise .NET's own socket in the temporary folder, named per user; CurrentUserOnly checks the peer.
        var user = new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
        return "SnipFlow.Desktop." + (user.Length > 0 ? user : "user");
    }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-bootstrap")
        {
            Environment.ExitCode = UpdateBootstrapper.Run(args[1]); return;
        }
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        startup = ShellCommandLine.Parse(args);
        instance = CreateInstanceMutex(out var first);
        if (!first)
        {
            try
            {
                // A second launch forwards its request (show, capture, open …) and exits.
                var messages = startup.ForwardedMessages();
                if (messages.Count > 0)
                {
                    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                    pipe.Connect(3000); using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                    foreach (var message in messages) writer.WriteLine(message);
                }
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException ex) { SettingsStore.Log(ex); }
            catch (System.Net.Sockets.SocketException ex) { SettingsStore.Log(ex); }
            instance.Dispose(); return;
        }
        try
        {
            I18n.SetLanguage(SettingsStore.Current.Language);
            // Apply at a fresh start only. A second launch must keep the existing editor intact.
            try
            {
                var updates = new UpdateService();
                if (updates.ReadyToRestart && !startup.UpdateFailed) { updates.ApplyAndRestart(args); return; }
            }
            catch (Exception ex) { SettingsStore.Log(ex); }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally { instance.ReleaseMutex(); instance.Dispose(); }
    }

    static Mutex CreateInstanceMutex(out bool first) => PlatformInfo.IsWindows
        ? new Mutex(true, "Local\\SnipFlow.Desktop", out first)
        : new Mutex(true, "SnipFlow.Desktop", new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out first);

    // WM_CLASS matches the desktop entry's StartupWMClass, so docks group SnipFlow's windows under its icon.
    // IME stays on so Chinese input methods (IBus/Fcitx) work in text annotations and fields.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new X11PlatformOptions { EnableIme = true, WmClass = "SnipFlow" }).LogToTrace();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeService.Initialize();
        try
        {
            // Every SnipFlow window shows the app icon, like the WPF AppWindow style. Windows uses the ICO for crisp taskbar sizes.
            using var stream = AssetLoader.Open(new Uri(PlatformInfo.IsWindows ? "avares://SnipFlow/Assets/SnipFlow-v3.ico" : "avares://SnipFlow/Assets/SnipFlow-v3.png"));
            var icon = new WindowIcon(stream);
            Resources["SnipFlowIcon"] = icon;
            Styles.Add(new Style(selector => selector.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } });
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        Dispatcher.UIThread.UnhandledException += (_, error) =>
        {
            SettingsStore.Log(error.Exception); error.Handled = true;
            // A modal error dialog pumps the Dispatcher; do not recursively open another one.
            if (reportingUnhandledException) return;
            reportingUnhandledException = true;
            try
            {
                var message = I18n.T("這個操作未完成，詳細資訊已寫入本機紀錄。\n") + error.Exception.Message;
                try { MessageBox.Show(message, "SnipFlow", MessageBoxButton.OK, MessageBoxImage.Warning); }
                catch (Exception dialogError) { SettingsStore.Log(dialogError); }
            }
            finally { reportingUnhandledException = false; }
        };
        // Listen before the main window exists; requests wait on the UI thread until it is ready.
        _ = ListenAsync(shutdown.Token);
        shell = new MainWindow();
        // Without a visible action, show the panel; capture and open requests reveal it themselves.
        // The lifetime shows MainWindow as it starts, so a tray or command-line start assigns it afterwards.
        var startHidden = startup.Background || startup.Commands.Count > 0;
        if (startHidden) Dispatcher.UIThread.Post(() => desktop.MainWindow ??= shell);
        else desktop.MainWindow = shell;
        CreateTray();
        desktop.Exit += (_, _) => OnExit();
        if (!startHidden) shell.Show();
        if (startup.RestoreSession is { } session) { shell.Show(); shell.RestoreAfterUpdate(session); }
        if (startup.UpdateFailed) shell.Updates.ReportRestartFailure();
        if (startup.Commands.Count > 0) Dispatcher.UIThread.Post(() => RunCommands(startup.Commands), DispatcherPriority.Background);
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(); updateTimer.Start();
        Dispatcher.UIThread.Post(async () => { await Task.Delay(1500); await CheckUpdatesAsync(); }, DispatcherPriority.Background);
    }

    void CreateTray()
    {
        if (shell is not { } window) return;
        var menu = new NativeMenu();
        void AddTrayAction(string key, Action action)
        {
            var item = new NativeMenuItem(I18n.T(key));
            item.Click += (_, _) => Dispatcher.UIThread.Post(action);
            menu.Add(item); trayItems.Add((item, key));
        }
        AddTrayAction("框選截圖", () => _ = window.StartCaptureAsync(CaptureMode.Region));
        AddTrayAction("視窗截圖", () => _ = window.StartCaptureAsync(CaptureMode.Window));
        AddTrayAction("單一螢幕截圖", () => _ = window.StartCaptureAsync(CaptureMode.Monitor));
        AddTrayAction("全部螢幕截圖", () => _ = window.StartCaptureAsync(CaptureMode.AllMonitors));
        AddTrayAction("視窗長截圖", () => _ = window.StartScrollCaptureAsync());
        AddTrayAction("框選長截圖", () => _ = window.StartScrollCaptureAsync(CaptureMode.Region));
        AddTrayAction("程式碼長截圖", () => _ = window.StartCodeCaptureAsync());
        menu.Add(new NativeMenuItemSeparator());
        AddTrayAction("螢幕錄影", () => _ = window.StartRecordingAsync());
        AddTrayAction("空白畫布", window.OpenBlankCanvas);
        AddTrayAction("開啟剪貼簿圖片", () => _ = window.OpenClipboardImageAsync());
        AddTrayAction("開啟 SnipFlow", ShowShell);
        AddTrayAction("結束", ExitRequested);
        I18n.Changed += TrayLanguageChanged;
        try
        {
            // On Linux this is a StatusNotifierItem; without a tray host (for example GNOME without the
            // AppIndicator extension) it stays invisible and SnipFlow is reached by launching it again.
            tray = new TrayIcon { Icon = Resources.TryGetValue("SnipFlowIcon", out var icon) ? icon as WindowIcon : null, ToolTipText = "SnipFlow", Menu = menu, IsVisible = true };
            tray.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowShell);
            TrayIcon.SetIcons(this, new TrayIcons { tray });
        }
        catch (Exception ex) { SettingsStore.Log(ex); tray = null; }
    }

    void TrayLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var (item, key) in trayItems) item.Header = I18n.T(key);
    });

    async Task CheckUpdatesAsync()
    {
        if (shell != null && SettingsStore.Current.AutoCheckUpdates) await shell.Updates.CheckAsync(SettingsStore.Current.AutoDownloadUpdates);
    }

    async Task ListenAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                // A client gets a few seconds and 64 KiB for its request; nothing else is read.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var text = await ReadRequestAsync(server, timeout.Token);
                var commands = new List<ShellCommand>();
                foreach (var line in text.Split('\n'))
                    if (ShellCommand.TryParse(line, out var command)) commands.Add(command);
                if (commands.Count > 0) await Dispatcher.UIThread.InvokeAsync(() => RunCommands(commands));
                failures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // A pipe that cannot be created stops after a while instead of spinning; the app keeps working.
                SettingsStore.Log(ex);
                if (++failures >= 20) break;
                await DelayAsync(cancellationToken);
            }
        }
    }

    static async Task DelayAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(500, cancellationToken); }
        catch (OperationCanceledException) { }
    }

    static async Task<string> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            builder.Append(buffer, 0, count);
            if (builder.Length > 65_536) break;
        }
        return builder.ToString();
    }

    /// <summary>Runs command-line or forwarded requests: open files (several images form a collage), capture, scroll, record, show.</summary>
    void RunCommands(IReadOnlyList<ShellCommand> commands)
    {
        if (shell is not { } window) return;
        var files = commands.Where(command => command.Kind == ShellCommandKind.Open && File.Exists(command.Path))
            .Select(command => command.Path!).Distinct(PlatformInfo.PathComparer).ToArray();
        if (files.Length > 0) window.OpenFiles(files);
        foreach (var command in commands)
            switch (command.Kind)
            {
                case ShellCommandKind.Show: ShowShell(); break;
                case ShellCommandKind.Capture when command.Mode is { } mode: _ = window.StartCaptureAsync(mode); break;
                // A plain --capture is the desktop's keyboard shortcut; it behaves like the global hotkey.
                case ShellCommandKind.Capture: window.DesktopShortcutPressed(); break;
                case ShellCommandKind.Scroll: _ = window.StartScrollCaptureAsync(); break;
                case ShellCommandKind.Record: _ = window.StartRecordingAsync(); break;
            }
    }

    public void ShowShell()
    {
        if (shell == null) return; shell.Show(); if (shell.WindowState == WindowState.Minimized) shell.WindowState = WindowState.Normal; shell.Activate();
    }

    public void ExitRequested()
    {
        if (shell?.PrepareToDiscard() == false) return;
        IsShuttingDown = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    void OnExit()
    {
        IsShuttingDown = true;
        shutdown.Cancel(); updateTimer?.Stop(); ThemeService.Shutdown(); I18n.Changed -= TrayLanguageChanged; tray?.Dispose(); shell?.ReleaseHotkey(); shutdown.Dispose();
    }
}

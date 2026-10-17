using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SnipFlow;

// Foundation placeholder: the shell port replaces this with the tray, single-instance pipe,
// update checks and the main window.
public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeService.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new FoundationPreviewWindow();
            window.Closed += (_, _) => desktop.Shutdown();
            desktop.MainWindow = window;
            window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

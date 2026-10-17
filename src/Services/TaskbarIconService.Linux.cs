namespace SnipFlow.Services;

/// <summary>
/// Windows only: keeps the taskbar button's icon and relaunch identity. Linux desktops match windows to
/// their .desktop entry through WM_CLASS (set in <see cref="App.BuildAvaloniaApp"/>), so nothing is needed.
/// </summary>
internal static class TaskbarIconService
{
    internal static void Attach(Window window) { }
}

namespace SnipFlow.Services;

/// <summary>
/// Native window frame styling that follows the app theme. On Windows 11 this sets the DWM
/// dark mode, rounded corners and border/caption colors; other platforms draw their own frames
/// (SnipFlow windows are frameless), so there is nothing to apply.
/// </summary>
public static partial class WindowAppearance
{
    static readonly List<WeakReference<Window>> Tracked = new();
    static bool listening;

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Track(window);
        if (window.TryGetPlatformHandle() is null)
        {
            window.Opened -= Opened;
            window.Opened += Opened;
            return;
        }
        ApplyNative(window);
    }

    static partial void ApplyNative(Window window);

    static void Opened(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Opened -= Opened;
        ApplyNative(window);
    }

    static void Track(Window window)
    {
        lock (Tracked)
        {
            Tracked.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (!Tracked.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, window)))
                Tracked.Add(new WeakReference<Window>(window));
            if (listening) return;
            listening = true;
        }
        ThemeService.Changed += (_, _) =>
        {
            Window[] windows;
            lock (Tracked) windows = Tracked.Select(reference => reference.TryGetTarget(out var target) ? target : null).OfType<Window>().ToArray();
            foreach (var target in windows)
                if (target.TryGetPlatformHandle() is not null) ApplyNative(target);
        };
    }
}

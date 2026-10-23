namespace SnipFlow.Ui;

/// <summary>
/// Synchronous modal helpers. The WPF version relied on blocking <c>ShowDialog</c> calls in many
/// decision paths (save prompts, capture workflows). Avalonia dialogs are asynchronous, so these
/// helpers run a nested dispatcher frame until the dialog closes, preserving the original flow.
/// Like WPF, other UI events keep running while a modal frame is active.
/// </summary>
public static class Modal
{
    /// <summary>Shows <paramref name="window"/> and blocks the caller until it closes.</summary>
    /// <remarks>
    /// With a visible owner the owner is disabled for the dialog's lifetime. Without one (for example
    /// while the main window is hidden during a capture) the window is shown on its own.
    /// </remarks>
    public static bool? ShowDialog(Window window, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        Dispatcher.UIThread.VerifyAccess();
        var closed = false;
        var frame = new DispatcherFrame();
        void OnClosed(object? sender, EventArgs e) { closed = true; frame.Continue = false; }
        window.Closed += OnClosed;
        try
        {
            if (owner is { IsVisible: true } && !ReferenceEquals(owner, window))
                _ = window.ShowDialog(owner);
            else
                window.Show();
            if (!closed) Dispatcher.UIThread.PushFrame(frame);
        }
        finally { window.Closed -= OnClosed; }
        return (window as DialogWindow)?.DialogResult;
    }

    /// <summary>Waits for <paramref name="task"/> on the UI thread while continuing to pump UI events.</summary>
    public static T Wait<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        WaitCore(task);
        return task.GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="Wait{T}(Task{T})"/>
    public static void Wait(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        WaitCore(task);
        task.GetAwaiter().GetResult();
    }

    static void WaitCore(Task task)
    {
        if (task.IsCompleted) return;
        if (!Dispatcher.UIThread.CheckAccess()) { task.Wait(); return; }
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false, DispatcherPriority.Send),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.UIThread.PushFrame(frame);
    }

    /// <summary>The application's main window, or the most recently active visible window.</summary>
    public static Window? DefaultOwner()
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        return desktop.Windows.LastOrDefault(window => window.IsActive && window.IsVisible)
            ?? (desktop.MainWindow is { IsVisible: true } main ? main : null)
            ?? desktop.Windows.LastOrDefault(window => window.IsVisible);
    }
}

/// <summary>A window with WPF-style <see cref="DialogResult"/> semantics for <see cref="Modal.ShowDialog"/>.</summary>
public class DialogWindow : Window
{
    bool? dialogResult;

    /// <summary>Setting a value closes the window, as in WPF.</summary>
    public bool? DialogResult
    {
        get => dialogResult;
        set
        {
            dialogResult = value;
            if (IsVisible) Close();
        }
    }

    /// <summary>Shows the window modally and returns <see cref="DialogResult"/> after it closes.</summary>
    public bool? ShowModal(Window? owner = null) => Modal.ShowDialog(this, owner);
}

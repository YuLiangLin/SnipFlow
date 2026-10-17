using Avalonia.Input;
using SnipFlow.Platform.Linux;
using SnipFlow.Platform.Linux.Portal;
using SnipFlow.Platform.Linux.X11;

namespace SnipFlow.Services;

public sealed partial class GlobalHotkeyService
{
    X11HotkeyGrab? x11;
    GlobalShortcutsPortal? portal;
    CancellationTokenSource? portalRequest;
    static bool? portalAvailable;

    private partial bool TryRegisterNative(HotkeyGesture gesture, out int errorCode)
    {
        errorCode = 0;
        if (PlatformInfo.IsX11)
        {
            x11 ??= X11HotkeyGrab.TryCreate(() => Dispatcher.UIThread.Post(RaisePressed));
            if (x11 is null) { errorCode = ErrorUnsupported; IsUnsupported = true; return false; }
            var result = x11.TryGrab(gesture.X11KeySym, X11Modifiers(gesture));
            if (result == 0) { SetActive(gesture); return true; }
            errorCode = result == Xcb.BadAccess ? ErrorInUse : result;
            return false;
        }
        if (PlatformInfo.IsWayland && portalAvailable != false)
        {
            BindWithPortal(gesture);
            errorCode = ErrorPending;
            return false;
        }
        IsUnsupported = true;
        errorCode = ErrorUnsupported;
        return false;
    }

    static uint X11Modifiers(HotkeyGesture gesture)
        => ((gesture.Modifiers & KeyModifiers.Shift) != 0 ? XLib.ShiftMask : 0)
        | ((gesture.Modifiers & KeyModifiers.Control) != 0 ? XLib.ControlMask : 0)
        | ((gesture.Modifiers & KeyModifiers.Alt) != 0 ? XLib.Mod1Mask : 0);

    async void BindWithPortal(HotkeyGesture gesture)
    {
        portalRequest?.Cancel();
        var request = portalRequest = new CancellationTokenSource();
        SetPending(true);
        try
        {
            // An unanswered check is not remembered, so the next registration asks the portal again.
            var available = portalAvailable ?? await GlobalShortcutsPortal.IsAvailableAsync();
            if (available is { } known) portalAvailable = known;
            if (disposed || request.IsCancellationRequested) return;
            if (available != true)
            {
                IsUnsupported = true;
                SetPending(false);
                RaiseStateChanged();
                return;
            }
            portal ??= new GlobalShortcutsPortal(
                () => Dispatcher.UIThread.Post(RaisePressed),
                trigger => Dispatcher.UIThread.Post(() =>
                {
                    if (disposed || ActiveGesture is not { } active) return;
                    SetActive(active, trigger);
                    RaiseStateChanged();
                }));
            var binding = await portal.BindAsync(GlobalShortcutsPortal.PreferredTrigger(gesture), I18n.T("框選截圖"), request.Token);
            if (disposed || request.IsCancellationRequested) return;
            // A new portal session replaces the old one, so a declined request leaves no shortcut.
            SetActive(binding.Bound ? gesture : null, binding.Bound ? binding.TriggerDescription : null);
            SetPending(false, binding.Bound ? "" : binding.Cancelled ? I18n.T("桌面環境未核准全域快捷鍵。") : I18n.T("桌面環境未啟用全域快捷鍵。"));
            RaiseStateChanged();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            if (disposed || request.IsCancellationRequested) return;
            SetActive(null);
            SetPending(false, I18n.T("全域快捷鍵無法啟用。"));
            RaiseStateChanged();
        }
    }

    partial void DisposeNative()
    {
        portalRequest?.Cancel();
        x11?.Dispose(); x11 = null;
        portal?.Dispose(); portal = null;
    }

    static partial void ReadDesktopShortcutNative(ref Task<DesktopShortcut?> result)
    {
        result = ReadGnomeShortcutAsync();
    }

    static async Task<DesktopShortcut?> ReadGnomeShortcutAsync()
    {
        try
        {
            var state = await GnomeKeybinding.ReadAsync();
            return state is null ? null : new DesktopShortcut(state.Registered, state.Gesture, state.CommandIsCurrent);
        }
        catch (Exception ex) { SettingsStore.Log(ex); return null; }
    }

    static partial void RegisterDesktopShortcutNative(HotkeyGesture gesture, ref Task? result)
    {
        result = GnomeKeybinding.RegisterAsync(gesture);
    }

    static partial void CanRegisterDesktopShortcutNative(ref bool result) => result = GnomeKeybinding.IsAvailable;
}

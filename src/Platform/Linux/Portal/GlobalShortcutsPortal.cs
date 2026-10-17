using Tmds.DBus.Protocol;

namespace SnipFlow.Platform.Linux.Portal;

/// <summary>The outcome of BindShortcuts: whether the desktop accepted the shortcut, and its trigger text.</summary>
internal sealed record GlobalShortcutBinding(bool Bound, bool Cancelled, string? TriggerDescription);

/// <summary>
/// org.freedesktop.portal.GlobalShortcuts: the Wayland way to receive a system-wide shortcut. The
/// desktop (KDE Plasma, GNOME 48+, Hyprland…) owns the key and may let the user choose another
/// trigger; SnipFlow passes its setting as the preferred trigger and listens for Activated.
/// </summary>
internal sealed class GlobalShortcutsPortal : IDisposable
{
    public const string Interface = "org.freedesktop.portal.GlobalShortcuts";
    const string SessionInterface = "org.freedesktop.portal.Session";
    const string ShortcutId = "capture";

    readonly Action activated;
    readonly Action<string?> changed;
    string? sessionHandle;
    IDisposable? activatedWatch, changedWatch;
    bool disposed;

    public GlobalShortcutsPortal(Action activated, Action<string?> changed)
    {
        this.activated = activated;
        this.changed = changed;
    }

    /// <summary>
    /// True when the session's portal implements GlobalShortcuts, false when it does not, and null
    /// when the portal did not answer in time (it may still be starting, so the caller can ask again).
    /// </summary>
    public static async Task<bool?> IsAvailableAsync()
    {
        try
        {
            var version = await DesktopPortal.TryGetPropertyAsync(Interface, "version")
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return version is { Type: VariantValueType.UInt32 } value && value.GetUInt32() >= 1;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or InvalidOperationException or IOException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Binds the capture shortcut with <paramref name="preferredTrigger"/> (XDG shortcut syntax, such as
    /// "CTRL+ALT+s"). A portal session binds once, so a changed trigger starts a new session.
    /// </summary>
    public async Task<GlobalShortcutBinding> BindAsync(string preferredTrigger, string description, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await CloseSessionAsync().ConfigureAwait(false);
        var bus = await DesktopPortal.ConnectAsync().ConfigureAwait(false);
        activatedWatch ??= await bus.WatchSignalAsync(DesktopPortal.Service, DesktopPortal.ObjectPath, Interface, "Activated",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var session = reader.ReadObjectPathAsString();
                var id = reader.ReadString();
                return (Session: session, Id: id);
            },
            (Notification<(string Session, string Id)> notification) =>
            {
                if (notification.Type != NotificationType.Value || disposed) return;
                var value = notification.Value;
                if (value.Id == ShortcutId && value.Session == sessionHandle) activated();
            },
            ObserverFlags.None, false, null).ConfigureAwait(false);
        changedWatch ??= await bus.WatchSignalAsync(DesktopPortal.Service, DesktopPortal.ObjectPath, Interface, "ShortcutsChanged",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var session = reader.ReadObjectPathAsString();
                string? trigger = null;
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    var id = reader.ReadString();
                    var properties = reader.ReadDictionaryOfStringToVariantValue();
                    if (id == ShortcutId) trigger = TriggerText(properties);
                }
                return (Session: session, Trigger: trigger);
            },
            (Notification<(string Session, string? Trigger)> notification) =>
            {
                if (notification.Type != NotificationType.Value || disposed) return;
                if (notification.Value.Session == sessionHandle) changed(notification.Value.Trigger);
            },
            ObserverFlags.None, false, null).ConfigureAwait(false);

        var created = await DesktopPortal.RequestAsync(Interface, "CreateSession", "a{sv}", null,
            new Dictionary<string, VariantValue> { ["session_handle_token"] = VariantValue.String(DesktopPortal.NewToken()) },
            cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded) return new(false, created.Cancelled, null);
        if (!created.Results.TryGetValue("session_handle", out var handleValue)) return new(false, false, null);
        var handle = handleValue.Type == VariantValueType.ObjectPath ? handleValue.GetObjectPathAsString() : handleValue.GetString();
        sessionHandle = handle;

        var shortcutProperties = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["description"] = VariantValue.String(description),
            ["preferred_trigger"] = VariantValue.String(preferredTrigger),
        };
        var bound = await DesktopPortal.RequestAsync(Interface, "BindShortcuts", "oa(sa{sv})sa{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteObjectPath(handle);
            var array = writer.WriteArrayStart(DBusType.Struct);
            writer.WriteStructureStart();
            writer.WriteString(ShortcutId);
            writer.WriteDictionary(shortcutProperties);
            writer.WriteArrayEnd(array);
            writer.WriteString(""); // No parent window identifier.
        }, null, cancellationToken).ConfigureAwait(false);
        if (!bound.Succeeded) return new(false, bound.Cancelled, null);
        string? trigger = null;
        var found = false;
        if (bound.Results.TryGetValue("shortcuts", out var shortcuts) && shortcuts.Type == VariantValueType.Array)
            for (int index = 0; index < shortcuts.Count; index++)
            {
                var item = shortcuts.GetItem(index);
                if (item.Type != VariantValueType.Struct || item.Count < 2 || item.GetItem(0).GetString() != ShortcutId) continue;
                found = true;
                var properties = item.GetItem(1).GetDictionary<string, VariantValue>();
                trigger = TriggerText(properties);
            }
        return new(found, false, trigger);
    }

    static string? TriggerText(Dictionary<string, VariantValue> properties)
        => properties.TryGetValue("trigger_description", out var value) && value.Type == VariantValueType.String
            && value.GetString() is { Length: > 0 } text ? text : null;

    async Task CloseSessionAsync()
    {
        var handle = Interlocked.Exchange(ref sessionHandle, null);
        if (handle is null) return;
        try
        {
            var bus = await DesktopPortal.ConnectAsync().ConfigureAwait(false);
            MessageBuffer message;
            // Dispose the writer that created the message, not a copy made before CreateMessage.
            var w = bus.GetMessageWriter();
            try
            {
                w.WriteMethodCallHeader(DesktopPortal.Service, handle, SessionInterface, "Close", "", MessageFlags.None);
                message = w.CreateMessage();
            }
            finally { w.Dispose(); }
            await bus.CallMethodAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex) { SettingsStore.Log(ex); }
    }

    /// <summary>The XDG shortcut trigger for a gesture, for example "CTRL+ALT+s" or "SHIFT+F1".</summary>
    public static string PreferredTrigger(HotkeyGesture gesture)
    {
        var parts = new List<string>();
        if ((gesture.Modifiers & Avalonia.Input.KeyModifiers.Control) != 0) parts.Add("CTRL");
        if ((gesture.Modifiers & Avalonia.Input.KeyModifiers.Alt) != 0) parts.Add("ALT");
        if ((gesture.Modifiers & Avalonia.Input.KeyModifiers.Shift) != 0) parts.Add("SHIFT");
        var key = gesture.Key;
        parts.Add(key switch
        {
            >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => ((char)('a' + (key - Avalonia.Input.Key.A))).ToString(),
            >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => ((char)('0' + (key - Avalonia.Input.Key.D0))).ToString(),
            _ => key.ToString(),
        });
        return string.Join('+', parts);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        activatedWatch?.Dispose(); changedWatch?.Dispose();
        _ = CloseSessionAsync();
    }
}

using Tmds.DBus.Protocol;

namespace SnipFlow.Platform.Linux.Portal;

/// <summary>The org.freedesktop.portal.Request result: 0 success, 1 cancelled by the user, 2 other failure.</summary>
internal sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results)
{
    public bool Succeeded => Code == 0;
    public bool Cancelled => Code == 1;
}

/// <summary>Writes the arguments that come before the trailing a{sv} options dictionary.</summary>
internal delegate void PortalArgumentWriter(ref MessageWriter writer);

/// <summary>
/// Client for xdg-desktop-portal (org.freedesktop.portal.Desktop) on the session bus.
/// Used for Screenshot, ScreenCast and GlobalShortcuts in Wayland sessions, where clients cannot
/// read other windows' pixels or grab keys directly.
/// </summary>
internal static class DesktopPortal
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    const string RequestInterface = "org.freedesktop.portal.Request";

    static readonly SemaphoreSlim ConnectGate = new(1, 1);
    static DBusConnection? connection;

    public static async Task<DBusConnection> ConnectAsync()
    {
        if (connection is { } existing) return existing;
        await ConnectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (connection is { } current) return current;
            var address = DBusAddress.Session ?? throw new InvalidOperationException(I18n.T("找不到桌面工作階段的 D-Bus，無法使用系統截圖服務。"));
            var created = new DBusConnection(address);
            await created.ConnectAsync().ConfigureAwait(false);
            connection = created;
            _ = WatchDisconnectAsync(created);
            return created;
        }
        finally { ConnectGate.Release(); }
    }

    static async Task WatchDisconnectAsync(DBusConnection watched)
    {
        try { await watched.DisconnectedAsync().ConfigureAwait(false); }
        catch (Exception) { }
        Interlocked.CompareExchange(ref connection, null, watched);
    }

    /// <summary>
    /// Calls a portal method whose last argument is an a{sv} options dictionary and waits for the
    /// Request.Response signal. <paramref name="signature"/> is the full method signature, e.g. "sa{sv}".
    /// </summary>
    public static async Task<PortalResponse> RequestAsync(string interfaceName, string member, string signature,
        PortalArgumentWriter? writeLeadingArguments, IReadOnlyDictionary<string, VariantValue>? options,
        CancellationToken cancellationToken = default)
    {
        var bus = await ConnectAsync().ConfigureAwait(false);
        var token = "snipflow_" + Guid.NewGuid().ToString("N");
        var expectedPath = RequestPath(bus, token);
        var completion = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe before calling so a fast response cannot be missed.
        using var watch = await WatchResponseAsync(bus, expectedPath, completion).ConfigureAwait(false);
        var arguments = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
        if (options is not null) foreach (var (key, value) in options) arguments[key] = value;
        arguments["handle_token"] = VariantValue.String(token);

        MessageBuffer message;
        using (var writer = bus.GetMessageWriter())
        {
            var w = writer;
            w.WriteMethodCallHeader(Service, ObjectPath, interfaceName, member, signature, MessageFlags.None);
            writeLeadingArguments?.Invoke(ref w);
            w.WriteDictionary(arguments);
            message = w.CreateMessage();
        }
        var actualPath = await bus.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null)
            .ConfigureAwait(false);

        // Portals older than 0.9 return a different handle; listen there as well.
        using var fallback = actualPath != expectedPath
            ? await WatchResponseAsync(bus, actualPath, completion).ConfigureAwait(false) : null;
        using var registration = cancellationToken.Register(() =>
        {
            completion.TrySetCanceled(cancellationToken);
            _ = CloseRequestAsync(bus, actualPath);
        });
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Calls a portal method that replies directly (no Request object).</summary>
    public static async Task<T> CallAsync<T>(string interfaceName, string member, string signature,
        PortalArgumentWriter? writeArguments, MessageValueReader<T> readReply)
    {
        var bus = await ConnectAsync().ConfigureAwait(false);
        MessageBuffer message;
        using (var writer = bus.GetMessageWriter())
        {
            var w = writer;
            w.WriteMethodCallHeader(Service, ObjectPath, interfaceName, member, signature, MessageFlags.None);
            writeArguments?.Invoke(ref w);
            message = w.CreateMessage();
        }
        return await bus.CallMethodAsync(message, readReply, null).ConfigureAwait(false);
    }

    /// <summary>Reads a portal interface property, for example the "version" of org.freedesktop.portal.ScreenCast.</summary>
    public static async Task<VariantValue?> TryGetPropertyAsync(string interfaceName, string property)
    {
        try
        {
            var bus = await ConnectAsync().ConfigureAwait(false);
            MessageBuffer message;
            using (var writer = bus.GetMessageWriter())
            {
                var w = writer;
                w.WriteMethodCallHeader(Service, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
                w.WriteString(interfaceName);
                w.WriteString(property);
                message = w.CreateMessage();
            }
            return await bus.CallMethodAsync(message, static (m, _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
        }
        catch (DBusExceptionBase) { return null; }
    }

    /// <summary>Converts a portal "uri" result (file://...) to a local path.</summary>
    public static string? LocalPath(VariantValue? uri)
    {
        if (uri is not { Type: VariantValueType.String } value) return null;
        return Uri.TryCreate(value.GetString(), UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;
    }

    public static string NewToken() => "snipflow_" + Guid.NewGuid().ToString("N");

    static string RequestPath(DBusConnection bus, string token)
    {
        var sender = (bus.UniqueName ?? "").TrimStart(':').Replace('.', '_');
        return $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
    }

    static async Task<IDisposable> WatchResponseAsync(DBusConnection bus, string path, TaskCompletionSource<PortalResponse> completion)
        => await bus.WatchSignalAsync(Service, path, RequestInterface, "Response",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return new PortalResponse(code, results);
            },
            (Notification<PortalResponse> notification) =>
            {
                if (notification.Type == NotificationType.Value) completion.TrySetResult(notification.Value);
                else if (notification.Exception is { } error) completion.TrySetException(error);
                else completion.TrySetException(new InvalidOperationException(I18n.T("系統截圖服務已中斷，請重新操作。")));
            },
            ObserverFlags.None, false, null).ConfigureAwait(false);

    static async Task CloseRequestAsync(DBusConnection bus, string path)
    {
        try
        {
            MessageBuffer message;
            using (var writer = bus.GetMessageWriter())
            {
                var w = writer;
                w.WriteMethodCallHeader(Service, path, RequestInterface, "Close", "", MessageFlags.None);
                message = w.CreateMessage();
            }
            await bus.CallMethodAsync(message).ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}

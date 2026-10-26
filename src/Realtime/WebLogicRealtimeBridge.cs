using CodeLogic.Core.Events;
using CL.WebLogic.Runtime;

namespace CL.WebLogic.Realtime;

public sealed class WebLogicRealtimeBridge : IWebLogicRealtimeBridge
{
    private IWebLogicRealtimeBroadcaster? _broadcaster;
    private readonly List<IEventSubscription> _subscriptions = [];
    private readonly object _gate = new();

    public WebLogicRealtimeBuffer Buffer { get; }

    public WebLogicRealtimeBridge(
        WebLogicRealtimeBuffer buffer,
        IWebLogicRealtimeBroadcaster? broadcaster = null)
    {
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _broadcaster = broadcaster;
    }

    public void AttachBroadcaster(IWebLogicRealtimeBroadcaster broadcaster)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    }

    public IReadOnlyList<WebLogicRealtimeEnvelope> GetRecent(int take = 100) =>
        Buffer.GetRecent(take);

    public WebLogicRealtimeSnapshot CreateSnapshot(int take = 100) =>
        Buffer.CreateSnapshot(take);

    public void Publish(WebLogicRealtimeEnvelope envelope)
    {
        Buffer.Add(envelope);
        _ = _broadcaster?.BroadcastAsync(envelope);
    }

    public async Task PublishAsync(WebLogicRealtimeEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Buffer.Add(envelope);
        if (_broadcaster is not null)
            await _broadcaster.BroadcastAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    public void RegisterDefaultMappings(IEventBus eventBus)
    {
        Register<WebRequestHandledEvent>(eventBus, MapRequestHandled);
        Register<WebRequestBlockedEvent>(eventBus, MapRequestBlocked);
        Register<ThemeSynchronizedEvent>(eventBus, MapThemeSynchronized);
        Register<LibraryStartedEvent>(eventBus, MapLibraryStarted);
        Register<LibraryStoppedEvent>(eventBus, MapLibraryStopped);
        Register<LibraryFailedEvent>(eventBus, MapLibraryFailed);
        Register<PluginLoadedEvent>(eventBus, MapPluginLoaded);
        Register<PluginUnloadedEvent>(eventBus, MapPluginUnloaded);
        Register<PluginFailedEvent>(eventBus, MapPluginFailed);
        Register<ConfigReloadedEvent>(eventBus, MapConfigReloaded);
        Register<LocalizationReloadedEvent>(eventBus, MapLocalizationReloaded);
        Register<HealthCheckCompletedEvent>(eventBus, MapHealthCheckCompleted);
        Register<ShutdownRequestedEvent>(eventBus, MapShutdownRequested);
        Register<ComponentAlertEvent>(eventBus, MapComponentAlert);
    }

    public IEventSubscription Register<TEvent>(
        IEventBus eventBus,
        Func<TEvent, WebLogicRealtimeEnvelope?> mapper) where TEvent : IEvent
    {
        if (eventBus is null)
            throw new ArgumentNullException(nameof(eventBus));

        if (mapper is null)
            throw new ArgumentNullException(nameof(mapper));

        var subscription = eventBus.Subscribe<TEvent>(evt =>
        {
            var envelope = mapper(evt);
            if (envelope is not null)
                Publish(envelope);
        });

        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();

            _subscriptions.Clear();
        }
    }

    private static WebLogicRealtimeEnvelope? MapRequestHandled(WebRequestHandledEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.RequestHandled,
            "cl.weblogic.runtime",
            $"{e.Method} {e.Path}",
            $"Request completed with {e.StatusCode} in {e.DurationMs}ms",
            new
            {
                e.Method,
                e.Path,
                e.StatusCode,
                e.DurationMs
            },
            WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapRequestBlocked(WebRequestBlockedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.RequestBlocked,
            "cl.weblogic.security",
            $"{e.Method} {e.Path}",
            e.Reason,
            new
            {
                e.Method,
                e.Path,
                e.StatusCode,
                e.Reason
            },
            WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapThemeSynchronized(ThemeSynchronizedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.ThemeSynchronized,
            "cl.weblogic.theme",
            $"Theme synchronized: {e.Source}",
            $"Theme root: {e.ThemeRoot}",
            e,
            WebLogicRealtimeAudience.Authenticated);

    private static WebLogicRealtimeEnvelope? MapLibraryStarted(LibraryStartedEvent e) =>
        MapFrameworkEvent(WebLogicRealtimeKind.LibraryStarted, "cl.logic.library", e.LibraryId, e.LibraryName, e, WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapLibraryStopped(LibraryStoppedEvent e) =>
        MapFrameworkEvent(WebLogicRealtimeKind.LibraryStopped, "cl.logic.library", e.LibraryId, e.LibraryName, e, WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapLibraryFailed(LibraryFailedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.LibraryFailed,
            $"cl.logic.library.{e.LibraryId}",
            e.LibraryName,
            e.Error.Message,
            new
            {
                e.LibraryId,
                e.LibraryName,
                ErrorType = e.Error.GetType().FullName,
                Error = e.Error.Message
            },
            WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapPluginLoaded(PluginLoadedEvent e) =>
        MapFrameworkEvent(WebLogicRealtimeKind.PluginLoaded, "cl.logic.plugin", e.PluginId, e.PluginName, e, WebLogicRealtimeAudience.Authenticated);

    private static WebLogicRealtimeEnvelope? MapPluginUnloaded(PluginUnloadedEvent e) =>
        MapFrameworkEvent(WebLogicRealtimeKind.PluginUnloaded, "cl.logic.plugin", e.PluginId, e.PluginName, e, WebLogicRealtimeAudience.Authenticated);

    private static WebLogicRealtimeEnvelope? MapPluginFailed(PluginFailedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.PluginFailed,
            $"cl.logic.plugin.{e.PluginId}",
            e.PluginName,
            e.Error.Message,
            new
            {
                e.PluginId,
                e.PluginName,
                ErrorType = e.Error.GetType().FullName,
                Error = e.Error.Message
            },
            WebLogicRealtimeAudience.Authenticated);

    private static WebLogicRealtimeEnvelope? MapConfigReloaded(ConfigReloadedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.ConfigReloaded,
            "cl.logic.config",
            e.ComponentId,
            e.ConfigType.Name,
            e,
            WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapLocalizationReloaded(LocalizationReloadedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.LocalizationReloaded,
            "cl.logic.localization",
            e.ComponentId,
            null,
            e,
            WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapHealthCheckCompleted(HealthCheckCompletedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.HealthCheckCompleted,
            "cl.logic.health",
            e.ComponentId,
            e.Message,
            e,
            e.IsHealthy ? WebLogicRealtimeAudience.Authenticated : WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapShutdownRequested(ShutdownRequestedEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.ShutdownRequested,
            "cl.logic.shutdown",
            "Shutdown requested",
            e.Reason,
            e,
            WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope? MapComponentAlert(ComponentAlertEvent e) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.ComponentAlert,
            e.ComponentId,
            e.AlertType,
            e.Message,
            e.Payload,
            WebLogicRealtimeAudience.Internal);

    private static WebLogicRealtimeEnvelope MapFrameworkEvent(
        WebLogicRealtimeKind kind,
        string source,
        string title,
        string? message,
        object payload,
        WebLogicRealtimeAudience audience) =>
        WebLogicRealtimeEnvelope.Create(kind, source, title, message, payload, audience);
}

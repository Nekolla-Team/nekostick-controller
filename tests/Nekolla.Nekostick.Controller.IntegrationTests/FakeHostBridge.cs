using System.Collections.Concurrent;
using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

/// <summary>
/// Minimal in-memory Host bridge that satisfies the controller's host-API 1.3 contract.
/// Configuration reads and writes are backed by a versioned snapshot so management mutations
/// round-trip exactly as they would against a real Host.
/// </summary>
public sealed class FakeHostBridge : IExtensionHostBridge13
{
    private readonly object _sync = new();
    private HostConfigurationSnapshot _snapshot;
    private ImmutableArray<ExtensionServiceRuntimeSnapshot> _supervisorSnapshots = ImmutableArray<ExtensionServiceRuntimeSnapshot>.Empty;
    private ConfigurationError? _nextReplaceFailure;

    public FakeHostBridge(HostConfigurationSnapshot initialSnapshot)
    {
        _snapshot = initialSnapshot;
        FullConfiguration = new FakeFullConfigurationApi(this);
        Status = new FakeStatusSink();
        Logger = new FakeLogger();
        LogWriter = new FakeLogWriter();
        Supervisor = new FakeSupervisorApi(this);
        Configuration = new FakeSettingsReader(this);
        ConfigurationApi = new FakeConfigurationApi(this);
        Routes = new FakeRouteApi();
        Services = new FakeServiceApi();
        Endpoints = new FakeEndpointApi();
        Lifecycle = new FakeLifecycleApi();
        Contracts = new FakeContractRegistry();
        Tasks = new FakeTaskScheduler();
        Events = new FakeEventPublisher();
        RouteEvents = new FakeRouteEvents();
    }

    public HostApiVersion ApiVersion { get; } = new(1, 3, 0);
    public IExtensionSettingsReader Configuration { get; }
    public IExtensionConfigurationApi ConfigurationApi { get; }
    public IExtensionFullConfigurationApi FullConfiguration { get; }
    public IExtensionRouteApi Routes { get; }
    public IExtensionServiceApi Services { get; }
    public IExtensionEndpointApi Endpoints { get; }
    public IExtensionLifecycleApi Lifecycle { get; }
    public IExtensionContractRegistry Contracts { get; }
    public IExtensionTaskScheduler Tasks { get; }
    public IExtensionEventPublisher Events { get; }
    public IExtensionStatusSink Status { get; }
    public IExtensionLogger Logger { get; }
    public IExtensionSupervisorApi Supervisor { get; }
    public IExtensionRouteEvents RouteEvents { get; }
    public IExtensionLogWriter LogWriter { get; }

    public ConcurrentQueue<ExtensionStatus> ReportedStatuses => ((FakeStatusSink)Status).Statuses;

    public HostConfigurationSnapshot ReadSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    /// <summary>Replaces the runtime snapshots returned by the supervisor facade.</summary>
    public void SetSupervisorSnapshots(ImmutableArray<ExtensionServiceRuntimeSnapshot> snapshots)
    {
        lock (_sync)
        {
            _supervisorSnapshots = snapshots;
        }
    }
    /// <summary>
    /// Makes the next <see cref="IExtensionFullConfigurationApi.ReplaceAsync"/> call fail once with
    /// the supplied error, simulating a Host-side write failure (for example a concurrency race
    /// between the controller's read and write) without altering the stored snapshot.
    /// </summary>
    public void FailNextReplace(ConfigurationErrorCode code)
    {
        lock (_sync)
        {
            _nextReplaceFailure = new ConfigurationError(code);
        }
    }

    public ConfigurationWriteResult ReplaceSnapshot(long expectedVersion, ConfigurationChangeSet changes)
    {
        lock (_sync)
        {
            if (_nextReplaceFailure is { } failure)
            {
                _nextReplaceFailure = null;
                return ConfigurationWriteResult.Failure(failure);
            }

            if (expectedVersion != _snapshot.Version)
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));
            }

            _snapshot = new HostConfigurationSnapshot(
                _snapshot.Version + 1,
                changes.GlobalSettings,
                changes.Routes,
                changes.Services,
                changes.ExtensionRecords,
                changes.ExtensionSettings);
            return ConfigurationWriteResult.Success(_snapshot.Version);
        }
    }

    private ImmutableArray<ExtensionServiceRuntimeSnapshot> ReadSupervisorSnapshots()
    {
        lock (_sync)
        {
            return _supervisorSnapshots;
        }
    }

    private ExtensionConfigurationSnapshot ReadExtensionSnapshot()
    {
        lock (_sync)
        {
            var settings = _snapshot.ExtensionSettings.FirstOrDefault(static value =>
                string.Equals(value.ExtensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal));

            return new ExtensionConfigurationSnapshot(
                _snapshot.Version,
                _snapshot.Routes
                    .Select(ToExtensionRoute)
                    .Where(static route => route is not null)
                    .Select(static route => route!)
                    .ToImmutableArray(),
                _snapshot.Services.Select(ToExtensionService).ToImmutableArray(),
                settings);
        }
    }

    private ExtensionSettingsConfiguration ReadExtensionSettings()
    {
        lock (_sync)
        {
            return _snapshot.ExtensionSettings.FirstOrDefault(static value =>
                       string.Equals(value.ExtensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal))
                   ?? new ExtensionSettingsConfiguration(ControllerOptions.ExtensionId, 1, "{}", 0);
        }
    }

    private ConfigurationWriteResult ApplyExtensionChanges(long expectedVersion, ExtensionConfigurationChangeSet changes)
    {
        lock (_sync)
        {
            if (expectedVersion != _snapshot.Version)
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));
            }

            var newVersion = _snapshot.Version + 1;
            var now = DateTimeOffset.UtcNow;
            var routes = _snapshot.Routes.ToBuilder();
            foreach (var removedRouteId in changes.RemovedRouteIds)
            {
                for (var index = routes.Count - 1; index >= 0; index--)
                {
                    if (routes[index].Id == removedRouteId)
                    {
                        routes.RemoveAt(index);
                    }
                }
            }

            foreach (var route in changes.Upserts)
            {
                var existingIndex = -1;
                for (var index = 0; index < routes.Count; index++)
                {
                    if (routes[index].Id == route.Id)
                    {
                        existingIndex = index;
                        break;
                    }
                }

                var replacement = ToRoute(route, existingIndex >= 0 ? routes[existingIndex] : null, now, newVersion);
                if (existingIndex >= 0)
                {
                    routes[existingIndex] = replacement;
                }
                else
                {
                    routes.Add(replacement);
                }
            }

            var services = _snapshot.Services.ToBuilder();
            foreach (var removedServiceId in changes.RemovedServiceIds)
            {
                for (var index = services.Count - 1; index >= 0; index--)
                {
                    if (services[index].Id == removedServiceId)
                    {
                        services.RemoveAt(index);
                    }
                }
            }

            foreach (var service in changes.ServiceUpserts)
            {
                var existingIndex = -1;
                for (var index = 0; index < services.Count; index++)
                {
                    if (services[index].Id == service.Id)
                    {
                        existingIndex = index;
                        break;
                    }
                }

                var replacement = ToService(service, existingIndex >= 0 ? services[existingIndex] : null, now, newVersion);
                if (existingIndex >= 0)
                {
                    services[existingIndex] = replacement;
                }
                else
                {
                    services.Add(replacement);
                }
            }


            var extensionSettings = _snapshot.ExtensionSettings;
            if (changes.Settings is { } settings)
            {
                var settingsBuilder = extensionSettings.ToBuilder();
                var existingIndex = -1;
                for (var index = 0; index < settingsBuilder.Count; index++)
                {
                    if (string.Equals(settingsBuilder[index].ExtensionId, settings.ExtensionId, StringComparison.Ordinal))
                    {
                        existingIndex = index;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    settingsBuilder[existingIndex] = settings;
                }
                else
                {
                    settingsBuilder.Add(settings);
                }

                extensionSettings = settingsBuilder.ToImmutable();
            }

            _snapshot = new HostConfigurationSnapshot(
                newVersion,
                _snapshot.GlobalSettings,
                routes.ToImmutable(),
                services.ToImmutable(),
                _snapshot.ExtensionRecords,
                extensionSettings);
            return ConfigurationWriteResult.Success(newVersion);
        }
    }

    private ConfigurationWriteResult WriteExtensionSettings(long expectedVersion, ExtensionSettingsConfiguration settings)
    {
        lock (_sync)
        {
            var current = _snapshot.ExtensionSettings.FirstOrDefault(value =>
                string.Equals(value.ExtensionId, settings.ExtensionId, StringComparison.Ordinal));
            if (current is null || expectedVersion != current.Version)
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));
            }

            var replacement = new ExtensionSettingsConfiguration(
                settings.ExtensionId,
                settings.SchemaVersion,
                settings.SettingsJson,
                current.Version + 1);
            var settingsBuilder = _snapshot.ExtensionSettings.ToBuilder();
            var settingsIndex = -1;
            for (var index = 0; index < settingsBuilder.Count; index++)
            {
                if (string.Equals(settingsBuilder[index].ExtensionId, settings.ExtensionId, StringComparison.Ordinal))
                {
                    settingsIndex = index;
                    break;
                }
            }

            if (settingsIndex < 0)
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict));
            }

            settingsBuilder[settingsIndex] = replacement;
            var newVersion = _snapshot.Version + 1;
            _snapshot = new HostConfigurationSnapshot(
                newVersion,
                _snapshot.GlobalSettings,
                _snapshot.Routes,
                _snapshot.Services,
                _snapshot.ExtensionRecords,
                settingsBuilder.ToImmutable());
            return ConfigurationWriteResult.Success(newVersion);
        }
    }

    private static ExtensionRouteConfiguration? ToExtensionRoute(RouteConfiguration route) => route.Target switch
    {
        MicroserviceRouteTargetConfiguration target => new ExtensionRouteConfiguration(
            route.Id,
            route.Enabled,
            route.Matcher,
            new ExtensionServiceRouteTarget(target.ServiceId),
            route.Priority),
        ExtensionHandlerRouteTargetConfiguration target => new ExtensionRouteConfiguration(
            route.Id,
            route.Enabled,
            route.Matcher,
            new ExtensionHandlerRouteTarget(target.HandlerId),
            route.Priority),
        _ => null
    };

    private static ExtensionServiceConfiguration ToExtensionService(ServiceConfiguration service) => new(
        service.Id,
        service.Enabled,
        service.FileName,
        service.ArgumentList,
        service.WorkingDirectory,
        service.StartMode,
        service.RestartPolicy,
        service.HealthCheck,
        service.CreatedAt,
        service.UpdatedAt,
        service.Version);

    private static RouteConfiguration ToRoute(
        ExtensionRouteConfiguration route,
        RouteConfiguration? current,
        DateTimeOffset now,
        long version) => new(
        route.Id,
        route.Enabled,
        route.Matcher,
        route.Target switch
        {
            ExtensionServiceRouteTarget target => new MicroserviceRouteTargetConfiguration(target.ServiceId),
            ExtensionHandlerRouteTarget target => new ExtensionHandlerRouteTargetConfiguration(target.HandlerId),
            _ => throw new InvalidOperationException("Unsupported extension route target.")
        },
        route.Priority,
        current?.Forwarding ?? new ForwardingConfiguration(ForwardingMode.Preserve, null),
        current?.RequestHeaderRewrites ?? ImmutableArray<HeaderRewriteConfiguration>.Empty,
        current?.ResponseHeaderRewrites ?? ImmutableArray<HeaderRewriteConfiguration>.Empty,
        current?.MetadataJson ?? string.Empty,
        current?.CreatedAt ?? now,
        now,
        version,
        current?.ClientIpRatePolicy,
        current?.MaxRequestBodyBytes,
        current?.MaxRequestHeaderBytes,
        current?.MaxConcurrentRequests,
        current?.RequestReadTimeout,
        current?.ProxyRetries);

    private static ServiceConfiguration ToService(
        ExtensionServiceConfiguration service,
        ServiceConfiguration? current,
        DateTimeOffset now,
        long version) => new(
        service.Id,
        service.Enabled,
        service.FileName,
        service.ArgumentList,
        service.WorkingDirectory,
        current?.Environment ?? ImmutableDictionary<string, string>.Empty,
        service.StartMode,
        service.RestartPolicy,
        service.HealthCheck,
        current?.CreatedAt ?? now,
        now,
        version);

    private sealed class FakeFullConfigurationApi(FakeHostBridge owner) : IExtensionFullConfigurationApi
    {
        public ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConfigurationReadResult<HostConfigurationSnapshot>.Success(owner.ReadSnapshot()));
        }

        public ValueTask<ConfigurationWriteResult> ReplaceAsync(long expectedVersion, ConfigurationChangeSet changes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(owner.ReplaceSnapshot(expectedVersion, changes));
        }
    }

    private sealed class FakeStatusSink : IExtensionStatusSink
    {
        public ConcurrentQueue<ExtensionStatus> Statuses { get; } = new();
        public void Report(ExtensionStatus status) => Statuses.Enqueue(status);
    }

    private sealed class FakeLogger : IExtensionLogger
    {
        public void Report(ExtensionLogLevel level, string code) { }
    }

    private sealed class FakeLogWriter : IExtensionLogWriter
    {
        public void WriteText(ExtensionLogLevel level, string text) { }
    }

    private sealed class FakeSupervisorApi(FakeHostBridge owner) : IExtensionSupervisorApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Success(owner.ReadSupervisorSnapshots()));
        }

        public ValueTask<ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>> GetAsync(Guid serviceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = owner.ReadSupervisorSnapshots()
                .Where(value => value.ServiceId == serviceId)
                .Select(static value => (ExtensionServiceRuntimeSnapshot?)value)
                .FirstOrDefault();
            return ValueTask.FromResult(ConfigurationReadResult<ExtensionServiceRuntimeSnapshot?>.Success(snapshot));
        }
    }

    private sealed class FakeSettingsReader(FakeHostBridge owner) : IExtensionSettingsReader
    {
        public ExtensionSettingsConfiguration Settings => owner.ReadExtensionSettings();
    }

    private sealed class FakeConfigurationApi(FakeHostBridge owner) : IExtensionConfigurationApi
    {
        public HostApiVersion ApiVersion => new(1, 3, 0);

        public ValueTask<ConfigurationReadResult<ExtensionConfigurationSnapshot>> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConfigurationReadResult<ExtensionConfigurationSnapshot>.Success(owner.ReadExtensionSnapshot()));
        }

        public ValueTask<ConfigurationWriteResult> ApplyAsync(long expectedVersion, ExtensionConfigurationChangeSet changes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(owner.ApplyExtensionChanges(expectedVersion, changes));
        }

        public ValueTask<ConfigurationReadResult<ExtensionSettingsConfiguration>> ReadSettingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConfigurationReadResult<ExtensionSettingsConfiguration>.Success(owner.ReadExtensionSettings()));
        }

        public ValueTask<ConfigurationWriteResult> WriteSettingsAsync(long expectedVersion, ExtensionSettingsConfiguration settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(owner.WriteExtensionSettings(expectedVersion, settings));
        }
    }

    private sealed class FakeRouteApi : IExtensionRouteApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>> ReadOwnedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionRouteConfiguration>>.Success(ImmutableArray<ExtensionRouteConfiguration>.Empty));

        public ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionRouteConfiguration route, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));

        public ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid routeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));
    }

    private sealed class FakeServiceApi : IExtensionServiceApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>> ReadOwnedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionServiceConfiguration>>.Success(ImmutableArray<ExtensionServiceConfiguration>.Empty));

        public ValueTask<ConfigurationWriteResult> UpsertAsync(long expectedVersion, ExtensionServiceConfiguration service, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));

        public ValueTask<ConfigurationWriteResult> RemoveAsync(long expectedVersion, Guid serviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConfigurationWriteResult.Success(expectedVersion + 1));

        public ValueTask<ExtensionServiceOperationResult> StartAsync(Guid serviceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ExtensionServiceOperationResult> StopAsync(Guid serviceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ExtensionServiceOperationResult> RestartAsync(Guid serviceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEndpointApi : IExtensionEndpointApi
    {
        public ImmutableArray<ExtensionEndpointLease> Current => ImmutableArray<ExtensionEndpointLease>.Empty;

        public ValueTask<ExtensionEndpointLease?> ResolveAsync(Guid serviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ExtensionEndpointLease?>(new ExtensionEndpointLease(serviceId, 0, DateTimeOffset.UtcNow));
    }

    private sealed class FakeLifecycleApi : IExtensionLifecycleApi
    {
        public ExtensionLifecycleStatus Status =>
            new(ControllerOptions.ExtensionId, "1.0.0", ExtensionLoadState.Loaded, 0, false, 0, 0, 0, 0, default);

        public ValueTask<ExtensionLifecycleOperationResult> RequestReloadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ExtensionLifecycleOperationResult> RequestUnloadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeContractRegistry : IExtensionContractRegistry
    {
        public bool TryExport<TContract>(string contractId, TContract implementation) where TContract : class => false;
        public bool TryImport<TContract>(string contractId, out TContract? contract) where TContract : class
        {
            contract = null;
            return false;
        }
    }

    private sealed class FakeTaskScheduler : IExtensionTaskScheduler
    {
        public ValueTask<bool> StartAsync(string taskName, Func<CancellationToken, ValueTask> callback) =>
            ValueTask.FromResult(false);
    }

    private sealed class FakeEventPublisher : IExtensionEventPublisher
    {
        public bool TryPublish(ExtensionEvent @event) => false;
        public bool TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback) => false;
    }

    private sealed class FakeRouteEvents : IExtensionRouteEvents
    {
        public bool TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback) => false;
        public bool TryRegisterHook(ExtensionRouteEventStage stage, Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> callback) => false;
    }
}

/// <summary>Captures the registered management handler so tests can drive the HostRoute transport in-process.</summary>
public sealed class FakeExtensionRegistration : IExtensionRegistration
{
    public IExtensionHandler? Handler { get; private set; }

    public bool TryRegisterHandler(IExtensionHandler handler)
    {
        Handler = handler;
        return true;
    }

    public bool TryRegisterFallback(IExtensionFallback fallback) => false;

    public bool TryUnregisterHandler(string handlerId)
    {
        if (Handler is null || !string.Equals(Handler.HandlerId, handlerId, StringComparison.Ordinal))
        {
            return false;
        }

        Handler = null;
        return true;
    }

    public bool TryUnregisterFallback() => false;
}

/// <summary>Start context handed to the controller entrypoint under test.</summary>
public sealed class FakeExtensionStartContext(IExtensionHostBridge host, IExtensionRegistration registration) : IExtensionStartContext
{
    public IExtensionHostBridge Host { get; } = host;
    public IExtensionRegistration Registration { get; } = registration;
    public IExtensionContractRegistry Contracts => Host.Contracts;
    public bool Reloading => false;
}

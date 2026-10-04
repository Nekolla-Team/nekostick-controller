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
public sealed class FakeHostBridge : IExtensionHostBridge14
{
    private readonly object _sync = new();
    private readonly HostApiVersion _apiVersion;
    private HostConfigurationSnapshot _snapshot;
    private ImmutableArray<ExtensionServiceRuntimeSnapshot> _supervisorSnapshots = ImmutableArray<ExtensionServiceRuntimeSnapshot>.Empty;
    private ExtensionHostInfoSnapshot _hostInfo = ExtensionHostInfoSnapshot.Unavailable;
    private ImmutableArray<ExtensionScanSkip> _refreshSkips = ImmutableArray<ExtensionScanSkip>.Empty;
    private ConfigurationError? _nextReplaceFailure;
    private ConfigurationError? _nextApplyFailure;
    private ConfigurationError? _nextManagementFailure;
    private readonly HashSet<string> _runningExtensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionStatus> _reportedStatusesByExtension = new(StringComparer.Ordinal);
    private readonly AsyncLocal<bool> _inRouteCallback = new();
    private readonly ConcurrentQueue<string> _scheduledReloads = new();
    private readonly ConcurrentQueue<string> _synchronousReloads = new();
    private bool _refuseReloadSchedule;
    private Exception? _readFailure;

    public FakeHostBridge(HostConfigurationSnapshot initialSnapshot, HostApiVersion? apiVersion = null)
    {
        _snapshot = initialSnapshot;
        _apiVersion = apiVersion ?? new HostApiVersion(1, 3, 1);
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
        Management = new FakeManagementApi(this);
        Dependencies = new FakeDependencyApi();
        ServiceOutput = new FakeServiceOutputApi();
        ServiceRuntimeState = new FakeServiceRuntimeStateApi();
    }
    public HostApiVersion ApiVersion => _apiVersion;
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

    public IExtensionDependencyApi Dependencies { get; }
    /// <summary>Gets the API 1.4 service-output capability.</summary>
    public IExtensionServiceOutputApi ServiceOutput { get; }
    /// <summary>Gets the programmable fake behind the API 1.4 service-output capability.</summary>
    public FakeServiceOutputApi ServiceOutputFake => (FakeServiceOutputApi)ServiceOutput;
    /// <summary>Gets the API 1.4 node-local runtime-state subscription capability.</summary>
    public IExtensionServiceRuntimeStateApi ServiceRuntimeState { get; }
    /// <summary>Gets the programmable fake behind the runtime-state capability.</summary>
    public FakeServiceRuntimeStateApi ServiceRuntimeStateFake => (FakeServiceRuntimeStateApi)ServiceRuntimeState;
    /// <summary>Gets the latest host information snapshot returned by the 1.3.3 bridge.</summary>
    public ExtensionHostInfoSnapshot HostInfo
    {
        get
        {
            lock (_sync)
            {
                return _hostInfo;
            }
        }
    }
 
    /// <summary>Sets the host information snapshot returned by the fake bridge.</summary>
    /// <param name="snapshot">The safe host information snapshot to return.</param>
    public void SetHostInfo(ExtensionHostInfoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            _hostInfo = snapshot;
        }
    }

    /// <summary>Sets the scan skips reported by the next refresh.</summary>
    /// <param name="skips">The skipped directories with their failure categories.</param>
    public void SetRefreshSkips(ImmutableArray<ExtensionScanSkip> skips)
    {
        lock (_sync)
        {
            _refreshSkips = skips.IsDefault ? ImmutableArray<ExtensionScanSkip>.Empty : skips;
        }
    }

    private ImmutableArray<ExtensionScanSkip> ReadRefreshSkips()
    {
        lock (_sync)
        {
            return _refreshSkips;
        }
    }
    public IExtensionManagementApi Management { get; }

    public ConcurrentQueue<ExtensionStatus> ReportedStatuses => ((FakeStatusSink)Status).Statuses;
    public string? LastLogText => ((FakeLogWriter)LogWriter).LastText;
    /// <summary>Gets every text written to the host log, in order.</summary>
    public IReadOnlyCollection<(ExtensionLogLevel Level, string Text)> LogEntries => ((FakeLogWriter)LogWriter).Entries;

    /// <summary>Routes every subsequent full-configuration read to the given failure.</summary>
    public void FailReads(Exception exception) => _readFailure = exception;

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
    public void FailNextReplace(ConfigurationErrorCode code, string? message = null)
    {
        lock (_sync)
        {
            _nextReplaceFailure = CreateConfigurationError(code, message);
        }
    }
    /// <summary>
    /// Makes the next <see cref="IExtensionConfigurationApi.ApplyAsync"/> call fail once with
    /// the supplied error without altering the stored snapshot.
    /// </summary>
    public void FailNextApply(ConfigurationErrorCode code, string? message = null)
    {
        lock (_sync)
        {
            _nextApplyFailure = CreateConfigurationError(code, message);
        }
    }
    /// <summary>Makes the next <see cref="IExtensionManagementApi"/> call fail once with the supplied error.</summary>
    public void FailNextManagement(ConfigurationErrorCode code, string? message = null)
    {
        lock (_sync)
        {
            _nextManagementFailure = CreateConfigurationError(code, message);
        }
    }
    private static ConfigurationError CreateConfigurationError(ConfigurationErrorCode code, string? message) =>
        new(code, message ?? (code switch
        {
            ConfigurationErrorCode.Validation => "The configuration change failed validation.",
            ConfigurationErrorCode.ConcurrencyConflict => "The expected configuration version is stale.",
            ConfigurationErrorCode.NotFound => "The requested configuration item was not found.",
            ConfigurationErrorCode.Unsupported => "The requested configuration operation is not supported.",
            ConfigurationErrorCode.StorageUnavailable => "The configuration store is unavailable.",
            ConfigurationErrorCode.NoSettings => "The extension settings document was not found.",
            _ => "The configuration operation failed."
        }));

    /// <summary>Marks an extension as having a running loaded generation for management listings.</summary>
    public void SetExtensionRunning(string extensionId, bool running)
    {
        lock (_sync)
        {
            if (running) _runningExtensions.Add(extensionId);
            else _runningExtensions.Remove(extensionId);
        }
    }
    /// <summary>Sets the latest extension-reported status surfaced by management listings, mirroring the API 1.4 host.</summary>
    public void SetReportedStatus(string extensionId, ExtensionStatusKind kind, string code)
    {
        lock (_sync)
        {
            _reportedStatusesByExtension[extensionId] = new ExtensionStatus(kind, code);
        }
    }
    /// <summary>Clears every extension-reported status so a test asserts only its own setup.</summary>
    public void ClearReportedStatuses()
    {
        lock (_sync)
        {
            _reportedStatusesByExtension.Clear();
        }
    }

    /// <summary>Gets the extension identifiers passed to the callback-safe scheduling entry point.</summary>
    public IReadOnlyCollection<string> ScheduledReloads => _scheduledReloads.ToArray();

    /// <summary>Gets the extension identifiers passed to the synchronous reload operation.</summary>
    public IReadOnlyCollection<string> SynchronousReloads => _synchronousReloads.ToArray();

    /// <summary>Clears the recorded reload observations so one test asserts only its own calls.</summary>
    public void ClearReloadObservations()
    {
        _scheduledReloads.Clear();
        _synchronousReloads.Clear();
    }

    /// <summary>Sets whether the callback-safe reload scheduling entry point declines, as on a read-only node.</summary>
    public void SetReloadScheduleRefused(bool refused)
    {
        lock (_sync)
        {
            _refuseReloadSchedule = refused;
        }
    }

    /// <summary>
    /// Runs one route handler invocation inside the callback scope the Host establishes for it.
    /// </summary>
    /// <remarks>
    /// The real Host vetoes the synchronous reload from these callbacks and offers scheduling
    /// instead, so the fake reproduces that veto to keep the controller honest on this transport.
    /// </remarks>
    public async ValueTask<T> InRouteCallbackAsync<T>(Func<ValueTask<T>> invoke)
    {
        var prior = _inRouteCallback.Value;
        _inRouteCallback.Value = true;
        try
        {
            return await invoke().ConfigureAwait(false);
        }
        finally
        {
            _inRouteCallback.Value = prior;
        }
    }

    private bool InRouteCallback => _inRouteCallback.Value;

    private bool ReadReloadScheduleRefused()
    {
        lock (_sync)
        {
            return _refuseReloadSchedule;
        }
    }

    private void RecordScheduledReload(string extensionId) => _scheduledReloads.Enqueue(extensionId);

    private void RecordSynchronousReload(string extensionId) => _synchronousReloads.Enqueue(extensionId);

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
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict, "The expected configuration version does not match the current version."));
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

    private ConfigurationError? ConsumeManagementFailure()
    {
        lock (_sync)
        {
            var failure = _nextManagementFailure;
            _nextManagementFailure = null;
            return failure;
        }
    }

    private ImmutableArray<ExtensionManagementEntry> ReadManagementEntries()
    {
        lock (_sync)
        {
            return _snapshot.ExtensionRecords.Select(record =>
            {
                var hasStatus = _reportedStatusesByExtension.TryGetValue(record.ExtensionId, out var status);
                return new ExtensionManagementEntry(
                    record.ExtensionId, record.Version, record.LoadState, record.CreatedAt, record.UpdatedAt,
                    record.RecordVersion, _runningExtensions.Contains(record.ExtensionId), record.Version, record.ContentHash,
                    hasStatus ? status.Kind : null,
                    hasStatus ? status.Code : null);
            }).ToImmutableArray();
        }
    }

    private long SetExtensionLoadState(string extensionId, ExtensionLoadState loadState, bool running)
    {
        lock (_sync)
        {
            var record = _snapshot.ExtensionRecords.First(candidate => string.Equals(candidate.ExtensionId, extensionId, StringComparison.Ordinal));
            var updated = new ExtensionRecordConfiguration(record.ExtensionId, record.Version, loadState, record.CreatedAt, DateTimeOffset.UtcNow, record.RecordVersion + 1, record.ContentHash);
            _snapshot = new HostConfigurationSnapshot(_snapshot.Version + 1, _snapshot.GlobalSettings, _snapshot.Routes, _snapshot.Services,
                _snapshot.ExtensionRecords.Replace(record, updated), _snapshot.ExtensionSettings);
            if (running) _runningExtensions.Add(extensionId);
            else _runningExtensions.Remove(extensionId);
            return _snapshot.Version;
        }
    }

    private long RemoveExtensionRecord(string extensionId)
    {
        lock (_sync)
        {
            var record = _snapshot.ExtensionRecords.First(candidate => string.Equals(candidate.ExtensionId, extensionId, StringComparison.Ordinal));
            _snapshot = new HostConfigurationSnapshot(_snapshot.Version + 1, _snapshot.GlobalSettings, _snapshot.Routes, _snapshot.Services,
                _snapshot.ExtensionRecords.Remove(record),
                _snapshot.ExtensionSettings.Where(item => !string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal)).ToImmutableArray());
            _runningExtensions.Remove(extensionId);
            return _snapshot.Version;
        }
    }

    private ImmutableArray<ExtensionServiceRuntimeSnapshot> ReadSupervisorSnapshots()
    {
        lock (_sync)
        {
            return _supervisorSnapshots;
        }
    }

    private void SetSupervisorLifecycle(Guid serviceId, ExtensionServiceLifecycleState lifecycleState)
    {
        lock (_sync)
        {
            var snapshots = _supervisorSnapshots.ToBuilder();
            for (var index = 0; index < snapshots.Count; index++)
            {
                var current = snapshots[index];
                if (current.ServiceId != serviceId) continue;
                snapshots[index] = new ExtensionServiceRuntimeSnapshot(
                    current.ServiceId,
                    current.ProcessId,
                    current.StartedAt,
                    current.Uptime,
                    lifecycleState,
                    current.HealthState,
                    current.ForwardedRequestCount,
                    current.ActiveForwardedRequestCount,
                    current.LastUpdatedAt,
                    current.LastHealthAt,
                    current.OwnerExtensionId);
                _supervisorSnapshots = snapshots.ToImmutable();
                return;
            }
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
            if (_nextApplyFailure is { } failure)
            {
                _nextApplyFailure = null;
                return ConfigurationWriteResult.Failure(failure);
            }
            if (expectedVersion != _snapshot.Version)
            {
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict, "The expected configuration version does not match the current version."));
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
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict, "The extension settings document is missing or its version does not match the expected version."));
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
                return ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.ConcurrencyConflict, "The extension settings document is no longer present in the configuration snapshot."));
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
            if (owner._readFailure is { } failure) throw failure;
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
        private readonly ConcurrentQueue<(ExtensionLogLevel Level, string Text)> _entries = new();

        public string? LastText { get; private set; }

        public IReadOnlyCollection<(ExtensionLogLevel Level, string Text)> Entries => _entries;

        public void WriteText(ExtensionLogLevel level, string text)
        {
            LastText = text;
            _entries.Enqueue((level, text));
        }
    }

    private sealed class FakeManagementApi(FakeHostBridge owner) : IExtensionManagementApi
    {
        public HostApiVersion ApiVersion => owner.ApiVersion;

        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.ConsumeManagementFailure() is { } failure)
                return ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>.Failure(failure));
            return ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionManagementEntry>>.Success(owner.ReadManagementEntries()));
        }

        public ValueTask<ConfigurationWriteResult> EnableAsync(string extensionId, CancellationToken cancellationToken) =>
            Write(extensionId, ExtensionLoadState.Loaded, running: true, cancellationToken);

        public ValueTask<ConfigurationWriteResult> DisableAsync(string extensionId, CancellationToken cancellationToken) =>
            Write(extensionId, ExtensionLoadState.Disabled, running: false, cancellationToken);

        public ValueTask<ConfigurationWriteResult> ReloadAsync(string extensionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.RecordSynchronousReload(extensionId);
            if (owner.InRouteCallback)
            {
                // The real Host vetoes the synchronous reload inside a route callback.
                return ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Unsupported, "Synchronous reload is not supported from a route callback.")));
            }

            if (owner.ConsumeManagementFailure() is { } failure) return ValueTask.FromResult(ConfigurationWriteResult.Failure(failure));
            var entry = owner.ReadManagementEntries().FirstOrDefault(candidate => candidate.ExtensionId == extensionId);
            if (entry is null) return ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound, "The extension record was not found.")));
            if (entry.LoadState != ExtensionLoadState.Loaded)
                return ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.Validation, "The extension must be loaded before it can be reloaded.")));
            return ValueTask.FromResult(ConfigurationWriteResult.Success(owner.ReadSnapshot().Version));
        }

        public ExtensionReloadScheduleResult ReloadSoon(string extensionId)
        {
            if (owner.ReadReloadScheduleRefused()) return ExtensionReloadScheduleResult.Unsupported;
            owner.RecordScheduledReload(extensionId);
            return ExtensionReloadScheduleResult.Success;
        }

        public ValueTask<ConfigurationWriteResult> DeleteRecordAsync(string extensionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.ConsumeManagementFailure() is { } failure) return ValueTask.FromResult(ConfigurationWriteResult.Failure(failure));
            if (owner.ReadManagementEntries().All(candidate => candidate.ExtensionId != extensionId))
                return ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound, "The extension record was not found.")));
            return ValueTask.FromResult(ConfigurationWriteResult.Success(owner.RemoveExtensionRecord(extensionId)));
        }

        public ValueTask<ConfigurationReadResult<ExtensionRefreshSummary>> RequestRefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.ConsumeManagementFailure() is { } failure)
                return ValueTask.FromResult(ConfigurationReadResult<ExtensionRefreshSummary>.Failure(failure));
            return ValueTask.FromResult(ConfigurationReadResult<ExtensionRefreshSummary>.Success(
                new ExtensionRefreshSummary(
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty,
                    owner.ReadRefreshSkips())));
        }

        private ValueTask<ConfigurationWriteResult> Write(string extensionId, ExtensionLoadState loadState, bool running, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.ConsumeManagementFailure() is { } failure) return ValueTask.FromResult(ConfigurationWriteResult.Failure(failure));
            if (owner.ReadManagementEntries().All(candidate => candidate.ExtensionId != extensionId))
                return ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound, "The extension record was not found.")));
            return ValueTask.FromResult(ConfigurationWriteResult.Success(owner.SetExtensionLoadState(extensionId, loadState, running)));
        }
    }

    private sealed class FakeSupervisorApi(FakeHostBridge owner) : IExtensionSupervisorApi
    {
        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Success(owner.ReadSupervisorSnapshots()));
        }

        public ValueTask<ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>> ReadForExtensionAsync(string extensionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(extensionId))
                return ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Failure(new ConfigurationError(ConfigurationErrorCode.Validation, "An extension identifier is required to read service runtime snapshots.")));
            return ValueTask.FromResult(ConfigurationReadResult<ImmutableArray<ExtensionServiceRuntimeSnapshot>>.Success(
                owner.ReadSupervisorSnapshots()
                    .Where(snapshot => string.Equals(snapshot.OwnerExtensionId, extensionId, StringComparison.Ordinal))
                    .ToImmutableArray()));
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
        public ValueTask<ConfigurationWriteResult> ResumeAsync(Guid serviceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.ReadSnapshot().Services.Any(service => service.Id == serviceId))
            {
                return ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound, "The service was not found.")));
            }

            var snapshot = owner.ReadSupervisorSnapshots().FirstOrDefault(value => value.ServiceId == serviceId);
            if (snapshot is { LifecycleState: ExtensionServiceLifecycleState.Waiting })
            {
                owner.SetSupervisorLifecycle(serviceId, ExtensionServiceLifecycleState.Running);
                return ValueTask.FromResult(ConfigurationWriteResult.Success());
            }

            return ValueTask.FromResult(ConfigurationWriteResult.NoOp());
        }

        public ValueTask<ConfigurationWriteResult> RestartAsync(Guid serviceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return owner.ReadSnapshot().Services.Any(service => service.Id == serviceId)
                ? ValueTask.FromResult(ConfigurationWriteResult.Success())
                : ValueTask.FromResult(ConfigurationWriteResult.Failure(new ConfigurationError(ConfigurationErrorCode.NotFound, "The service was not found.")));
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

        public ValueTask<ExtensionEndpointResolutionResult> ResolveAsync(Guid serviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ExtensionEndpointResolutionResult.Success(
                new ExtensionEndpointLease(serviceId, 1, DateTimeOffset.UtcNow)));
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
        public ExtensionContractExportResult TryExport<TContract>(string contractId, TContract implementation) where TContract : class =>
            ExtensionContractExportResult.Failure(
                ExtensionContractExportFailureCode.NotDeclared,
                new ExtensionErrorDetail($"Contract '{contractId}' is not declared for export."));

        public ExtensionContractImportResult<TContract> TryImport<TContract>(string contractId) where TContract : class =>
            ExtensionContractImportResult<TContract>.Failure(
                ExtensionContractImportFailureCode.NotDeclared,
                new ExtensionErrorDetail($"Contract '{contractId}' is not declared for import."));
    }

    private sealed class FakeTaskScheduler : IExtensionTaskScheduler
    {
        public ValueTask<ExtensionTaskStartResult> StartAsync(string taskName, Func<CancellationToken, ValueTask> callback) =>
            ValueTask.FromResult(ExtensionTaskStartResult.Failure(
                ExtensionTaskStartFailureCode.Stopped,
                new ExtensionErrorDetail("The task scheduler is stopped.")));
    }

    private sealed class FakeEventPublisher : IExtensionEventPublisher
    {
        public ExtensionEventPublishResult TryPublish(ExtensionEvent @event) =>
            ExtensionEventPublishResult.Failure(
                ExtensionEventPublishFailureCode.Unavailable,
                new ExtensionErrorDetail("The event queue is unavailable."));

        public ExtensionEventSubscribeResult TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback) =>
            ExtensionEventSubscribeResult.Failure(
                ExtensionEventSubscribeFailureCode.Unavailable,
                new ExtensionErrorDetail("The event queue is unavailable."));
    }

    private sealed class FakeRouteEvents : IExtensionRouteEvents
    {
        public ExtensionRouteRegistrationResult TrySubscribe(Func<ExtensionEvent, CancellationToken, ValueTask> callback) =>
            ExtensionRouteRegistrationResult.Unsupported;

        public ExtensionRouteRegistrationResult TryRegisterHook(ExtensionRouteEventStage stage, Func<ExtensionRouteHookContext, CancellationToken, ValueTask<ExtensionRouteHookResult>> callback) =>
            ExtensionRouteRegistrationResult.Unsupported;
    }

    private sealed class FakeDependencyApi : IExtensionDependencyApi
    {
        public IExtensionDependencyContext GetDependencyContext(string extensionId) => new FakeDependencyContext(extensionId);
    }

    private sealed class FakeDependencyContext(string extensionId) : IExtensionDependencyContext
    {
        public string ExtensionId { get; } = extensionId;
        public ExtensionDependencyState State => ExtensionDependencyState.NotDeclared;
        public bool IsOptional => false;
        public string VersionRange => string.Empty;
        public string? InstalledVersion => null;

        public ExtensionContractImportResult<TContract> TryImport<TContract>(string contractId) where TContract : class
        {
            var installedVersion = InstalledVersion is null ? "not installed" : $"version '{InstalledVersion}'";
            var range = string.IsNullOrWhiteSpace(VersionRange)
                ? "no declared version range"
                : $"required range '{VersionRange}'";
            return ExtensionContractImportResult<TContract>.Failure(
                ExtensionContractImportFailureCode.DependencyUnsatisfied,
                new ExtensionErrorDetail(
                    $"Dependency '{ExtensionId}' has state {State} with {installedVersion}; {range} is not satisfied, so contract '{contractId}' cannot be imported."));
        }
    }

    /// <summary>Programmable API 1.4 service-output capability used by the WebSocket and SSE endpoint tests.</summary>
    public sealed class FakeServiceOutputApi : IExtensionServiceOutputApi
    {
        private readonly ConcurrentQueue<(Guid ServiceId, ExtensionServiceOutputStream Stream)> _opens = new();
        private readonly ConcurrentQueue<(Guid ServiceId, long? SinceSequence)> _subscriptions = new();
        private readonly ConcurrentDictionary<Guid, ExtensionServiceLogCode> _rejections = new();
        private readonly object _sinkGate = new();
        private readonly Dictionary<Guid, List<IExtensionServiceLogSink>> _sinks = new();
        private volatile Func<Guid, ExtensionServiceOutputStream, ExtensionServiceOutputStreamResult>? _openHandler;

        /// <summary>Gets every legacy open request observed so far, in order.</summary>
        public IReadOnlyCollection<(Guid ServiceId, ExtensionServiceOutputStream Stream)> Opens => _opens.ToArray();

        /// <summary>Gets every log-feed subscription request observed so far, in order.</summary>
        public IReadOnlyCollection<(Guid ServiceId, long? SinceSequence)> Subscriptions => _subscriptions.ToArray();

        /// <summary>Programs the result of subsequent legacy open calls; the default rejects with NotFound.</summary>
        public void OnOpen(Func<Guid, ExtensionServiceOutputStream, ExtensionServiceOutputStreamResult> handler) =>
            _openHandler = handler;

        /// <summary>Rejects log-feed subscriptions for one service with the supplied Host code.</summary>
        public void Reject(Guid serviceId, ExtensionServiceLogCode code) => _rejections[serviceId] = code;

        /// <summary>Allows log-feed subscriptions for one service again.</summary>
        public void ClearRejection(Guid serviceId) => _rejections.TryRemove(serviceId, out _);

        /// <summary>Pushes one log entry to every active subscriber for its service.</summary>
        public void Push(ExtensionServiceLogEntry entry)
        {
            IExtensionServiceLogSink[] sinks;
            lock (_sinkGate)
            {
                sinks = _sinks.TryGetValue(entry.ServiceId, out var registered)
                    ? registered.ToArray()
                    : Array.Empty<IExtensionServiceLogSink>();
            }

            foreach (var sink in sinks)
            {
                sink.OnEntry(entry);
            }
        }

        /// <summary>Completes all active log-feed subscribers and unregisters their sinks.</summary>
        public void Complete()
        {
            IExtensionServiceLogSink[] sinks;
            lock (_sinkGate)
            {
                sinks = _sinks.Values.SelectMany(static registered => registered).ToArray();
                _sinks.Clear();
            }

            foreach (var sink in sinks)
            {
                sink.OnCompleted();
            }
        }

        public ValueTask<ExtensionServiceOutputStreamResult> OpenStreamAsync(
            Guid serviceId,
            ExtensionServiceOutputStream stream,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _opens.Enqueue((serviceId, stream));
            return ValueTask.FromResult(
                _openHandler?.Invoke(serviceId, stream) ??
                new ExtensionServiceOutputStreamResult(
                    false,
                    ExtensionServiceOutputCode.NotFound,
                    serviceId,
                    null,
                    new ExtensionErrorDetail("The service was not found.")));
        }

        public ValueTask<ExtensionServiceLogSubscriptionResult> SubscribeAsync(
            Guid serviceId,
            IExtensionServiceLogSink sink,
            long? sinceSequence = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _subscriptions.Enqueue((serviceId, sinceSequence));
            if (_rejections.TryGetValue(serviceId, out var rejection))
            {
                var detail = rejection switch
                {
                    ExtensionServiceLogCode.NotFound => new ExtensionErrorDetail("The service was not found."),
                    ExtensionServiceLogCode.InvalidArgument or ExtensionServiceLogCode.InvalidCursor =>
                        new ExtensionErrorDetail("The 'sinceSequence' parameter must be a non-negative log sequence number."),
                    ExtensionServiceLogCode.Unsupported =>
                        new ExtensionErrorDetail("The host does not support the requested service log feed."),
                    _ => new ExtensionErrorDetail("The host could not open a service log feed.")
                };
                return ValueTask.FromResult(new ExtensionServiceLogSubscriptionResult(
                    false,
                    rejection,
                    serviceId,
                    null,
                    detail));
            }

            var subscription = new Subscription(this, serviceId, sink);
            lock (_sinkGate)
            {
                if (!_sinks.TryGetValue(serviceId, out var registered))
                {
                    registered = [];
                    _sinks.Add(serviceId, registered);
                }

                registered.Add(sink);
            }

            return ValueTask.FromResult(new ExtensionServiceLogSubscriptionResult(
                true,
                ExtensionServiceLogCode.Subscribed,
                serviceId,
                subscription,
                null));
        }

        private void Unregister(Guid serviceId, IExtensionServiceLogSink sink)
        {
            lock (_sinkGate)
            {
                if (!_sinks.TryGetValue(serviceId, out var registered))
                {
                    return;
                }

                registered.Remove(sink);
                if (registered.Count == 0)
                {
                    _sinks.Remove(serviceId);
                }
            }
        }

        private sealed class Subscription(FakeServiceOutputApi owner, Guid serviceId, IExtensionServiceLogSink sink)
            : IExtensionServiceLogSubscription
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.Unregister(serviceId, sink);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Programmable API 1.4 runtime-state capability used by the runtime-feed endpoint tests.</summary>
    public sealed class FakeServiceRuntimeStateApi : IExtensionServiceRuntimeStateApi
    {
        private readonly object _gate = new();
        private readonly List<IExtensionServiceRuntimeStateSink> _sinks = [];
        private ExtensionServiceRuntimeStateSubscriptionCode? _rejection;
        private Exception? _subscribeFailure;

        /// <summary>Rejects runtime-state subscriptions with the supplied Host code.</summary>
        public void Reject(ExtensionServiceRuntimeStateSubscriptionCode code)
        {
            lock (_gate)
            {
                _subscribeFailure = null;
                _rejection = code;
            }
        }

        /// <summary>Makes subsequent runtime-state subscriptions throw the supplied exception.</summary>
        public void ThrowOnSubscribe(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            lock (_gate)
            {
                _rejection = null;
                _subscribeFailure = exception;
            }
        }

        /// <summary>Pushes one runtime-state change to every active subscriber.</summary>
        public void Push(ExtensionServiceRuntimeStateChange change)
        {
            IExtensionServiceRuntimeStateSink[] sinks;
            lock (_gate)
            {
                sinks = _sinks.ToArray();
            }

            foreach (var sink in sinks)
            {
                sink.OnStateChanged(change);
            }
        }

        /// <summary>Completes the fake subscription set and unregisters all sinks.</summary>
        public void Complete()
        {
            lock (_gate)
            {
                _sinks.Clear();
            }
        }

        public ValueTask<ExtensionServiceRuntimeStateSubscriptionResult> SubscribeStatesAsync(
            IExtensionServiceRuntimeStateSink sink,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_subscribeFailure is { } failure)
                {
                    throw failure;
                }

                if (_rejection is { } rejection)
                {
                    var detail = rejection == ExtensionServiceRuntimeStateSubscriptionCode.Unsupported
                        ? new ExtensionErrorDetail("The host does not support service runtime-state subscriptions.")
                        : new ExtensionErrorDetail($"The host rejected the service runtime-state subscription with code '{rejection}'.");
                    return ValueTask.FromResult(new ExtensionServiceRuntimeStateSubscriptionResult(false, rejection, null, detail));
                }

                _sinks.Add(sink);
            }

            var subscription = new Subscription(this, sink);
            return ValueTask.FromResult(new ExtensionServiceRuntimeStateSubscriptionResult(
                true,
                ExtensionServiceRuntimeStateSubscriptionCode.Subscribed,
                subscription,
                null));
        }

        private void Unregister(IExtensionServiceRuntimeStateSink sink)
        {
            lock (_gate)
            {
                _sinks.Remove(sink);
            }
        }

        private sealed class Subscription(FakeServiceRuntimeStateApi owner, IExtensionServiceRuntimeStateSink sink)
            : IExtensionServiceRuntimeStateSubscription
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.Unregister(sink);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

/// <summary>Captures buffered and streaming management handlers for in-process HostRoute tests.</summary>
public sealed class FakeExtensionRegistration : IExtensionRegistration
{
    public IExtensionHandler? Handler { get; private set; }
    public IExtensionStreamingHandler? StreamingHandler { get; private set; }

    public ExtensionRegistrationResult TryRegisterHandler(IExtensionHandler handler)
    {
        Handler = handler;
        return ExtensionRegistrationResult.Success;
    }

    public ExtensionRegistrationResult TryRegisterStreamingHandler(IExtensionStreamingHandler handler)
    {
        StreamingHandler = handler;
        return ExtensionRegistrationResult.Success;
    }

    public ExtensionRegistrationResult TryRegisterFallback(IExtensionFallback fallback) => ExtensionRegistrationResult.Unsupported;

    public ExtensionRegistrationResult TryUnregisterHandler(string handlerId)
    {
        if (Handler is not null && string.Equals(Handler.HandlerId, handlerId, StringComparison.Ordinal))
        {
            Handler = null;
            return ExtensionRegistrationResult.Success;
        }

        if (StreamingHandler is not null && string.Equals(StreamingHandler.HandlerId, handlerId, StringComparison.Ordinal))
        {
            StreamingHandler = null;
            return ExtensionRegistrationResult.Success;
        }

        return ExtensionRegistrationResult.Failure(
            ExtensionRegistrationFailureCode.NotFound,
            new ExtensionErrorDetail("The handler is not registered."));
    }

    public ExtensionRegistrationResult TryUnregisterFallback() => ExtensionRegistrationResult.Failure(
        ExtensionRegistrationFailureCode.NotFound,
        new ExtensionErrorDetail("No fallback is registered."));
}

/// <summary>Start context handed to the controller entrypoint under test.</summary>
public sealed class FakeExtensionStartContext(
    IExtensionHostBridge host,
    IExtensionRegistration registration,
    bool reloading = false) : IExtensionStartContext
{
    public IExtensionHostBridge Host { get; } = host;
    public IExtensionRegistration Registration { get; } = registration;
    public IExtensionContractRegistry Contracts => Host.Contracts;
    public bool Reloading { get; } = reloading;
}

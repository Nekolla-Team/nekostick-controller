using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Maps Host contract records to stable controller-owned DTOs.</summary>
internal static class ControllerContractMapper
{

    internal static ControllerGlobalSettingsReadDto ToRead(GlobalSettingsConfiguration source) => new()
    {
        Version = source.Version, AutoPortRangeStart = source.AutoPortRangeStart, AutoPortRangeEnd = source.AutoPortRangeEnd,
        MaxRequestBodyBytes = source.MaxRequestBodyBytes, MaxRequestHeaderBytes = source.MaxRequestHeaderBytes,
        MaxConcurrentRequests = source.MaxConcurrentRequests, RequestReadTimeoutMs = source.RequestReadTimeout.Ticks / TimeSpan.TicksPerMillisecond,
        ConfigurationPollIntervalMs = source.ConfigurationPollInterval.Ticks / TimeSpan.TicksPerMillisecond,
        TrustedProxyCidrs = source.TrustedProxyCidrs, ProxyTimeouts = ToRead(source.ProxyTimeouts),
        ClientIpRatePolicy = source.ClientIpRatePolicy is null ? null : ToRead(source.ClientIpRatePolicy), ProxyRetries = ToRead(source.ProxyRetries)
    };

    internal static ControllerRouteReadDto ToRead(RouteConfiguration source) => new()
    {
        Id = source.Id, Enabled = source.Enabled, Matcher = ToRead(source.Matcher), Target = ToRead(source.Target), Priority = source.Priority,
        Forwarding = new ControllerForwardingDto { Mode = (ControllerForwardingMode)source.Forwarding.Mode, ReplaceTemplate = source.Forwarding.ReplaceTemplate },
        RequestHeaderRewrites = source.RequestHeaderRewrites.Select(ToRead).ToImmutableArray(), ResponseHeaderRewrites = source.ResponseHeaderRewrites.Select(ToRead).ToImmutableArray(),
        MetadataJson = source.MetadataJson, CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, Version = source.Version,
        ClientIpRatePolicy = source.ClientIpRatePolicy is null ? null : ToRead(source.ClientIpRatePolicy), MaxRequestBodyBytes = source.MaxRequestBodyBytes,
        MaxRequestHeaderBytes = source.MaxRequestHeaderBytes, MaxConcurrentRequests = source.MaxConcurrentRequests,
        RequestReadTimeoutMs = source.RequestReadTimeout?.Ticks / TimeSpan.TicksPerMillisecond, ProxyRetries = source.ProxyRetries is null ? null : ToRead(source.ProxyRetries)
    };

    internal static ControllerServiceReadDto ToRead(ServiceConfiguration source) => new()
    {
        Id = source.Id, Enabled = source.Enabled, FileName = source.FileName, ArgumentList = source.ArgumentList, WorkingDirectory = source.WorkingDirectory,
        StartMode = (ControllerServiceStartMode)source.StartMode, RestartPolicy = (ControllerServiceRestartPolicy)source.RestartPolicy, HealthCheck = ToRead(source.HealthCheck),
        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, Version = source.Version
    };
    internal static ControllerServiceRuntimeReadDto ToRead(ExtensionServiceRuntimeSnapshot source) => new()
    {
        ServiceId = source.ServiceId,
        ProcessId = source.ProcessId,
        StartedAt = source.StartedAt,
        UptimeMs = source.Uptime?.Ticks / TimeSpan.TicksPerMillisecond,
        LifecycleState = (ControllerServiceLifecycleState)source.LifecycleState,
        HealthState = (ControllerServiceHealthState)source.HealthState,
        ForwardedRequestCount = source.ForwardedRequestCount,
        ActiveForwardedRequestCount = source.ActiveForwardedRequestCount,
        LastUpdatedAt = source.LastUpdatedAt,
        LastHealthAt = source.LastHealthAt,
        OwnerExtensionId = source.OwnerExtensionId
    };

    internal static ControllerExtensionRecordReadDto ToRead(ExtensionManagementEntry source) => new()
    {
        ExtensionId = source.ExtensionId, Version = source.InstalledVersion, LoadState = (ControllerExtensionLoadState)source.LoadState,
        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, RecordVersion = source.RecordVersion,
        IsRunning = source.IsRunning, ManifestVersion = source.ManifestVersion
    };

    /// <summary>Maps an API 1.3.3 extension management entry, including its content hash.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ControllerExtensionRecordReadDto ToReadApi133(ExtensionManagementEntry source) => new()
    {
        ExtensionId = source.ExtensionId, Version = source.InstalledVersion, LoadState = (ControllerExtensionLoadState)source.LoadState,
        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, RecordVersion = source.RecordVersion,
        IsRunning = source.IsRunning, ManifestVersion = source.ManifestVersion, ContentHash = source.ContentHash
    };

    internal static ControllerExtensionRefreshReadDto ToRead(ExtensionRefreshSummary source) => new()
    {
        Added = source.Added, VersionUpdated = source.VersionUpdated, Missing = source.Missing
    };

    /// <summary>Maps a refresh summary including skipped scan directories. NoInlining: touches 1.3.4-only members.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ControllerExtensionRefreshReadDto ToReadApi134(ExtensionRefreshSummary source) => new()
    {
        Added = source.Added,
        VersionUpdated = source.VersionUpdated,
        Missing = source.Missing,
        Skipped = source.Skipped.Select(static skip => new ControllerExtensionScanSkipDto
        {
            DirectoryName = skip.DirectoryName,
            FailureCode = skip.FailureCode
        }).ToImmutableArray()
    };

    internal static ControllerExtensionRecordReadDto ToRead(ExtensionRecordConfiguration source) => new()
    {
        ExtensionId = source.ExtensionId, Version = source.Version, LoadState = (ControllerExtensionLoadState)source.LoadState,
        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, RecordVersion = source.RecordVersion
    };

    /// <summary>Maps an API 1.3.3 extension record configuration, including its content hash.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ControllerExtensionRecordReadDto ToReadApi133(ExtensionRecordConfiguration source) => new()
    {
        ExtensionId = source.ExtensionId, Version = source.Version, LoadState = (ControllerExtensionLoadState)source.LoadState,
        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, RecordVersion = source.RecordVersion, ContentHash = source.ContentHash
    };

    internal static ControllerExtensionSettingsReadDto ToRead(ExtensionSettingsConfiguration source)
    {
        ValidateEmbeddedJson(source.SettingsJson);
        using var document = JsonDocument.Parse(source.SettingsJson, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        return new ControllerExtensionSettingsReadDto { ExtensionId = source.ExtensionId, SchemaVersion = source.SchemaVersion, Settings = document.RootElement.Clone(), Version = source.Version };
    }

    internal static ControllerGlobalSettingsWriteDto ToWrite(GlobalSettingsConfiguration source) => new()
    {
        AutoPortRangeStart = source.AutoPortRangeStart,
        AutoPortRangeEnd = source.AutoPortRangeEnd,
        MaxRequestBodyBytes = source.MaxRequestBodyBytes,
        MaxRequestHeaderBytes = source.MaxRequestHeaderBytes,
        MaxConcurrentRequests = source.MaxConcurrentRequests,
        RequestReadTimeoutMs = source.RequestReadTimeout.Ticks / TimeSpan.TicksPerMillisecond,
        ConfigurationPollIntervalMs = source.ConfigurationPollInterval.Ticks / TimeSpan.TicksPerMillisecond,
        TrustedProxyCidrs = source.TrustedProxyCidrs,
        ProxyTimeouts = ToRead(source.ProxyTimeouts),
        ClientIpRatePolicy = source.ClientIpRatePolicy is null ? null : ToRead(source.ClientIpRatePolicy),
        ProxyRetries = ToRead(source.ProxyRetries)
    };

    internal static ControllerRouteWriteDto ToWrite(RouteConfiguration source) => new()
    {
        Enabled = source.Enabled,
        Matcher = ToRead(source.Matcher),
        Target = ToRead(source.Target),
        Priority = source.Priority,
        Forwarding = new ControllerForwardingDto { Mode = (ControllerForwardingMode)source.Forwarding.Mode, ReplaceTemplate = source.Forwarding.ReplaceTemplate },
        RequestHeaderRewrites = source.RequestHeaderRewrites.Select(ToRead).ToImmutableArray(),
        ResponseHeaderRewrites = source.ResponseHeaderRewrites.Select(ToRead).ToImmutableArray(),
        MetadataJson = source.MetadataJson,
        ClientIpRatePolicy = source.ClientIpRatePolicy is null ? null : ToRead(source.ClientIpRatePolicy),
        MaxRequestBodyBytes = source.MaxRequestBodyBytes,
        MaxRequestHeaderBytes = source.MaxRequestHeaderBytes,
        MaxConcurrentRequests = source.MaxConcurrentRequests,
        RequestReadTimeoutMs = source.RequestReadTimeout?.Ticks / TimeSpan.TicksPerMillisecond,
        ProxyRetries = source.ProxyRetries is null ? null : ToRead(source.ProxyRetries)
    };

    internal static ControllerServiceWriteDto ToWrite(ServiceConfiguration source, bool includeEnvironment) => new()
    {
        Enabled = source.Enabled,
        FileName = source.FileName,
        ArgumentList = source.ArgumentList,
        WorkingDirectory = source.WorkingDirectory,
        Environment = includeEnvironment ? source.Environment : null,
        StartMode = (ControllerServiceStartMode)source.StartMode,
        RestartPolicy = (ControllerServiceRestartPolicy)source.RestartPolicy,
        HealthCheck = ToRead(source.HealthCheck)
    };

    internal static GlobalSettingsConfiguration ToContract(ControllerGlobalSettingsWriteDto source, long currentVersion) => new(
        currentVersion, source.AutoPortRangeStart, source.AutoPortRangeEnd, source.MaxRequestBodyBytes, source.MaxConcurrentRequests,
        ToDuration(source.ConfigurationPollIntervalMs, nameof(source.ConfigurationPollIntervalMs)), source.TrustedProxyCidrs.IsDefault ? ImmutableArray<string>.Empty : source.TrustedProxyCidrs,
        ToContract(source.ProxyTimeouts), source.MaxRequestHeaderBytes, ToDuration(source.RequestReadTimeoutMs, nameof(source.RequestReadTimeoutMs)),
        source.ClientIpRatePolicy is null ? null : ToContract(source.ClientIpRatePolicy), ToContract(source.ProxyRetries));

    internal static RouteConfiguration ToContract(ControllerRouteWriteDto source, RouteConfiguration? current, Guid? createId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var id = current?.Id ?? createId ?? throw new ArgumentException("A route identifier is required.");
        var created = current?.CreatedAt ?? DateTimeOffset.UtcNow;
        var updated = current?.UpdatedAt ?? created;
        var version = current?.Version ?? 0;
        ValidateEmbeddedJson(source.MetadataJson ?? "null");
        return new RouteConfiguration(id, source.Enabled, ToContract(source.Matcher), ToContract(source.Target), source.Priority,
            ToContract(source.Forwarding), source.RequestHeaderRewrites.IsDefault ? ImmutableArray<HeaderRewriteConfiguration>.Empty : source.RequestHeaderRewrites.Select(ToContract).ToImmutableArray(),
            source.ResponseHeaderRewrites.IsDefault ? ImmutableArray<HeaderRewriteConfiguration>.Empty : source.ResponseHeaderRewrites.Select(ToContract).ToImmutableArray(), source.MetadataJson ?? "null",
            created, updated, version, source.ClientIpRatePolicy is null ? null : ToContract(source.ClientIpRatePolicy), source.MaxRequestBodyBytes, source.MaxRequestHeaderBytes,
            source.MaxConcurrentRequests, source.RequestReadTimeoutMs is null ? null : ToDuration(source.RequestReadTimeoutMs.Value, nameof(source.RequestReadTimeoutMs)), source.ProxyRetries is null ? null : ToContract(source.ProxyRetries));
    }

    internal static ServiceConfiguration ToContract(ControllerServiceWriteDto source, ServiceConfiguration? current, Guid? createId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var id = current?.Id ?? createId ?? throw new ArgumentException("A service identifier is required.");
        var created = current?.CreatedAt ?? DateTimeOffset.UtcNow;
        var updated = current?.UpdatedAt ?? created;
        var version = current?.Version ?? 0;
        var argumentList = source.ArgumentList.IsDefault ? ImmutableArray<string>.Empty : source.ArgumentList;
        var environment = source.Environment ?? current?.Environment ?? ImmutableDictionary<string, string>.Empty;
        return new ServiceConfiguration(id, source.Enabled, source.FileName, argumentList, source.WorkingDirectory, environment,
            (ServiceStartMode)source.StartMode, (ServiceRestartPolicy)source.RestartPolicy, ToContract(source.HealthCheck), created, updated, version);
    }

    internal static ExtensionSettingsConfiguration ToContract(ControllerExtensionSettingsWriteDto source, string extensionId, long currentVersion)
    {
        if (source.Settings.ValueKind is JsonValueKind.Undefined) throw new ArgumentException("Settings are required.", nameof(source));
        var json = source.Settings.GetRawText();
        ValidateEmbeddedJson(json);
        return new ExtensionSettingsConfiguration(extensionId, source.SchemaVersion, json, currentVersion);
    }

    internal static ControllerServiceEnvironmentReadDto ToEnvironment(ServiceConfiguration source) => new() { ServiceId = source.Id, Environment = source.Environment };

    private static ControllerRouteMatcherDto ToRead(RouteMatcherConfiguration source) => new() { Type = (ControllerRouteMatcherType)source.Type, Pattern = source.Pattern, HostPatterns = source.HostPatterns, Methods = source.Methods };
    private static ControllerRouteTargetDto ToRead(RouteTargetConfiguration source) => source switch
    {
        MicroserviceRouteTargetConfiguration target => new() { Type = ControllerRouteTargetKind.Microservice, ServiceId = target.ServiceId },
        StaticFileRouteTargetConfiguration target => new() { Type = ControllerRouteTargetKind.StaticFile, RootPath = target.RootPath },
        ExtensionHandlerRouteTargetConfiguration target => new() { Type = ControllerRouteTargetKind.ExtensionHandler, HandlerId = target.HandlerId },
        _ => throw new InvalidOperationException("Unsupported route target.")
    };
    private static ControllerHeaderRewriteDto ToRead(HeaderRewriteConfiguration source) => new() { Operation = (ControllerHeaderRewriteOperation)source.Operation, Name = source.Name, Value = source.Value };
    private static ControllerProxyTimeoutDto ToRead(ProxyTimeoutConfiguration source) => new() { ConnectTimeoutMs = source.ConnectTimeout.Ticks / TimeSpan.TicksPerMillisecond, HttpActivityTimeoutMs = source.HttpActivityTimeout.Ticks / TimeSpan.TicksPerMillisecond, HttpTotalTimeoutMs = source.HttpTotalTimeout.Ticks / TimeSpan.TicksPerMillisecond, WebSocketIdleTimeoutMs = source.WebSocketIdleTimeout.Ticks / TimeSpan.TicksPerMillisecond };
    private static ControllerProxyRetryDto ToRead(ProxyRetryConfiguration source) => new() { MaxRetries = source.MaxRetries, InitialBackoffMs = source.InitialBackoff.Ticks / TimeSpan.TicksPerMillisecond, MaximumBackoffMs = source.MaximumBackoff.Ticks / TimeSpan.TicksPerMillisecond, RetryOnConnectionFailure = source.RetryOnConnectionFailure, RetryOnUpstreamDisconnect = source.RetryOnUpstreamDisconnect };
    private static ControllerClientIpRatePolicyDto ToRead(ClientIpRatePolicyConfiguration source) => new() { TokenLimit = source.TokenLimit, TokensPerPeriod = source.TokensPerPeriod, ReplenishmentPeriodMs = source.ReplenishmentPeriod.Ticks / TimeSpan.TicksPerMillisecond, QueueLimit = source.QueueLimit, RejectionBehavior = (ControllerRateLimitRejectionBehavior)source.RejectionBehavior, RetryAfterBehavior = (ControllerRateLimitRetryAfterBehavior)source.RetryAfterBehavior };
    private static ControllerHealthCheckDto ToRead(ServiceHealthCheckConfiguration source) => new() { Type = (ControllerServiceHealthCheckType)source.Type, HttpPath = source.HttpPath, TimeoutMs = source.Timeout.Ticks / TimeSpan.TicksPerMillisecond };
    private static RouteMatcherConfiguration ToContract(ControllerRouteMatcherDto? source) { ArgumentNullException.ThrowIfNull(source); return new RouteMatcherConfiguration((RouteMatcherType)source.Type, source.Pattern, source.HostPatterns.IsDefault ? ImmutableArray<string>.Empty : source.HostPatterns, source.Methods.IsDefault ? ImmutableArray<string>.Empty : source.Methods); }
    private static RouteTargetConfiguration ToContract(ControllerRouteTargetDto? source) => source switch
    {
        null => throw new ArgumentNullException(nameof(source)),
        { Type: ControllerRouteTargetKind.Microservice, ServiceId: { } serviceId, RootPath: null, HandlerId: null } => new MicroserviceRouteTargetConfiguration(serviceId),
        { Type: ControllerRouteTargetKind.StaticFile, ServiceId: null, RootPath: not null, HandlerId: null } => new StaticFileRouteTargetConfiguration(source.RootPath),
        { Type: ControllerRouteTargetKind.ExtensionHandler, ServiceId: null, RootPath: null, HandlerId: not null } => new ExtensionHandlerRouteTargetConfiguration(source.HandlerId),
        _ => throw new ArgumentException("The route target fields do not match its type.", nameof(source))
    };
    private static ForwardingConfiguration ToContract(ControllerForwardingDto? source) { ArgumentNullException.ThrowIfNull(source); return new ForwardingConfiguration((ForwardingMode)source.Mode, source.ReplaceTemplate); }
    private static HeaderRewriteConfiguration ToContract(ControllerHeaderRewriteDto source) { ArgumentNullException.ThrowIfNull(source); return new HeaderRewriteConfiguration((HeaderRewriteOperation)source.Operation, source.Name, source.Value); }
    private static ProxyTimeoutConfiguration ToContract(ControllerProxyTimeoutDto? source) { ArgumentNullException.ThrowIfNull(source); return new ProxyTimeoutConfiguration(ToDuration(source.ConnectTimeoutMs, nameof(source.ConnectTimeoutMs)), ToDuration(source.HttpActivityTimeoutMs, nameof(source.HttpActivityTimeoutMs)), ToDuration(source.HttpTotalTimeoutMs, nameof(source.HttpTotalTimeoutMs)), ToDuration(source.WebSocketIdleTimeoutMs, nameof(source.WebSocketIdleTimeoutMs))); }
    private static ProxyRetryConfiguration ToContract(ControllerProxyRetryDto? source) { ArgumentNullException.ThrowIfNull(source); return new ProxyRetryConfiguration(source.MaxRetries, ToDuration(source.InitialBackoffMs, nameof(source.InitialBackoffMs)), ToDuration(source.MaximumBackoffMs, nameof(source.MaximumBackoffMs)), source.RetryOnConnectionFailure, source.RetryOnUpstreamDisconnect); }
    private static ClientIpRatePolicyConfiguration ToContract(ControllerClientIpRatePolicyDto? source) { ArgumentNullException.ThrowIfNull(source); return new ClientIpRatePolicyConfiguration(source.TokenLimit, source.TokensPerPeriod, ToDuration(source.ReplenishmentPeriodMs, nameof(source.ReplenishmentPeriodMs)), source.QueueLimit, (RateLimitRejectionBehavior)source.RejectionBehavior, (RateLimitRetryAfterBehavior)source.RetryAfterBehavior); }
    private static ServiceHealthCheckConfiguration ToContract(ControllerHealthCheckDto? source) { ArgumentNullException.ThrowIfNull(source); return new ServiceHealthCheckConfiguration((ServiceHealthCheckType)source.Type, source.HttpPath, ToDuration(source.TimeoutMs, nameof(source.TimeoutMs))); }
    private static TimeSpan ToDuration(long milliseconds, string parameterName)
    {
        if (milliseconds <= 0) throw new ArgumentOutOfRangeException(parameterName);
        try { return TimeSpan.FromMilliseconds(milliseconds); } catch (OverflowException) { throw new ArgumentOutOfRangeException(parameterName); }
    }
    private static void ValidateEmbeddedJson(string? json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > ControllerManagementJson.MaximumEmbeddedJsonBytes) throw new ArgumentException("Embedded JSON is invalid.", nameof(json));
        try { using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); _ = document.RootElement.ValueKind; }
        catch (JsonException) { throw new ArgumentException("Embedded JSON is invalid.", nameof(json)); }
    }
}

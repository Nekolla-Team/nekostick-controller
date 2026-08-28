using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Read representation of a service. Environment is intentionally absent.</summary>
public sealed class ControllerServiceReadDto
{
    /// <summary>Unique service identifier.</summary>
    [JsonPropertyName("id")] public Guid Id { get; init; }
    /// <summary>Whether the service is enabled.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    /// <summary>Executable file name for the service.</summary>
    [JsonPropertyName("fileName")] public string FileName { get; init; } = string.Empty;
    /// <summary>Arguments supplied to the service process.</summary>
    [JsonPropertyName("argumentList")] public ImmutableArray<string> ArgumentList { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Working directory for the service process.</summary>
    [JsonPropertyName("workingDirectory")] public string WorkingDirectory { get; init; } = string.Empty;
    /// <summary>Service start mode.</summary>
    [JsonPropertyName("startMode")] public ControllerServiceStartMode StartMode { get; init; }
    /// <summary>Service restart policy.</summary>
    [JsonPropertyName("restartPolicy")] public ControllerServiceRestartPolicy RestartPolicy { get; init; }
    /// <summary>Service health-check configuration.</summary>
    [JsonPropertyName("healthCheck")] public ControllerHealthCheckDto HealthCheck { get; init; } = new();
    /// <summary>Time at which the service was created.</summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Time at which the service was last updated.</summary>
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Current service version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
}

/// <summary>Read-only runtime telemetry for one supervised service.</summary>
public sealed class ControllerServiceRuntimeReadDto
{
    /// <summary>Stable service identifier.</summary>
    [JsonPropertyName("serviceId")] public Guid ServiceId { get; init; }
    /// <summary>Operating-system process identifier when known.</summary>
    [JsonPropertyName("processId")] public int? ProcessId { get; init; }
    /// <summary>UTC start time of the current process generation when known.</summary>
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; init; }
    /// <summary>Current process-generation uptime in milliseconds when representable.</summary>
    [JsonPropertyName("uptimeMs")] public long? UptimeMs { get; init; }
    /// <summary>Safe lifecycle state.</summary>
    [JsonPropertyName("lifecycleState")] public ControllerServiceLifecycleState LifecycleState { get; init; }
    /// <summary>Safe health state.</summary>
    [JsonPropertyName("healthState")] public ControllerServiceHealthState HealthState { get; init; }
    /// <summary>Cumulative forwarded request count.</summary>
    [JsonPropertyName("forwardedRequestCount")] public long ForwardedRequestCount { get; init; }
    /// <summary>Currently active forwarded request count.</summary>
    [JsonPropertyName("activeForwardedRequestCount")] public long ActiveForwardedRequestCount { get; init; }
    /// <summary>UTC time at which telemetry was last updated when known.</summary>
    [JsonPropertyName("lastUpdatedAt")] public DateTimeOffset? LastUpdatedAt { get; init; }
    /// <summary>UTC time of the latest health observation when known.</summary>
    [JsonPropertyName("lastHealthAt")] public DateTimeOffset? LastHealthAt { get; init; }
    /// <summary>Owning extension identifier; null for Host-owned services.</summary>
    [JsonPropertyName("ownerExtensionId")] public string? OwnerExtensionId { get; init; }
}

/// <summary>Write representation for service create/patch. Environment is write-only on create.</summary>
public sealed class ControllerServiceWriteDto
{
    /// <summary>Whether the service is enabled.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    /// <summary>Executable file name for the service.</summary>
    [JsonPropertyName("fileName")] public string FileName { get; init; } = string.Empty;
    /// <summary>Arguments supplied to the service process.</summary>
    [JsonPropertyName("argumentList")] public ImmutableArray<string> ArgumentList { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Working directory for the service process.</summary>
    [JsonPropertyName("workingDirectory")] public string WorkingDirectory { get; init; } = string.Empty;
    /// <summary>Environment variables supplied when the service is created or updated.</summary>
    [JsonPropertyName("environment")] public ImmutableDictionary<string, string>? Environment { get; init; }
    /// <summary>Service start mode.</summary>
    [JsonPropertyName("startMode")] public ControllerServiceStartMode StartMode { get; init; }
    /// <summary>Service restart policy.</summary>
    [JsonPropertyName("restartPolicy")] public ControllerServiceRestartPolicy RestartPolicy { get; init; }
    /// <summary>Optional service health-check configuration.</summary>
    [JsonPropertyName("healthCheck")] public ControllerHealthCheckDto? HealthCheck { get; init; }
}

/// <summary>Describes a service health check.</summary>
public sealed class ControllerHealthCheckDto
{
    /// <summary>Health-check type.</summary>
    [JsonPropertyName("type")] public ControllerServiceHealthCheckType Type { get; init; }
    /// <summary>HTTP path checked by an HTTP health check.</summary>
    [JsonPropertyName("httpPath")] public string? HttpPath { get; init; }
    /// <summary>Health-check timeout in milliseconds.</summary>
    [JsonPropertyName("timeoutMs")] public long TimeoutMs { get; init; }
}

/// <summary>Read representation of a service environment.</summary>
public sealed class ControllerServiceEnvironmentReadDto
{
    /// <summary>Identifier of the service owning the environment.</summary>
    [JsonPropertyName("serviceId")] public Guid ServiceId { get; init; }
    /// <summary>Environment variables configured for the service.</summary>
    [JsonPropertyName("environment")] public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>Write representation of a service environment.</summary>
public sealed class ControllerServiceEnvironmentWriteDto
{
    /// <summary>Environment variables to apply to the service.</summary>
    [JsonPropertyName("environment")] public ImmutableDictionary<string, string>? Environment { get; init; }
}

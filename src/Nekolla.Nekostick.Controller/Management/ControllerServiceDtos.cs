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
    /// <summary>Failure stage name (Spawn, HealthProbe, ProcessExit) when the host reports enriched runtime state.</summary>
    [JsonPropertyName("failureStage")] public string? FailureStage { get; set; }
    /// <summary>Machine-readable failure code when the host reports enriched runtime state.</summary>
    [JsonPropertyName("failureCode")] public string? FailureCode { get; set; }
    /// <summary>Human-readable failure reason when the host reports enriched runtime state.</summary>
    [JsonPropertyName("failureReason")] public string? FailureReason { get; set; }
    /// <summary>Exit code of the last process generation when known.</summary>
    [JsonPropertyName("processExitCode")] public int? ProcessExitCode { get; set; }
    /// <summary>Number of restart attempts consumed; null on hosts without enriched runtime state.</summary>
    [JsonPropertyName("restartCount")] public int? RestartCount { get; set; }
    /// <summary>UTC time at which the current lifecycle state was entered when known.</summary>
    [JsonPropertyName("stateEnteredAt")] public DateTimeOffset? StateEnteredAt { get; set; }
    /// <summary>UTC time of the next scheduled retry when the host reports one.</summary>
    [JsonPropertyName("retryAt")] public DateTimeOffset? RetryAt { get; set; }
    /// <summary>Latest health-probe observation when the host reports enriched runtime state.</summary>
    [JsonPropertyName("lastProbe")] public ControllerServiceProbeReadDto? LastProbe { get; set; }
}

/// <summary>Contains one host health-probe observation.</summary>
public sealed class ControllerServiceProbeReadDto
{
    /// <summary>UTC time the probe was observed.</summary>
    [JsonPropertyName("observedAt")] public DateTimeOffset ObservedAt { get; init; }
    /// <summary>Probe result name (Healthy, Unhealthy, TimedOut, Unavailable, Cancelled, Unknown).</summary>
    [JsonPropertyName("result")] public string Result { get; init; } = string.Empty;
    /// <summary>Probe target (for example the HTTP URL) when known.</summary>
    [JsonPropertyName("target")] public string? Target { get; init; }
    /// <summary>Machine-readable failure code when the probe failed.</summary>
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    /// <summary>Human-readable probe error detail when available.</summary>
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

/// <summary>One entry of the service runtime-state feed (initial replay or live change).</summary>
public sealed class ControllerServiceRuntimeFeedEntryDto
{
    /// <summary>Monotonic host-assigned feed sequence.</summary>
    [JsonPropertyName("sequence")] public long Sequence { get; init; }
    /// <summary>Entry kind: snapshot for state upserts, removed for removals.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    /// <summary>Service the entry describes.</summary>
    [JsonPropertyName("serviceId")] public Guid ServiceId { get; init; }
    /// <summary>Whether the entry belongs to the subscriber's initial state replay.</summary>
    [JsonPropertyName("isInitialSnapshot")] public bool IsInitialSnapshot { get; init; }
    /// <summary>Owning extension identifier of the service; null for host-owned services.</summary>
    [JsonPropertyName("ownerExtensionId")] public string? OwnerExtensionId { get; init; }
    /// <summary>Latest runtime state; null on removed entries.</summary>
    [JsonPropertyName("snapshot")] public ControllerServiceRuntimeReadDto? Snapshot { get; init; }
}

/// <summary>One entry of a service log feed: an output chunk or a lifecycle event.</summary>
public sealed class ControllerServiceLogEntryDto
{
    /// <summary>Entry kind: output, generationStarted, processExited, startupFailed, currentState, gap, or termination.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    /// <summary>Host-assigned feed sequence used as the resume cursor; null when the entry carries none.</summary>
    [JsonPropertyName("sequence")] public long? Sequence { get; init; }
    /// <summary>UTC time at which the host recorded the entry.</summary>
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    /// <summary>Output stream name (stdout or stderr); only set on output entries.</summary>
    [JsonPropertyName("stream")] public string? Stream { get; init; }
    /// <summary>Base64-encoded output bytes; only set on output entries.</summary>
    [JsonPropertyName("data")] public string? Data { get; init; }
    /// <summary>Process generation the entry belongs to when known.</summary>
    [JsonPropertyName("processInstanceId")] public Guid? ProcessInstanceId { get; init; }
    /// <summary>1-based attempt number of the process generation when known.</summary>
    [JsonPropertyName("attemptNumber")] public int? AttemptNumber { get; init; }
    /// <summary>Exit code of a completed process generation.</summary>
    [JsonPropertyName("processExitCode")] public int? ProcessExitCode { get; init; }
    /// <summary>Lifecycle state name carried by state entries.</summary>
    [JsonPropertyName("lifecycleState")] public string? LifecycleState { get; init; }
    /// <summary>Failure stage name when the entry describes a failure.</summary>
    [JsonPropertyName("failureStage")] public string? FailureStage { get; init; }
    /// <summary>Machine-readable failure code when the entry describes a failure.</summary>
    [JsonPropertyName("failureCode")] public string? FailureCode { get; init; }
    /// <summary>Human-readable failure detail when the entry describes a failure.</summary>
    [JsonPropertyName("failureReason")] public string? FailureReason { get; init; }
    /// <summary>First sequence dropped before a gap entry.</summary>
    [JsonPropertyName("firstMissingSequence")] public long? FirstMissingSequence { get; init; }
    /// <summary>Last sequence dropped before a gap entry.</summary>
    [JsonPropertyName("lastMissingSequence")] public long? LastMissingSequence { get; init; }
    /// <summary>Why the feed ended; only set on termination entries.</summary>
    [JsonPropertyName("terminationReason")] public string? TerminationReason { get; init; }
}

/// <summary>Read representation of a node-local service runtime action outcome.</summary>
public sealed class ControllerServiceRuntimeActionReadDto
{
    /// <summary>Outcome of the requested action.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = string.Empty;
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

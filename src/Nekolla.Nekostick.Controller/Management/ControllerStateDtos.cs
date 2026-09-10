using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Describes whether one controller listener is configured and accepting requests.</summary>
public sealed class ControllerListenerStateDto
{
    /// <summary>Gets whether the listener is enabled by the active controller options.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    /// <summary>Gets whether the listener is currently accepting requests.</summary>
    [JsonPropertyName("running")] public bool Running { get; init; }
}

/// <summary>Contains the safe runtime state of all controller listeners.</summary>
public sealed class ControllerListenersStateDto
{
    /// <summary>Gets the HostRoute listener state.</summary>
    [JsonPropertyName("hostRoute")] public ControllerListenerStateDto HostRoute { get; init; } = new();
    /// <summary>Gets the HTTP/JSON listener state.</summary>
    [JsonPropertyName("httpJson")] public ControllerListenerStateDto HttpJson { get; init; } = new();
    /// <summary>Gets the gRPC listener state.</summary>
    [JsonPropertyName("grpc")] public ControllerListenerStateDto Grpc { get; init; } = new();
    /// <summary>Gets the Unix-socket listener state.</summary>
    [JsonPropertyName("unixSocket")] public ControllerListenerStateDto UnixSocket { get; init; } = new();
}

/// <summary>Identifies the latest host configuration snapshot outcome exposed by the controller.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerHostSnapshotState>))]
public enum ControllerHostSnapshotState
{
    /// <summary>No snapshot outcome is available.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    /// <summary>The latest complete snapshot was accepted and published.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,
    /// <summary>The latest candidate snapshot was rejected.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected
}

/// <summary>Identifies the host readiness state exposed by the controller.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerHostReadinessState>))]
public enum ControllerHostReadinessState
{
    /// <summary>No readiness observation is available.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    /// <summary>No validated configuration snapshot is available.</summary>
    [JsonStringEnumMemberName("unready")]
    Unready,
    /// <summary>A validated snapshot and database are available.</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,
    /// <summary>A snapshot is available while persistence capabilities are degraded.</summary>
    [JsonStringEnumMemberName("degraded")]
    Degraded
}

/// <summary>Contains safe, non-sensitive host information exposed by API 1.3.3.</summary>
public sealed class ControllerHostInfoDto
{
    /// <summary>Stable host node identifier when available.</summary>
    [JsonPropertyName("nodeId")] public string? NodeId { get; init; }
    /// <summary>Whether host configuration writes are disabled.</summary>
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; init; }
    /// <summary>Whether host extension loading is disabled.</summary>
    [JsonPropertyName("extensionsSkipped")] public bool ExtensionsSkipped { get; init; }
    /// <summary>Whether host service supervision is disabled.</summary>
    [JsonPropertyName("supervisorDisabled")] public bool SupervisorDisabled { get; init; }
    /// <summary>Whether the host database was available during the latest operation.</summary>
    [JsonPropertyName("databaseAvailable")] public bool DatabaseAvailable { get; init; }
    /// <summary>Whether a complete host configuration snapshot is published.</summary>
    [JsonPropertyName("snapshotAvailable")] public bool SnapshotAvailable { get; init; }
    /// <summary>Whether the published host configuration is valid.</summary>
    [JsonPropertyName("configurationValid")] public bool ConfigurationValid { get; init; }
    /// <summary>Published host configuration version when available.</summary>
    [JsonPropertyName("publishedConfigurationVersion")] public long? PublishedConfigurationVersion { get; init; }
    /// <summary>Latest host snapshot outcome.</summary>
    [JsonPropertyName("lastSnapshotState")] public ControllerHostSnapshotState LastSnapshotState { get; init; }
    /// <summary>UTC time of the latest host snapshot state transition when known.</summary>
    [JsonPropertyName("lastSnapshotStateAt")] public DateTimeOffset? LastSnapshotStateAt { get; init; }
    /// <summary>Current host readiness state.</summary>
    [JsonPropertyName("readiness")] public ControllerHostReadinessState Readiness { get; init; }
}

/// <summary>Contains the embedded Web UI availability and serving state.</summary>
public sealed class ControllerWebUiStateDto
{
    /// <summary>Gets whether the embedded Web UI resource is present in the controller assembly.</summary>
    [JsonPropertyName("embedded")] public bool Embedded { get; init; }
    /// <summary>Gets whether the Web UI is enabled by current options and embedded.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
}

/// <summary>Contains unversioned, non-secret controller runtime state.</summary>
public sealed class ControllerStateDto
{
    /// <summary>Gets whether the controller is using its ephemeral bootstrap route.</summary>
    [JsonPropertyName("bootstrapMode")] public bool BootstrapMode { get; init; }
    /// <summary>Gets the safe state of each controller listener.</summary>
    [JsonPropertyName("listeners")] public ControllerListenersStateDto Listeners { get; init; } = new();
    /// <summary>Gets embedded Web UI availability and enablement.</summary>
    [JsonPropertyName("webUi")] public ControllerWebUiStateDto WebUi { get; init; } = new();
    /// <summary>Gets safe host information when the API 1.3.3 snapshot is available.</summary>
    [JsonPropertyName("host")] public ControllerHostInfoDto? Host { get; init; }
}

/// <summary>Contains the stable transport response envelope.</summary>
public sealed class ControllerResponseEnvelope
{
    /// <summary>Protocol version carried by this response.</summary>
    [JsonPropertyName("apiVersion")] public int ApiVersion { get; init; } = ControllerManagementApiContract.Version;
    /// <summary>Indicates whether the operation succeeded.</summary>
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    /// <summary>Stable machine-readable response code.</summary>
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    /// <summary>Human-readable response message.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    /// <summary>Optional response payload.</summary>
    [JsonPropertyName("data")] public object? Data { get; init; }
    /// <summary>Optional resource or configuration version associated with the response.</summary>
    [JsonPropertyName("version")] public long? Version { get; init; }
}

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

/// <summary>Contains unversioned, non-secret controller runtime state.</summary>
public sealed class ControllerStateDto
{
    /// <summary>Gets whether the controller is using its ephemeral bootstrap route.</summary>
    [JsonPropertyName("bootstrapMode")] public bool BootstrapMode { get; init; }
    /// <summary>Gets the safe state of each controller listener.</summary>
    [JsonPropertyName("listeners")] public ControllerListenersStateDto Listeners { get; init; } = new();
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

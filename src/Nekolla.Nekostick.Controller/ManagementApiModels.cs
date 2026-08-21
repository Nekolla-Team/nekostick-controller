using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller;

/// <summary>Names and versions shared by every controller management transport.</summary>
public static class ControllerManagementApiContract
{
    /// <summary>Current version of the controller management API.</summary>
    public const int Version = 1;
    /// <summary>HTTP header used to present the management API key.</summary>
    public const string ApiKeyHeaderName = "x-nekostick-controller-key";
    /// <summary>Media type used by management API JSON requests and responses.</summary>
    public const string JsonMediaType = "application/json";
    /// <summary>Stable identifier of the management handler.</summary>
    public const string HandlerId = "nekolla.nekostick.controller.management";
    /// <summary>Root path of the versioned management API.</summary>
    public const string RootPath = "/v1";
    /// <summary>Path for global settings operations.</summary>
    public const string GlobalSettingsPath = "/v1/global-settings";
    /// <summary>Path for route operations.</summary>
    public const string RoutesPath = "/v1/routes";
    /// <summary>Path for service operations.</summary>
    public const string ServicesPath = "/v1/services";
    /// <summary>Path for extension operations.</summary>
    public const string ExtensionsPath = "/v1/extensions";
    /// <summary>HTTP header carrying a resource entity tag.</summary>
    public const string ETagHeaderName = "etag";
    /// <summary>HTTP header used to supply the entity tag required for a conditional update.</summary>
    public const string IfMatchHeaderName = "if-match";
    /// <summary>HTTP header carrying the location of a newly created resource.</summary>
    public const string LocationHeaderName = "location";
}

/// <summary>Identifies the destination kind for a controller route.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerRouteTargetKind>))]
public enum ControllerRouteTargetKind
{
    /// <summary>Forwards the route to a managed microservice.</summary>
    Microservice,
    /// <summary>Serves the route from a static-file root.</summary>
    StaticFile,
    /// <summary>Forwards the route to another extension handler.</summary>
    ExtensionHandler
}

/// <summary>Specifies how a route matcher compares request paths.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerRouteMatcherType>))]
public enum ControllerRouteMatcherType
{
    /// <summary>Matches the path exactly using case-sensitive comparison.</summary>
    Exact,
    /// <summary>Matches the path exactly without regard to case.</summary>
    ExactCaseInsensitive,
    /// <summary>Matches paths beginning with the configured prefix using case-sensitive comparison.</summary>
    Prefix,
    /// <summary>Matches paths beginning with the configured prefix without regard to case.</summary>
    PrefixCaseInsensitive,
    /// <summary>Matches the path using a regular expression.</summary>
    Regex
}

/// <summary>Specifies how a matched path is forwarded to its target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerForwardingMode>))]
public enum ControllerForwardingMode
{
    /// <summary>Preserves the matched path when forwarding.</summary>
    Preserve,
    /// <summary>Removes the matched route prefix before forwarding.</summary>
    Strip,
    /// <summary>Replaces the matched path using the configured template.</summary>
    Replace
}

/// <summary>Specifies the operation applied to a request or response header.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerHeaderRewriteOperation>))]
public enum ControllerHeaderRewriteOperation
{
    /// <summary>Removes the named header.</summary>
    Remove,
    /// <summary>Sets the named header to the configured value.</summary>
    Set,
    /// <summary>Adds the configured value to the named header.</summary>
    Add
}

/// <summary>Specifies when a managed service is started.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerServiceStartMode>))]
public enum ControllerServiceStartMode
{
    /// <summary>Starts the service during controller startup.</summary>
    Eager,
    /// <summary>Starts the service when it is first needed.</summary>
    Lazy
}

/// <summary>Specifies when a managed service is restarted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerServiceRestartPolicy>))]
public enum ControllerServiceRestartPolicy
{
    /// <summary>Never restarts the service automatically.</summary>
    Never,
    /// <summary>Restarts the service after a failure.</summary>
    OnFailure,
    /// <summary>Always restarts the service when it exits.</summary>
    Always
}

/// <summary>Specifies the health check used for a managed service.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerServiceHealthCheckType>))]
public enum ControllerServiceHealthCheckType
{
    /// <summary>Checks whether the service process is running.</summary>
    Process,
    /// <summary>Checks whether the configured TCP endpoint is reachable.</summary>
    Tcp,
    /// <summary>Checks the configured HTTP endpoint.</summary>
    Http
}

/// <summary>Specifies what happens when a client IP rate-limit request is rejected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerRateLimitRejectionBehavior>))]
public enum ControllerRateLimitRejectionBehavior
{
    /// <summary>Rejects the request immediately.</summary>
    Reject,
    /// <summary>Queues the request when the queue has capacity.</summary>
    Queue
}

/// <summary>Specifies how retry-after information is generated for rate-limited requests.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerRateLimitRetryAfterBehavior>))]
public enum ControllerRateLimitRetryAfterBehavior
{
    /// <summary>Does not include replenishment-period retry-after information.</summary>
    None,
    /// <summary>Derives retry-after information from the replenishment period.</summary>
    FromReplenishmentPeriod
}

/// <summary>Describes the loading state of a controller extension.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerExtensionLoadState>))]
public enum ControllerExtensionLoadState
{
    /// <summary>The extension has been discovered but not loaded.</summary>
    Discovered,
    /// <summary>The extension is loaded.</summary>
    Loaded,
    /// <summary>The extension has been stopped.</summary>
    Stopped,
    /// <summary>The extension failed to load or run.</summary>
    Failed,
    /// <summary>The extension is being unloaded.</summary>
    Unloading
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

/// <summary>Describes the criteria used to match a route.</summary>
public sealed class ControllerRouteMatcherDto
{
    /// <summary>Path matching strategy.</summary>
    [JsonPropertyName("type")] public ControllerRouteMatcherType Type { get; init; }
    /// <summary>Path pattern evaluated by the matcher.</summary>
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = string.Empty;
    /// <summary>Host patterns that constrain route matching.</summary>
    [JsonPropertyName("hostPatterns")] public ImmutableArray<string> HostPatterns { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>HTTP methods that constrain route matching.</summary>
    [JsonPropertyName("methods")] public ImmutableArray<string> Methods { get; init; } = ImmutableArray<string>.Empty;
}

/// <summary>Describes the destination selected by a route.</summary>
public sealed class ControllerRouteTargetDto
{
    /// <summary>Destination kind.</summary>
    [JsonPropertyName("type")] public ControllerRouteTargetKind Type { get; init; }
    /// <summary>Identifier of the target microservice, when applicable.</summary>
    [JsonPropertyName("serviceId")] public Guid? ServiceId { get; init; }
    /// <summary>Static-file root path, when applicable.</summary>
    [JsonPropertyName("rootPath")] public string? RootPath { get; init; }
    /// <summary>Extension handler identifier, when applicable.</summary>
    [JsonPropertyName("handlerId")] public string? HandlerId { get; init; }
}

/// <summary>Describes a request or response header rewrite.</summary>
public sealed class ControllerHeaderRewriteDto
{
    /// <summary>Rewrite operation.</summary>
    [JsonPropertyName("operation")] public ControllerHeaderRewriteOperation Operation { get; init; }
    /// <summary>Name of the header to rewrite.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    /// <summary>Value used by operations that provide a header value.</summary>
    [JsonPropertyName("value")] public string? Value { get; init; }
}

/// <summary>Describes how a matched request path is forwarded.</summary>
public sealed class ControllerForwardingDto
{
    /// <summary>Path forwarding mode.</summary>
    [JsonPropertyName("mode")] public ControllerForwardingMode Mode { get; init; }
    /// <summary>Template used when the forwarding mode replaces the path.</summary>
    [JsonPropertyName("replaceTemplate")] public string? ReplaceTemplate { get; init; }
}

/// <summary>Describes request rate limiting by client IP.</summary>
public sealed class ControllerClientIpRatePolicyDto
{
    /// <summary>Maximum number of tokens the bucket can hold.</summary>
    [JsonPropertyName("tokenLimit")] public long TokenLimit { get; init; }
    /// <summary>Number of tokens added during each replenishment period.</summary>
    [JsonPropertyName("tokensPerPeriod")] public long TokensPerPeriod { get; init; }
    /// <summary>Token replenishment period in milliseconds.</summary>
    [JsonPropertyName("replenishmentPeriodMs")] public long ReplenishmentPeriodMs { get; init; }
    /// <summary>Maximum number of requests that may wait in the queue.</summary>
    [JsonPropertyName("queueLimit")] public int QueueLimit { get; init; }
    /// <summary>Behavior applied when a request cannot be admitted immediately.</summary>
    [JsonPropertyName("rejectionBehavior")] public ControllerRateLimitRejectionBehavior RejectionBehavior { get; init; }
    /// <summary>Behavior used to determine retry-after information.</summary>
    [JsonPropertyName("retryAfterBehavior")] public ControllerRateLimitRetryAfterBehavior RetryAfterBehavior { get; init; }
}

/// <summary>Describes proxy operation time limits.</summary>
public sealed class ControllerProxyTimeoutDto
{
    /// <summary>Connection timeout in milliseconds.</summary>
    [JsonPropertyName("connectTimeoutMs")] public long ConnectTimeoutMs { get; init; }
    /// <summary>Timeout for an individual HTTP activity in milliseconds.</summary>
    [JsonPropertyName("httpActivityTimeoutMs")] public long HttpActivityTimeoutMs { get; init; }
    /// <summary>Total HTTP operation timeout in milliseconds.</summary>
    [JsonPropertyName("httpTotalTimeoutMs")] public long HttpTotalTimeoutMs { get; init; }
    /// <summary>WebSocket idle timeout in milliseconds.</summary>
    [JsonPropertyName("webSocketIdleTimeoutMs")] public long WebSocketIdleTimeoutMs { get; init; }
}

/// <summary>Describes proxy retry behavior.</summary>
public sealed class ControllerProxyRetryDto
{
    /// <summary>Maximum number of retries.</summary>
    [JsonPropertyName("maxRetries")] public int MaxRetries { get; init; }
    /// <summary>Initial retry backoff in milliseconds.</summary>
    [JsonPropertyName("initialBackoffMs")] public long InitialBackoffMs { get; init; }
    /// <summary>Maximum retry backoff in milliseconds.</summary>
    [JsonPropertyName("maximumBackoffMs")] public long MaximumBackoffMs { get; init; }
    /// <summary>Whether connection failures are retried.</summary>
    [JsonPropertyName("retryOnConnectionFailure")] public bool RetryOnConnectionFailure { get; init; }
    /// <summary>Whether upstream disconnects are retried.</summary>
    [JsonPropertyName("retryOnUpstreamDisconnect")] public bool RetryOnUpstreamDisconnect { get; init; }
}

/// <summary>Read representation of global settings. Version is server-owned.</summary>
public sealed class ControllerGlobalSettingsReadDto
{
    /// <summary>Server-owned global-settings version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
    /// <summary>Beginning of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeStart")] public int AutoPortRangeStart { get; init; }
    /// <summary>End of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeEnd")] public int AutoPortRangeEnd { get; init; }
    /// <summary>Maximum request body size in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long MaxRequestBodyBytes { get; init; }
    /// <summary>Maximum request header size in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long MaxRequestHeaderBytes { get; init; }
    /// <summary>Maximum number of concurrent requests.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int MaxConcurrentRequests { get; init; }
    /// <summary>Request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long RequestReadTimeoutMs { get; init; }
    /// <summary>Configuration polling interval in milliseconds.</summary>
    [JsonPropertyName("configurationPollIntervalMs")] public long ConfigurationPollIntervalMs { get; init; }
    /// <summary>Trusted proxy network ranges in CIDR notation.</summary>
    [JsonPropertyName("trustedProxyCidrs")] public ImmutableArray<string> TrustedProxyCidrs { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Proxy operation timeouts.</summary>
    [JsonPropertyName("proxyTimeouts")] public ControllerProxyTimeoutDto ProxyTimeouts { get; init; } = new();
    /// <summary>Optional client-IP rate-limit policy.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto ProxyRetries { get; init; } = new();
}

/// <summary>Write representation of global settings. It deliberately has no identity fields.</summary>
public sealed class ControllerGlobalSettingsWriteDto
{
    /// <summary>Beginning of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeStart")] public int AutoPortRangeStart { get; init; }
    /// <summary>End of the automatic service port range.</summary>
    [JsonPropertyName("autoPortRangeEnd")] public int AutoPortRangeEnd { get; init; }
    /// <summary>Maximum request body size in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long MaxRequestBodyBytes { get; init; }
    /// <summary>Maximum request header size in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long MaxRequestHeaderBytes { get; init; }
    /// <summary>Maximum number of concurrent requests.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int MaxConcurrentRequests { get; init; }
    /// <summary>Request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long RequestReadTimeoutMs { get; init; }
    /// <summary>Configuration polling interval in milliseconds.</summary>
    [JsonPropertyName("configurationPollIntervalMs")] public long ConfigurationPollIntervalMs { get; init; }
    /// <summary>Trusted proxy network ranges in CIDR notation.</summary>
    [JsonPropertyName("trustedProxyCidrs")] public ImmutableArray<string> TrustedProxyCidrs { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Proxy operation timeouts.</summary>
    [JsonPropertyName("proxyTimeouts")] public ControllerProxyTimeoutDto ProxyTimeouts { get; init; } = new();
    /// <summary>Optional client-IP rate-limit policy.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto ProxyRetries { get; init; } = new();
}

/// <summary>Read representation of a route, including server-owned metadata.</summary>
public sealed class ControllerRouteReadDto
{
    /// <summary>Unique route identifier.</summary>
    [JsonPropertyName("id")] public Guid Id { get; init; }
    /// <summary>Whether the route is enabled.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    /// <summary>Route matching criteria.</summary>
    [JsonPropertyName("matcher")] public ControllerRouteMatcherDto Matcher { get; init; } = new();
    /// <summary>Route destination.</summary>
    [JsonPropertyName("target")] public ControllerRouteTargetDto Target { get; init; } = new();
    /// <summary>Route priority used during route selection.</summary>
    [JsonPropertyName("priority")] public int Priority { get; init; }
    /// <summary>Path forwarding configuration.</summary>
    [JsonPropertyName("forwarding")] public ControllerForwardingDto Forwarding { get; init; } = new();
    /// <summary>Rewrites applied to request headers.</summary>
    [JsonPropertyName("requestHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> RequestHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Rewrites applied to response headers.</summary>
    [JsonPropertyName("responseHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> ResponseHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Embedded route metadata as a JSON document.</summary>
    [JsonPropertyName("metadataJson")] public string MetadataJson { get; init; } = string.Empty;
    /// <summary>Time at which the route was created.</summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Time at which the route was last updated.</summary>
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Current route version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
    /// <summary>Optional client-IP rate-limit policy for the route.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Optional route-specific request body limit in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long? MaxRequestBodyBytes { get; init; }
    /// <summary>Optional route-specific request header limit in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long? MaxRequestHeaderBytes { get; init; }
    /// <summary>Optional route-specific concurrent request limit.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int? MaxConcurrentRequests { get; init; }
    /// <summary>Optional route-specific request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long? RequestReadTimeoutMs { get; init; }
    /// <summary>Optional route-specific proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto? ProxyRetries { get; init; }
}

/// <summary>Write representation for route create/patch. Identity and server fields are absent.</summary>
public sealed class ControllerRouteWriteDto
{
    /// <summary>Whether the route is enabled.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    /// <summary>Route matching criteria.</summary>
    [JsonPropertyName("matcher")] public ControllerRouteMatcherDto? Matcher { get; init; }
    /// <summary>Route destination.</summary>
    [JsonPropertyName("target")] public ControllerRouteTargetDto? Target { get; init; }
    /// <summary>Route priority used during route selection.</summary>
    [JsonPropertyName("priority")] public int Priority { get; init; }
    /// <summary>Path forwarding configuration.</summary>
    [JsonPropertyName("forwarding")] public ControllerForwardingDto? Forwarding { get; init; }
    /// <summary>Rewrites applied to request headers.</summary>
    [JsonPropertyName("requestHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> RequestHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Rewrites applied to response headers.</summary>
    [JsonPropertyName("responseHeaderRewrites")] public ImmutableArray<ControllerHeaderRewriteDto> ResponseHeaderRewrites { get; init; } = ImmutableArray<ControllerHeaderRewriteDto>.Empty;
    /// <summary>Embedded route metadata as a JSON document.</summary>
    [JsonPropertyName("metadataJson")] public string MetadataJson { get; init; } = string.Empty;
    /// <summary>Optional client-IP rate-limit policy for the route.</summary>
    [JsonPropertyName("clientIpRatePolicy")] public ControllerClientIpRatePolicyDto? ClientIpRatePolicy { get; init; }
    /// <summary>Optional route-specific request body limit in bytes.</summary>
    [JsonPropertyName("maxRequestBodyBytes")] public long? MaxRequestBodyBytes { get; init; }
    /// <summary>Optional route-specific request header limit in bytes.</summary>
    [JsonPropertyName("maxRequestHeaderBytes")] public long? MaxRequestHeaderBytes { get; init; }
    /// <summary>Optional route-specific concurrent request limit.</summary>
    [JsonPropertyName("maxConcurrentRequests")] public int? MaxConcurrentRequests { get; init; }
    /// <summary>Optional route-specific request-read timeout in milliseconds.</summary>
    [JsonPropertyName("requestReadTimeoutMs")] public long? RequestReadTimeoutMs { get; init; }
    /// <summary>Optional route-specific proxy retry policy.</summary>
    [JsonPropertyName("proxyRetries")] public ControllerProxyRetryDto? ProxyRetries { get; init; }
}

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

/// <summary>Read representation of a registered extension.</summary>
public sealed class ControllerExtensionRecordReadDto
{
    /// <summary>Extension identifier.</summary>
    [JsonPropertyName("extensionId")] public string ExtensionId { get; init; } = string.Empty;
    /// <summary>Extension version.</summary>
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    /// <summary>Current extension load state.</summary>
    [JsonPropertyName("loadState")] public ControllerExtensionLoadState LoadState { get; init; }
    /// <summary>Time at which the extension record was created.</summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Time at which the extension record was last updated.</summary>
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Current version of the extension record.</summary>
    [JsonPropertyName("recordVersion")] public long RecordVersion { get; init; }
}

/// <summary>Read representation of extension settings.</summary>
public sealed class ControllerExtensionSettingsReadDto
{
    /// <summary>Extension identifier.</summary>
    [JsonPropertyName("extensionId")] public string ExtensionId { get; init; } = string.Empty;
    /// <summary>Version of the extension settings schema.</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    /// <summary>Extension settings JSON value.</summary>
    [JsonPropertyName("settings")] public JsonElement Settings { get; init; }
    /// <summary>Current extension-settings version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
}

/// <summary>Write representation of extension settings.</summary>
public sealed class ControllerExtensionSettingsWriteDto
{
    /// <summary>Version of the extension settings schema.</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    /// <summary>Extension settings JSON value.</summary>
    [JsonPropertyName("settings")] public JsonElement Settings { get; init; }
}

/// <summary>Aggregate root representation. Sensitive service environments and settings are not embedded.</summary>
public sealed class ControllerApiRootDto
{
    /// <summary>Current aggregate version.</summary>
    [JsonPropertyName("version")] public long Version { get; init; }
    /// <summary>Current global settings.</summary>
    [JsonPropertyName("globalSettings")] public ControllerGlobalSettingsReadDto GlobalSettings { get; init; } = new();
    /// <summary>Routes currently configured in the controller.</summary>
    [JsonPropertyName("routes")] public ImmutableArray<ControllerRouteReadDto> Routes { get; init; } = ImmutableArray<ControllerRouteReadDto>.Empty;
    /// <summary>Services currently configured in the controller.</summary>
    [JsonPropertyName("services")] public ImmutableArray<ControllerServiceReadDto> Services { get; init; } = ImmutableArray<ControllerServiceReadDto>.Empty;
    /// <summary>Extensions currently registered with the controller.</summary>
    [JsonPropertyName("extensions")] public ImmutableArray<ControllerExtensionRecordReadDto> Extensions { get; init; } = ImmutableArray<ControllerExtensionRecordReadDto>.Empty;
}
/// <summary>Bounded JSON serialization and deserialization boundary.</summary>
public static class ControllerManagementJson
{
    /// <summary>Maximum serialized response body size in bytes.</summary>
    public const int MaximumResponseBodyBytes = 1024 * 1024;
    /// <summary>Maximum size in bytes of embedded JSON values.</summary>
    public const int MaximumEmbeddedJsonBytes = MaximumResponseBodyBytes;
    /// <summary>Serializer options used by the management API.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    internal static ReadOnlyMemory<byte> ResponseTooLargeBody => ResponseTooLargeEnvelopeBytes;

    /// <summary>Attempts to deserialize a management API JSON body.</summary>
    /// <typeparam name="T">Expected model type.</typeparam>
    /// <param name="body">UTF-8 JSON body to deserialize.</param>
    /// <param name="value">Deserialized value when successful; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the body is valid and produces a value; otherwise <see langword="false"/>.</returns>
    public static bool TryDeserialize<T>(ReadOnlyMemory<byte> body, out T? value)
    {
        value = default;
        if (body.IsEmpty || body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes) return false;
        try { value = JsonSerializer.Deserialize<T>(body.Span, Options); return value is not null; }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static ReadOnlyMemory<byte> AsReadOnlyMemory(ImmutableArray<byte> body) =>
        ImmutableCollectionsMarshal.AsArray(body) ?? ReadOnlyMemory<byte>.Empty;

    internal static bool TrySerialize(ControllerResponseEnvelope response, out byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(response);
        payload = Array.Empty<byte>();
        using var buffer = new BoundedBufferWriter(MaximumResponseBodyBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                JsonSerializer.Serialize(writer, response, Options);
                writer.Flush();
            }
            payload = buffer.ToArray();
            return true;
        }
        catch (ResponseTooLargeException) { return false; }
    }

    /// <summary>Serializes a response envelope, returning a bounded error envelope if it is too large.</summary>
    /// <param name="response">Response envelope to serialize.</param>
    /// <returns>UTF-8 JSON representation of the response envelope.</returns>
    public static byte[] Serialize(ControllerResponseEnvelope response) =>
        TrySerialize(response, out var payload) ? payload : ResponseTooLargeBody.ToArray();

    private static readonly byte[] ResponseTooLargeEnvelopeBytes = Encoding.UTF8.GetBytes(
        $"{{\"apiVersion\":{ControllerManagementApiContract.Version},\"ok\":false,\"code\":\"response_too_large\",\"message\":\"The management response is too large.\",\"data\":null,\"version\":null}}");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed class BoundedBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly int _maximumLength;
        private byte[]? _buffer;
        private int _written;
        internal BoundedBufferWriter(int maximumLength) { _maximumLength = maximumLength; _buffer = ArrayPool<byte>.Shared.Rent(maximumLength); }
        public void Advance(int count)
        {
            if (count < 0 || count > _maximumLength - _written) throw new ResponseTooLargeException();
            _written += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(BoundedBufferWriter));
            var remaining = _maximumLength - _written;
            if (remaining == 0) throw new ResponseTooLargeException();
            return buffer.AsMemory(_written, remaining);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        internal byte[] ToArray() => (_buffer ?? throw new ObjectDisposedException(nameof(BoundedBufferWriter))).AsSpan(0, _written).ToArray();
        public void Dispose()
        {
            if (_buffer is not { } buffer) return;
            _buffer = null; _written = 0; ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        private void ValidateSizeHint(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            if (sizeHint > _maximumLength - _written) throw new ResponseTooLargeException();
        }
    }
    private sealed class ResponseTooLargeException : Exception { }
}

/// <summary>Maps Host 1.2 records to stable public read/write DTOs.</summary>
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

    internal static ControllerExtensionRecordReadDto ToRead(ExtensionRecordConfiguration source) => new()
    {
        ExtensionId = source.ExtensionId, Version = source.Version, LoadState = (ControllerExtensionLoadState)source.LoadState,
        CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, RecordVersion = source.RecordVersion
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

/// <summary>Provides the Host route handler over the shared dispatcher.</summary>
public sealed class ControllerManagementHandler : IExtensionHandler
{
    private readonly IControllerManagementDispatcher _dispatcher;
    /// <summary>Initializes a management handler over the supplied dispatcher.</summary>
    /// <param name="dispatcher">Dispatcher that processes admitted management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    public ControllerManagementHandler(IControllerManagementDispatcher dispatcher, ControllerOptions options) { _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)); ArgumentNullException.ThrowIfNull(options); }
    /// <summary>Stable identifier advertised by this handler.</summary>
    public string HandlerId => ControllerManagementApiContract.HandlerId;
    /// <summary>Handles one extension-handler request.</summary>
    /// <param name="request">Incoming extension-handler request.</param>
    /// <param name="cancellationToken">Token used to cancel request dispatch.</param>
    /// <returns>The extension-handler response produced by the management dispatcher.</returns>
    public async ValueTask<ExtensionHandlerResponse> HandleAsync(ExtensionHandlerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var presentedKey = ReadApiKey(request.Headers);
        if (!ControllerAdmissionLimits.TryCreateRequest(ControllerTransport.HostRoute, request.Method, request.Path, presentedKey,
            request.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)), ControllerManagementJson.AsReadOnlyMemory(request.Body), out var managementRequest) || managementRequest is null)
            return ToExtensionResponse(ControllerManagementResponseBuilder.InvalidRequest);
        var response = await _dispatcher.DispatchAsync(managementRequest, cancellationToken).ConfigureAwait(false);
        return ToExtensionResponse(response);
    }
    private static string? ReadApiKey(IReadOnlyDictionary<string, ImmutableArray<string>> headers) => headers.TryGetValue(ControllerManagementApiContract.ApiKeyHeaderName, out var values) && values.Length == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
    private static ExtensionHandlerResponse ToExtensionResponse(ControllerManagementResponse response) => new(response.StatusCode, response.Headers.Select(static pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)), ControllerManagementJson.AsReadOnlyMemory(response.Body));
}

/// <summary>Creates management handlers for controller transports.</summary>
public sealed class ControllerManagementHandlerFactory : IControllerManagementHandlerFactory
{
    /// <summary>Creates a management handler using the supplied dispatcher and options.</summary>
    /// <param name="dispatcher">Dispatcher that processes management requests.</param>
    /// <param name="options">Controller options used by the handler.</param>
    /// <returns>A configured management handler.</returns>
    public IExtensionHandler Create(IControllerManagementDispatcher dispatcher, ControllerOptions options) => new ControllerManagementHandler(dispatcher, options);
}


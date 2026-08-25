using System.Text.Json.Serialization;

namespace Nekolla.Nekostick.Controller.Management;

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

/// <summary>Identifies the safe lifecycle state of a supervised service runtime.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerServiceLifecycleState>))]
public enum ControllerServiceLifecycleState
{
    /// <summary>No lifecycle observation is available.</summary>
    Unknown,
    /// <summary>The service is disabled.</summary>
    Disabled,
    /// <summary>The service is starting.</summary>
    Starting,
    /// <summary>The service is running.</summary>
    Running,
    /// <summary>The service is stopping.</summary>
    Stopping,
    /// <summary>The service failed to start or remain healthy.</summary>
    Failed
}

/// <summary>Identifies the safe health state of a supervised service runtime.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControllerServiceHealthState>))]
public enum ControllerServiceHealthState
{
    /// <summary>No health observation is available.</summary>
    Unknown,
    /// <summary>The latest health observation succeeded.</summary>
    Healthy,
    /// <summary>The latest health observation failed.</summary>
    Unhealthy
}

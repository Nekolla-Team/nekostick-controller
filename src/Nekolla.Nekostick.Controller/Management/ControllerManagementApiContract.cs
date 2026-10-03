namespace Nekolla.Nekostick.Controller.Management;

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
    /// <summary>Path for Host-wide service runtime telemetry.</summary>
    public const string ServicesRuntimePath = "/v1/services/runtime";
    /// <summary>Template path for one service runtime telemetry snapshot.</summary>
    public const string ServiceRuntimePath = "/v1/services/{id}/runtime";
    /// <summary>Template path for resuming one service on the local host.</summary>
    public const string ServiceRuntimeResumePath = "/v1/services/{id}/runtime/resume";
    /// <summary>Template path for strictly restarting one service on the local host.</summary>
    public const string ServiceRuntimeRestartPath = "/v1/services/{id}/runtime/restart";
    /// <summary>Path for subscribing to node-local service runtime-state changes as Server-Sent Events.</summary>
    public const string ServiceRuntimeFeedPath = "/v1/services/runtime/stream";
    /// <summary>Template path for streaming one service's live output over WebSocket.</summary>
    public const string ServiceOutputStreamPath = "/v1/services/{id}/output/stream";
    /// <summary>Query parameter selecting the output stream (stdout or stderr).</summary>
    public const string ServiceOutputStreamQueryParameter = "stream";
    /// <summary>Path suffix shared by every transport's service-output stream endpoint.</summary>
    public const string ServiceOutputStreamSuffix = "/output/stream";
    /// <summary>Media type of the Server-Sent Events framing used on the HostRoute transport.</summary>
    public const string ServiceOutputEventStreamMediaType = "text/event-stream";
    /// <summary>
    /// <c>Sec-WebSocket-Protocol</c> token prefix that carries the base64url-encoded API key for
    /// browser WebSocket clients, which cannot set request headers on an upgrade.
    /// </summary>
    public const string ServiceOutputKeySubProtocolPrefix = "nekostick.controller.key.";
    /// <summary>Path for extension operations.</summary>
    public const string ExtensionsPath = "/v1/extensions";
    /// <summary>Path of the extension directory refresh endpoint.</summary>
    public const string ExtensionsRefreshPath = "/v1/extensions/refresh";
    /// <summary>Path of the streaming extension package install endpoint.</summary>
    public const string ExtensionsInstallPath = "/v1/extensions/install";
    /// <summary>Path for unversioned controller runtime state.</summary>
    public const string StatePath = "/v1/controller/state";
    /// <summary>Path for unversioned controller and host telemetry.</summary>
    public const string TelemetryPath = "/v1/controller/telemetry";
    /// <summary>Path for hot controller settings reload.</summary>
    public const string ReloadSettingsPath = "/v1/controller/reload-settings";
    /// <summary>HTTP header carrying a resource entity tag.</summary>
    public const string ETagHeaderName = "etag";
    /// <summary>HTTP header used to supply the entity tag required for a conditional update.</summary>
    public const string IfMatchHeaderName = "if-match";
    /// <summary>HTTP header carrying the location of a newly created resource.</summary>
    public const string LocationHeaderName = "location";
}

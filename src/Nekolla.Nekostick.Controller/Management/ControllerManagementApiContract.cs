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
    /// <summary>Path for extension operations.</summary>
    public const string ExtensionsPath = "/v1/extensions";
    /// <summary>Path of the extension directory refresh endpoint.</summary>
    public const string ExtensionsRefreshPath = "/v1/extensions/refresh";
    /// <summary>Path for unversioned controller runtime state.</summary>
    public const string StatePath = "/v1/controller/state";
    /// <summary>Path for hot controller settings reload.</summary>
    public const string ReloadSettingsPath = "/v1/controller/reload-settings";
    /// <summary>HTTP header carrying a resource entity tag.</summary>
    public const string ETagHeaderName = "etag";
    /// <summary>HTTP header used to supply the entity tag required for a conditional update.</summary>
    public const string IfMatchHeaderName = "if-match";
    /// <summary>HTTP header carrying the location of a newly created resource.</summary>
    public const string LocationHeaderName = "location";
}

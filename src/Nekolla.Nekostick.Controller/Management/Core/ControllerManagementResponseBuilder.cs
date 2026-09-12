using System.Collections.Immutable;
using System.Globalization;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

/// <summary>Maps bridge result categories into bounded response envelopes.</summary>
internal static class ControllerManagementResponseBuilder
{
    private static readonly KeyValuePair<string, IEnumerable<string>> JsonContentType = new("content-type", new[] { "application/json; charset=utf-8" });
    private static readonly KeyValuePair<string, IEnumerable<string>> NoStore = new("cache-control", new[] { "no-store" });
    internal static ControllerManagementResponse Success(object? data, long version, int statusCode = 200, string? location = null) => Create(statusCode, ControllerDispatchCode.Success, new ControllerResponseEnvelope { Ok = true, Code = "ok", Message = "The operation completed.", Data = data, Version = version }, version, location);
    internal static ControllerManagementResponse SuccessUnversioned(object? data) => Create(200, ControllerDispatchCode.Success, new ControllerResponseEnvelope { Ok = true, Code = "ok", Message = "The operation completed.", Data = data, Version = null });
    internal static ControllerManagementResponse NoContent(long version) => new(204, ControllerDispatchCode.Success, new[] { JsonContentType, NoStore, ETag(version) });
    internal static ControllerManagementResponse Error(int statusCode, ControllerDispatchCode dispatchCode, string code, string message) => Create(statusCode, dispatchCode, new ControllerResponseEnvelope { Ok = false, Code = code, Message = message });
    private static ControllerManagementResponse Create(int statusCode, ControllerDispatchCode dispatchCode, ControllerResponseEnvelope envelope, long? version = null, string? location = null)
    {
        var headers = new List<KeyValuePair<string, IEnumerable<string>>> { JsonContentType, NoStore };
        if (version is { } currentVersion) headers.Add(ETag(currentVersion));
        if (location is not null) headers.Add(new KeyValuePair<string, IEnumerable<string>>(ControllerManagementApiContract.LocationHeaderName, new[] { location }));
        if (ControllerManagementJson.TrySerialize(envelope, out var body)) return new ControllerManagementResponse(statusCode, dispatchCode, headers, body);
        return ResponseTooLarge;
    }
    private static KeyValuePair<string, IEnumerable<string>> ETag(long version) => new(ControllerManagementApiContract.ETagHeaderName, new[] { $"\"{version.ToString(CultureInfo.InvariantCulture)}\"" });
    private static ControllerManagementResponse ResponseTooLarge => new(503, ControllerDispatchCode.Unavailable, new[] { JsonContentType, NoStore }, ControllerManagementJson.ResponseTooLargeBody);
    internal static ControllerManagementResponse FromConfigurationErrors(ImmutableArray<ConfigurationError> errors)
    {
        var error = errors.IsDefaultOrEmpty ? null : errors[0];
        return error?.Code switch
        {
            ConfigurationErrorCode.Validation => InvalidRequest,
            ConfigurationErrorCode.ConcurrencyConflict => PreconditionFailed,
            ConfigurationErrorCode.NotFound => NotFound,
            ConfigurationErrorCode.Unsupported => Unsupported,
            ConfigurationErrorCode.StorageUnavailable => StorageUnavailable,
            _ => StorageUnavailable
        };
    }
    internal static ControllerManagementResponse InvalidRequest => Error(400, ControllerDispatchCode.InvalidRequest, "invalid_request", "The management request is invalid.");
    internal static ControllerManagementResponse Unauthorized => Error(401, ControllerDispatchCode.Unauthorized, "unauthorized", "The management API key is invalid.");
    internal static ControllerManagementResponse TransportDisabled => Error(404, ControllerDispatchCode.TransportDisabled, "transport_disabled", "The management transport is disabled.");
    internal static ControllerManagementResponse NotFound => Error(404, ControllerDispatchCode.NotFound, "not_found", "The management resource was not found.");
    /// <summary>Reports a recorded extension whose settings document has not been created.</summary>
    /// <remarks>
    /// The response carries the aggregate ETag so a client can create the document with one
    /// conditional PUT directly from this answer.
    /// </remarks>
    internal static ControllerManagementResponse SettingsAbsent(long version) => Create(404, ControllerDispatchCode.NotFound, new ControllerResponseEnvelope { Ok = false, Code = "no_settings", Message = "The extension settings document has not been created." }, version);
    internal static ControllerManagementResponse Conflict => Error(409, ControllerDispatchCode.Conflict, "conflict", "The management resource conflicts with current state.");
    internal static ControllerManagementResponse PreconditionRequired => Error(428, ControllerDispatchCode.InvalidRequest, "precondition_required", "Exactly one If-Match precondition is required.");
    internal static ControllerManagementResponse PreconditionFailed => Error(412, ControllerDispatchCode.Conflict, "precondition_failed", "The supplied If-Match precondition is stale.");
    internal static ControllerManagementResponse ReservedRoute => Error(409, ControllerDispatchCode.Conflict, "reserved_route", "The controller management route is reserved.");
    internal static ControllerManagementResponse DowngradeForbidden => Error(409, ControllerDispatchCode.Conflict, "downgrade_forbidden", "The installed extension version is newer than the uploaded package.");
    internal static ControllerManagementResponse InvalidRequestWithReason(string reason) => Error(400, ControllerDispatchCode.InvalidRequest, "invalid_request", reason);
    internal static ControllerManagementResponse DowngradeForbiddenWithReason(string reason) => Error(409, ControllerDispatchCode.Conflict, "downgrade_forbidden", reason);
    internal static ControllerManagementResponse Unsupported => Error(501, ControllerDispatchCode.Unsupported, "unsupported", "The management operation is unsupported.");
    internal static ControllerManagementResponse MethodNotAllowed => Error(405, ControllerDispatchCode.InvalidRequest, "method_not_allowed", "The management method is not supported.");
    internal static ControllerManagementResponse Unavailable => Error(503, ControllerDispatchCode.Unavailable, "unavailable", "The controller management service is unavailable.");
    internal static ControllerManagementResponse StorageUnavailable => Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", "The configuration store is unavailable.");
    internal static ControllerManagementResponse StorageUnavailableWithRestore(bool restored) => Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", restored ? "The extension install failed; the previous installation was restored." : "The extension install failed; the previous installation could not be restored.");
}


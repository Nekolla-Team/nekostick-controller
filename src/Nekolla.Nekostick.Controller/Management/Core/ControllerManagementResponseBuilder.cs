using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
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
    internal static ControllerManagementResponse Error(int statusCode, ControllerDispatchCode dispatchCode, string code, string message, ControllerErrorDetails details, IReadOnlyList<ControllerFieldError>? errors = null) => Create(statusCode, dispatchCode, new ControllerResponseEnvelope { Ok = false, Code = code, Message = message, Details = details, Errors = errors });
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
        if (errors.IsDefaultOrEmpty)
        {
            return Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", "The configuration store failed without providing error details.", Details("storage_io_failure"));
        }

        var includeFieldErrors = errors.Length > 1 || errors.Any(static error => error.Code == ConfigurationErrorCode.Validation);
        var fieldErrors = includeFieldErrors
            ? errors.Select(error => new ControllerFieldError
            {
                Field = string.Empty,
                Reason = ConfigurationErrorReason(error),
                Message = ConfigurationErrorMessage(error)
            }).ToArray()
            : null;
        var firstError = errors[0];
        var firstMessage = ConfigurationErrorMessage(firstError);
        var message = $"The configuration operation failed with {errors.Length} error{(errors.Length == 1 ? string.Empty : "s")}. First error: {firstMessage}";
        var details = new ControllerErrorDetails { Reason = ConfigurationErrorReason(firstError) };
        return firstError.Code switch
        {
            ConfigurationErrorCode.Validation => Error(400, ControllerDispatchCode.InvalidRequest, "invalid_request", message, details, fieldErrors),
            ConfigurationErrorCode.ConcurrencyConflict => Error(412, ControllerDispatchCode.Conflict, "precondition_failed", message, details, fieldErrors),
            ConfigurationErrorCode.NotFound => Error(404, ControllerDispatchCode.NotFound, "not_found", message, details, fieldErrors),
            ConfigurationErrorCode.NoSettings => Error(404, ControllerDispatchCode.NotFound, "no_settings", message, details, fieldErrors),
            ConfigurationErrorCode.Unsupported => Error(501, ControllerDispatchCode.Unsupported, "unsupported", message, details, fieldErrors),
            ConfigurationErrorCode.StorageUnavailable => Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", message, details, fieldErrors),
            _ => Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", message, details, fieldErrors)
        };
    }
    internal static ControllerManagementResponse InvalidRequest(string reason, string? message = null, string? parameter = null) => BuildError(400, ControllerDispatchCode.InvalidRequest, "invalid_request", reason, message, "The management request contains an invalid value.", parameter);
    internal static ControllerManagementResponse Unauthorized(string reason = "api_key_invalid", string? message = null, string? parameter = null) => BuildError(401, ControllerDispatchCode.Unauthorized, "unauthorized", reason, message, "The management API key is invalid.", parameter);
    internal static ControllerManagementResponse TransportDisabled(string reason = "transport_disabled", string? message = null, string? parameter = null) => BuildError(404, ControllerDispatchCode.TransportDisabled, "transport_disabled", reason, message, "The management transport is disabled.", parameter);
    internal static ControllerManagementResponse NotFound(string reason = "unknown_path", string? message = null, string? parameter = null) => BuildError(404, ControllerDispatchCode.NotFound, "not_found", reason, message, "No management resource matches the requested path.", parameter);
    /// <summary>Reports a recorded extension whose settings document has not been created.</summary>
    /// <remarks>
    /// The response carries the aggregate ETag so a client can create the document with one
    /// conditional PUT directly from this answer.
    /// </remarks>
    internal static ControllerManagementResponse SettingsAbsent(long version, string reason = "settings_absent", string? message = null, string? parameter = null) => Create(404, ControllerDispatchCode.NotFound, new ControllerResponseEnvelope { Ok = false, Code = "no_settings", Message = message ?? "The extension settings document has not been created.", Details = Details(reason, parameter) }, version);
    internal static ControllerManagementResponse Conflict(string reason = "state_conflict", string? message = null, string? parameter = null) => BuildError(409, ControllerDispatchCode.Conflict, "conflict", reason, message, "The management operation conflicts with the current state.", parameter);
    /// <summary>Reports a configured service without a live output pump to stream from.</summary>
    internal static ControllerManagementResponse ServiceNotRunning(string reason = "not_running", string? message = null, string? parameter = null) => BuildError(409, ControllerDispatchCode.Conflict, "not_running", reason, message, "The service has no live output to stream.", parameter);
    internal static ControllerManagementResponse PreconditionRequired(string reason = "if_match_missing", string? message = null, string? parameter = null) => BuildError(428, ControllerDispatchCode.InvalidRequest, "precondition_required", reason, message, "The If-Match header is required for this operation.", parameter);
    internal static ControllerManagementResponse PreconditionFailed(string reason = "if_match_stale", string? message = null, string? parameter = null) => BuildError(412, ControllerDispatchCode.Conflict, "precondition_failed", reason, message, "The supplied If-Match version is stale.", parameter);
    internal static ControllerManagementResponse ReservedRoute(string reason = "reserved_route", string? message = null, string? parameter = null) => BuildError(409, ControllerDispatchCode.Conflict, "reserved_route", reason, message, "The controller management route is reserved.", parameter);
    internal static ControllerManagementResponse DowngradeForbidden(string reason = "downgrade_forbidden", string? message = null, string? parameter = null) => BuildError(409, ControllerDispatchCode.Conflict, "downgrade_forbidden", reason, message, "The installed extension version is newer than the uploaded package.", parameter);
    internal static ControllerManagementResponse InvalidRequestWithReason(string reason, string message, string? parameter = null) => InvalidRequest(reason, message, parameter);
    internal static ControllerManagementResponse DowngradeForbiddenWithReason(string reason, string message, string? parameter = null) => DowngradeForbidden(reason, message, parameter);
    internal static ControllerManagementResponse Unsupported(string reason = "operation_not_supported", string? message = null, string? parameter = null) => BuildError(501, ControllerDispatchCode.Unsupported, "unsupported", reason, message, "The management operation is not supported.", parameter);
    internal static ControllerManagementResponse MethodNotAllowed(string reason = "method_not_allowed", string? message = null, string? parameter = null) => BuildError(405, ControllerDispatchCode.InvalidRequest, "method_not_allowed", reason, message, "The management method is not supported for this path.", parameter);
    internal static ControllerManagementResponse Unavailable(string reason, string? message = null, string? parameter = null) => BuildError(503, ControllerDispatchCode.Unavailable, "unavailable", reason, message, "The controller management service is unavailable.", parameter);
    internal static ControllerManagementResponse StorageUnavailable(string reason = "storage_io_failure", string? message = null, string? parameter = null, string? traceId = null) => Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", message ?? "The configuration store is unavailable.", Details(reason, parameter, traceId));
    internal static ControllerManagementResponse StorageUnavailableWithRestore(bool restored, string? reason = null, string? message = null, string? parameter = null, string? traceId = null) => Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", message ?? (restored ? "The extension install failed; the previous installation was restored." : "The extension install failed; the previous installation could not be restored."), Details(reason ?? (restored ? "install_failed_restored" : "install_failed_restore_failed"), parameter, traceId));
    private static ControllerManagementResponse BuildError(int statusCode, ControllerDispatchCode dispatchCode, string code, string reason, string? message, string defaultMessage, string? parameter = null) => Error(statusCode, dispatchCode, code, message ?? defaultMessage, Details(reason, parameter));
    private static ControllerErrorDetails Details(string reason, string? parameter = null, string? traceId = null) => new() { Reason = reason, Parameter = parameter, TraceId = traceId };
    private static string ConfigurationErrorReason(ConfigurationError error) => error.Code switch
    {
        ConfigurationErrorCode.Validation => "invalid_value",
        ConfigurationErrorCode.ConcurrencyConflict => "if_match_stale",
        ConfigurationErrorCode.NotFound => "unknown_resource",
        ConfigurationErrorCode.NoSettings => "settings_absent",
        ConfigurationErrorCode.Unsupported => "host_api_unsupported",
        ConfigurationErrorCode.StorageUnavailable => "storage_io_failure",
        _ => "unknown_configuration_error"
    };
    private static string ConfigurationErrorMessage(ConfigurationError error) => error.Message;
}


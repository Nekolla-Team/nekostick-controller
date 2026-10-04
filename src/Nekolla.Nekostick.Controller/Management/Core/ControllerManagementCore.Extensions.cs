using System.Collections.Immutable;
using System.Linq;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    private IExtensionManagementApi? ExtensionManagement => (_bridge as IExtensionHostBridge13)?.Management;

    private async ValueTask<ControllerManagementResponse> ReadExtensionsAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported(reason: "extension_management_unavailable", message: "The host does not provide the extension management API.");
        var read = await management.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var records = read.Value.Select(MapExtensionEntry).ToImmutableArray();
        return ControllerManagementResponseBuilder.SuccessUnversioned(records);
    }

    private async ValueTask<ControllerManagementResponse> ReadExtensionAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported(reason: "extension_management_unavailable", message: "The host does not provide the extension management API.");
        var read = await management.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var extension = read.Value.FirstOrDefault(entry => string.Equals(entry.ExtensionId, extensionId, StringComparison.Ordinal));
        if (extension is null) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_extension", message: $"Extension '{extensionId}' was not found.", parameter: extensionId);
        var mapped = MapExtensionEntry(extension);
        return ControllerManagementResponseBuilder.SuccessUnversioned(mapped);
    }
    /// <summary>Maps a management entry, including only the members the negotiated host API actually provides.</summary>
    private ControllerExtensionRecordReadDto MapExtensionEntry(ExtensionManagementEntry entry) =>
        _bridge is IExtensionHostBridge13 bridge13
            ? ExtensionHostApiSupport.IsApi14Supported(bridge13.ApiVersion)
                ? ControllerContractMapper.ToReadApi14(entry)
                : ExtensionHostApiSupport.IsApi133Supported(bridge13.ApiVersion)
                    ? ControllerContractMapper.ToReadApi133(entry)
                    : ControllerContractMapper.ToRead(entry)
            : ControllerContractMapper.ToRead(entry);

    private async ValueTask<ControllerManagementResponse> RefreshExtensionsAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported(reason: "extension_management_unavailable", message: "The host does not provide the extension management API.");
        var read = await management.RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } summary) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var mapped = _bridge is IExtensionHostBridge13 bridge13 && ExtensionHostApiSupport.IsApi134Supported(bridge13.ApiVersion)
            ? ControllerContractMapper.ToReadApi134(summary)
            : ControllerContractMapper.ToRead(summary);
        return ControllerManagementResponseBuilder.SuccessUnversioned(mapped);
    }

    private static async ValueTask<ControllerManagementResponse> InstallExtensionAsync(Stream body, CancellationToken cancellationToken)
    {
        var result = await ControllerExtensionInstaller.InstallAsync(body, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != ControllerExtensionInstallOutcome.Installed)
        {
            return result.Outcome switch
            {
                ControllerExtensionInstallOutcome.InvalidPackage => ControllerManagementResponseBuilder.InvalidRequestWithReason(
                    result.ErrorReason ?? "invalid_package",
                    result.Reason ?? "The uploaded extension package is invalid.",
                    "package"),
                ControllerExtensionInstallOutcome.DowngradeForbidden => result.Reason is { } downgradeMessage
                    ? ControllerManagementResponseBuilder.DowngradeForbiddenWithReason("downgrade_forbidden", downgradeMessage, "package.version")
                    : ControllerManagementResponseBuilder.DowngradeForbidden(reason: "downgrade_forbidden", message: "The uploaded package version is older than the installed extension version.", parameter: "package.version"),
                _ => result.RestoreSucceeded is { } restored
                    ? ControllerManagementResponseBuilder.StorageUnavailableWithRestore(
                        restored,
                        reason: result.ErrorReason,
                        parameter: "package",
                        traceId: result.TraceId)
                    : ControllerManagementResponseBuilder.StorageUnavailable(
                        reason: result.ErrorReason ?? "storage_io_failure",
                        message: "The extension package could not be installed because configuration storage is unavailable.",
                        parameter: "package",
                        traceId: result.TraceId)
            };
        }

        return ControllerManagementResponseBuilder.SuccessUnversioned(new ControllerExtensionInstallResultDto
        {
            Id = result.Id ?? string.Empty,
            Version = result.Version ?? string.Empty,
            Replaced = result.Replaced
        });
    }

    private async ValueTask<ControllerManagementResponse> WriteExtensionLifecycleAsync(ControllerManagementRequest request, string method, string extensionId, string action, CancellationToken cancellationToken)
    {
        var expectedMethod = action == "record" ? "DELETE" : "POST";
        if (method != expectedMethod) return ControllerManagementResponseBuilder.MethodNotAllowed(message: $"The {method} method is not allowed for the '{action}' extension action.", parameter: method);
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported(reason: "extension_management_unavailable", message: "The host does not provide the extension management API.");
        if (action == "disable" &&
            _options.PreventSelfDisable &&
            string.Equals(extensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal))
        {
            // Self-disable takes the management API offline until someone flips the record back on
            // the Host side; the operator opted into rejecting it via preventSelfDisable.
            return ControllerManagementResponseBuilder.InvalidRequestWithReason(
                "self_disable_forbidden",
                "Disabling the controller extension is forbidden while preventSelfDisable is enabled.",
                "preventSelfDisable");
        }

        if (action == "reload") return await ReloadExtensionAsync(request, management, extensionId, cancellationToken).ConfigureAwait(false);
        var write = action switch
        {
            "enable" => await management.EnableAsync(extensionId, cancellationToken).ConfigureAwait(false),
            "disable" => await management.DisableAsync(extensionId, cancellationToken).ConfigureAwait(false),
            _ => await management.DeleteRecordAsync(extensionId, cancellationToken).ConfigureAwait(false),
        };
        return write.IsSuccess ? ControllerManagementResponseBuilder.SuccessUnversioned(null) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private static async ValueTask<ControllerManagementResponse> ReloadExtensionAsync(
        ControllerManagementRequest request,
        IExtensionManagementApi management,
        string extensionId,
        CancellationToken cancellationToken)
    {
        if (request.Transport != ControllerTransport.HostRoute)
        {
            var write = await management.ReloadAsync(extensionId, cancellationToken).ConfigureAwait(false);
            return write.IsSuccess
                ? ControllerManagementResponseBuilder.SuccessUnversioned(new ControllerExtensionReloadReadDto { Outcome = "reloaded" })
                : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
        }

        // The Host invokes this transport inside its own extension route callback, where the
        // synchronous reload is vetoed and scheduling is the only supported entry point. A
        // scheduled reload never awaits generation replacement, so the outcome says so.
        var read = await management.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var entry = read.Value.FirstOrDefault(candidate => string.Equals(candidate.ExtensionId, extensionId, StringComparison.Ordinal));
        if (entry is null) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_extension", message: $"Extension '{extensionId}' was not found.", parameter: extensionId);
        if (entry.LoadState != ExtensionLoadState.Loaded) return ControllerManagementResponseBuilder.InvalidRequest("extension_not_loaded", "The extension must be loaded before it can be reloaded.", "extensionId");
        if (management.ReloadSoon(extensionId)) return ControllerManagementResponseBuilder.SuccessUnversioned(new ControllerExtensionReloadReadDto { Outcome = "scheduled" });
        return ControllerManagementResponseBuilder.Unsupported(reason: "reload_not_scheduled", message: $"The host could not schedule a reload for extension '{extensionId}'.", parameter: extensionId);
    }

    private static bool TryGetExtensionActionPath(string path, out string extensionId, out string action)
    {
        extensionId = string.Empty;
        action = string.Empty;
        if (!path.StartsWith(ControllerManagementApiContract.ExtensionsPath + "/", StringComparison.Ordinal)) return false;
        var rest = path[(ControllerManagementApiContract.ExtensionsPath.Length + 1)..];
        var separator = rest.IndexOf('/');
        if (separator <= 0 || separator == rest.Length - 1) return false;
        var candidate = rest[(separator + 1)..];
        if (candidate is not ("enable" or "disable" or "reload" or "record")) return false;
        extensionId = Uri.UnescapeDataString(rest[..separator]);
        action = candidate;
        return true;
    }

    private async ValueTask<ControllerManagementResponse> ReadExtensionSettingsAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (!snapshot.ExtensionRecords.Any(record => string.Equals(record.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_extension", message: $"Extension '{extensionId}' was not found.", parameter: extensionId);
        var settings = snapshot.ExtensionSettings.FirstOrDefault(item => string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal));
        if (settings is null) return ControllerManagementResponseBuilder.SettingsAbsent(snapshot.Version, message: $"The settings document for extension '{extensionId}' has not been created.", parameter: extensionId);
        return ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(settings), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> PutExtensionSettingsAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (ValidateJsonBody(request) is { } bodyError) return bodyError;
        if (!ControllerManagementJson.TryDeserialize<ControllerExtensionSettingsWriteDto>(ControllerManagementJson.AsReadOnlyMemory(request.Body), out var payload) || payload is null) return InvalidJsonBodyResponse(request.Body, "extension settings");
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed(reason: "if_match_stale", message: $"The If-Match version {expectedVersion} is stale; the current configuration version is {snapshot.Version}.", parameter: "If-Match");
        if (!snapshot.ExtensionRecords.Any(record => string.Equals(record.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_extension", message: $"Extension '{extensionId}' was not found.", parameter: extensionId);
        var current = snapshot.ExtensionSettings.FirstOrDefault(item => string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal));
        var settings = ControllerContractMapper.ToContract(payload, extensionId, current?.Version ?? 0);
        var replacement = snapshot.ExtensionSettings.Where(item => !string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal)).Append(settings).ToImmutableArray();
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, extensionSettings: replacement), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(settings), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> DeleteExtensionSettingsAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed(reason: "if_match_stale", message: $"The If-Match version {expectedVersion} is stale; the current configuration version is {snapshot.Version}.", parameter: "If-Match");
        if (!snapshot.ExtensionRecords.Any(record => string.Equals(record.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_extension", message: $"Extension '{extensionId}' was not found.", parameter: extensionId);
        if (!snapshot.ExtensionSettings.Any(item => string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound(reason: "settings_absent", message: $"Settings for extension '{extensionId}' were not found.", parameter: extensionId);
        var replacement = snapshot.ExtensionSettings.Where(item => !string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal)).ToImmutableArray();
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, extensionSettings: replacement), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.NoContent(write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

}

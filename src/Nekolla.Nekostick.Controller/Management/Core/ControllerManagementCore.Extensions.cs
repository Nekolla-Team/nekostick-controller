using System.Collections.Immutable;
using System.Linq;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    private IExtensionManagementApi? ExtensionManagement => (_bridge as IExtensionHostBridge13)?.Management;

    private async ValueTask<ControllerManagementResponse> ReadExtensionsAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported;
        var read = await management.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var records = _bridge is IExtensionHostBridge13 bridge13 && ExtensionHostApiSupport.IsApi133Supported(bridge13.ApiVersion)
            ? read.Value.Select(ControllerContractMapper.ToReadApi133).ToImmutableArray()
            : read.Value.Select(ControllerContractMapper.ToRead).ToImmutableArray();
        return ControllerManagementResponseBuilder.SuccessUnversioned(records);
    }

    private async ValueTask<ControllerManagementResponse> ReadExtensionAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported;
        var read = await management.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var extension = read.Value.FirstOrDefault(entry => string.Equals(entry.ExtensionId, extensionId, StringComparison.Ordinal));
        if (extension is null) return ControllerManagementResponseBuilder.NotFound;
        var mapped = _bridge is IExtensionHostBridge13 bridge13 && ExtensionHostApiSupport.IsApi133Supported(bridge13.ApiVersion)
            ? ControllerContractMapper.ToReadApi133(extension)
            : ControllerContractMapper.ToRead(extension);
        return ControllerManagementResponseBuilder.SuccessUnversioned(mapped);
    }

    private async ValueTask<ControllerManagementResponse> RefreshExtensionsAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported;
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
                ControllerExtensionInstallOutcome.InvalidPackage => result.Reason is { } invalidReason
                    ? ControllerManagementResponseBuilder.InvalidRequestWithReason(invalidReason)
                    : ControllerManagementResponseBuilder.InvalidRequest,
                ControllerExtensionInstallOutcome.DowngradeForbidden => result.Reason is { } downgradeReason
                    ? ControllerManagementResponseBuilder.DowngradeForbiddenWithReason(downgradeReason)
                    : ControllerManagementResponseBuilder.DowngradeForbidden,
                _ => result.RestoreSucceeded is { } restored
                    ? ControllerManagementResponseBuilder.StorageUnavailableWithRestore(restored)
                    : ControllerManagementResponseBuilder.StorageUnavailable
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
        if (method != expectedMethod) return ControllerManagementResponseBuilder.MethodNotAllowed;
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported;
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
        if (entry is null) return ControllerManagementResponseBuilder.NotFound;
        if (entry.LoadState != ExtensionLoadState.Loaded) return ControllerManagementResponseBuilder.InvalidRequest;
        return management.ReloadSoon(extensionId)
            ? ControllerManagementResponseBuilder.SuccessUnversioned(new ControllerExtensionReloadReadDto { Outcome = "scheduled" })
            : ControllerManagementResponseBuilder.Unsupported;
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
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (!snapshot.ExtensionRecords.Any(record => string.Equals(record.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound;
        var settings = snapshot.ExtensionSettings.FirstOrDefault(item => string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal));
        return settings is null
            ? ControllerManagementResponseBuilder.SettingsAbsent(snapshot.Version)
            : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(settings), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> PutExtensionSettingsAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!HasJsonBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (!ControllerManagementJson.TryDeserialize<ControllerExtensionSettingsWriteDto>(ControllerManagementJson.AsReadOnlyMemory(request.Body), out var payload) || payload is null) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        if (!snapshot.ExtensionRecords.Any(record => string.Equals(record.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound;
        var current = snapshot.ExtensionSettings.FirstOrDefault(item => string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal));
        var settings = ControllerContractMapper.ToContract(payload, extensionId, current?.Version ?? 0);
        var replacement = snapshot.ExtensionSettings.Where(item => !string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal)).Append(settings).ToImmutableArray();
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, extensionSettings: replacement), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(settings), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> DeleteExtensionSettingsAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        if (!snapshot.ExtensionRecords.Any(record => string.Equals(record.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound;
        if (!snapshot.ExtensionSettings.Any(item => string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal))) return ControllerManagementResponseBuilder.NotFound;
        var replacement = snapshot.ExtensionSettings.Where(item => !string.Equals(item.ExtensionId, extensionId, StringComparison.Ordinal)).ToImmutableArray();
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, extensionSettings: replacement), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.NoContent(write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

}

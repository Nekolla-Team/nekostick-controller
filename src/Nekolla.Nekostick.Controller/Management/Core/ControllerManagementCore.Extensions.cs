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
        return read.IsSuccess && read.Value is { } summary
            ? ControllerManagementResponseBuilder.SuccessUnversioned(ControllerContractMapper.ToRead(summary))
            : ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
    }

    private async ValueTask<ControllerManagementResponse> WriteExtensionLifecycleAsync(ControllerManagementRequest request, string method, string extensionId, string action, CancellationToken cancellationToken)
    {
        var expectedMethod = action == "record" ? "DELETE" : "POST";
        if (method != expectedMethod) return ControllerManagementResponseBuilder.MethodNotAllowed;
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (ExtensionManagement is not { } management) return ControllerManagementResponseBuilder.Unsupported;
        var write = action switch
        {
            "enable" => await management.EnableAsync(extensionId, cancellationToken).ConfigureAwait(false),
            "disable" => await management.DisableAsync(extensionId, cancellationToken).ConfigureAwait(false),
            "reload" => await management.ReloadAsync(extensionId, cancellationToken).ConfigureAwait(false),
            _ => await management.DeleteRecordAsync(extensionId, cancellationToken).ConfigureAwait(false),
        };
        return write.IsSuccess ? ControllerManagementResponseBuilder.SuccessUnversioned(null) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
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
        return settings is null ? ControllerManagementResponseBuilder.NotFound : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(settings), snapshot.Version);
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

using System.Collections.Immutable;
using System.Linq;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    private async ValueTask<ControllerManagementResponse> ReadExtensionsAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return ControllerManagementResponseBuilder.Success(snapshot.ExtensionRecords.Select(ControllerContractMapper.ToRead).ToImmutableArray(), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> ReadExtensionAsync(ControllerManagementRequest request, string extensionId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var extension = snapshot.ExtensionRecords.FirstOrDefault(record => string.Equals(record.ExtensionId, extensionId, StringComparison.Ordinal));
        return extension is null ? ControllerManagementResponseBuilder.NotFound : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(extension), snapshot.Version);
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

using System.Collections.Immutable;
using System.Linq;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    private async ValueTask<ControllerManagementResponse> ReadRootAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var data = new ControllerApiRootDto { Version = snapshot.Version, GlobalSettings = ControllerContractMapper.ToRead(snapshot.GlobalSettings), Routes = snapshot.Routes.Where(route => !IsReservedRoute(route)).Select(ControllerContractMapper.ToRead).ToImmutableArray(), Services = snapshot.Services.Select(ControllerContractMapper.ToRead).ToImmutableArray(), Extensions = snapshot.ExtensionRecords.Select(ControllerContractMapper.ToRead).ToImmutableArray() };
        return ControllerManagementResponseBuilder.Success(data, snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> ReadGlobalSettingsAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(snapshot.GlobalSettings), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> PatchGlobalSettingsAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!HasJsonBody(request, mergePatch: true)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        if (!TryMergePatch(ControllerContractMapper.ToWrite(snapshot.GlobalSettings), ControllerManagementJson.AsReadOnlyMemory(request.Body), out ControllerGlobalSettingsWriteDto? payload) || payload is null) return ControllerManagementResponseBuilder.InvalidRequest;
        var global = ControllerContractMapper.ToContract(payload, snapshot.GlobalSettings.Version);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, globalSettings: global), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(global), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

}

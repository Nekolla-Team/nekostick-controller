using System.Collections.Immutable;
using System.Linq;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    private async ValueTask<ControllerManagementResponse> ReadRoutesAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return ControllerManagementResponseBuilder.Success(snapshot.Routes.Where(route => !IsReservedRoute(route)).Select(ControllerContractMapper.ToRead).ToImmutableArray(), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> ReadRouteAsync(ControllerManagementRequest request, Guid routeId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var route = snapshot.Routes.FirstOrDefault(candidate => candidate.Id == routeId);
        return route is null || IsReservedRoute(route) ? ControllerManagementResponseBuilder.NotFound : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(route), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> CreateRouteAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!HasJsonBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (!ControllerManagementJson.TryDeserialize<ControllerRouteWriteDto>(ControllerManagementJson.AsReadOnlyMemory(request.Body), out var payload) || payload is null) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        if (IsReservedRoutePayload(payload)) return ControllerManagementResponseBuilder.ReservedRoute;
        var route = ControllerContractMapper.ToContract(payload, current: null, createId: Guid.CreateVersion7());
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, routes: snapshot.Routes.Add(route)), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(route), write.NewVersion!.Value, 201, $"{ControllerManagementApiContract.RoutesPath}/{route.Id}") : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> PatchRouteAsync(ControllerManagementRequest request, Guid routeId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!HasJsonBody(request, mergePatch: true)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        var current = snapshot.Routes.FirstOrDefault(route => route.Id == routeId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound;
        if (IsReservedRoute(current)) return ControllerManagementResponseBuilder.ReservedRoute;
        if (!TryMergePatch(ControllerContractMapper.ToWrite(current), ControllerManagementJson.AsReadOnlyMemory(request.Body), out ControllerRouteWriteDto? payload) || payload is null) return ControllerManagementResponseBuilder.InvalidRequest;
        if (IsReservedRoutePayload(payload)) return ControllerManagementResponseBuilder.ReservedRoute;
        var route = ControllerContractMapper.ToContract(payload, current);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, routes: snapshot.Routes.Select(item => item.Id == routeId ? route : item).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(route), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> DeleteRouteAsync(ControllerManagementRequest request, Guid routeId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        var current = snapshot.Routes.FirstOrDefault(route => route.Id == routeId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound;
        if (IsReservedRoute(current)) return ControllerManagementResponseBuilder.ReservedRoute;
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, routes: snapshot.Routes.Remove(current)), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.NoContent(write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

}

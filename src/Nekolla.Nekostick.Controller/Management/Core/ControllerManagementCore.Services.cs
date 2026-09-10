using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    private async ValueTask<ControllerManagementResponse> ReadServicesAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return ControllerManagementResponseBuilder.Success(snapshot.Services.Select(ControllerContractMapper.ToRead).ToImmutableArray(), snapshot.Version);
    }
    private async ValueTask<ControllerManagementResponse> ReadServiceRuntimesAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireNoIfMatch(request) || !RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionHostApiSupport.IsApi13Supported(bridge13.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported;
        var read = await bridge13.Supervisor.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var snapshots = read.Value.IsDefault
            ? ImmutableArray<ControllerServiceRuntimeReadDto>.Empty
            : read.Value.Select(ControllerContractMapper.ToRead).ToImmutableArray();
        return ControllerManagementResponseBuilder.SuccessUnversioned(snapshots);
    }

    private async ValueTask<ControllerManagementResponse> ReadServiceRuntimeAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!RequireNoIfMatch(request) || !RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionHostApiSupport.IsApi13Supported(bridge13.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported;
        var read = await bridge13.Supervisor.GetAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return read.Value is { } snapshot
            ? ControllerManagementResponseBuilder.SuccessUnversioned(ControllerContractMapper.ToRead(snapshot))
            : ControllerManagementResponseBuilder.NotFound;
    }

    private async ValueTask<ControllerManagementResponse> WriteServiceRuntimeAsync(
        ControllerManagementRequest request,
        string method,
        Guid serviceId,
        string action,
        CancellationToken cancellationToken)
    {
        if (method != "POST") return ControllerManagementResponseBuilder.MethodNotAllowed;
        if (!RequireNoIfMatch(request) || !RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionHostApiSupport.IsApi133Supported(bridge13.ApiVersion))
        {
            return ControllerManagementResponseBuilder.Unsupported;
        }

        var write = action == "resume"
            ? await ResumeServiceRuntimeAsync(bridge13, serviceId, cancellationToken).ConfigureAwait(false)
            : await RestartServiceRuntimeAsync(bridge13, serviceId, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
        return MapServiceRuntimeActionResult(action, write);

    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ControllerManagementResponse MapServiceRuntimeActionResult(string action, ConfigurationWriteResult write)
    {
        var outcome = action == "resume"
            ? write.IsNoOp ? "ignored" : "resumed"
            : "restarted";
        return ControllerManagementResponseBuilder.SuccessUnversioned(new ControllerServiceRuntimeActionReadDto { Outcome = outcome });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<ConfigurationWriteResult> ResumeServiceRuntimeAsync(
        IExtensionHostBridge13 bridge13,
        Guid serviceId,
        CancellationToken cancellationToken) =>
        bridge13.Supervisor.ResumeAsync(serviceId, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<ConfigurationWriteResult> RestartServiceRuntimeAsync(
        IExtensionHostBridge13 bridge13,
        Guid serviceId,
        CancellationToken cancellationToken) =>
        bridge13.Supervisor.RestartAsync(serviceId, cancellationToken);

    private async ValueTask<ControllerManagementResponse> ReadServiceAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var service = snapshot.Services.FirstOrDefault(candidate => candidate.Id == serviceId);
        return service is null ? ControllerManagementResponseBuilder.NotFound : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(service), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> CreateServiceAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!HasJsonBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (!ControllerManagementJson.TryDeserialize<ControllerServiceWriteDto>(ControllerManagementJson.AsReadOnlyMemory(request.Body), out var payload) || payload is null) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        var service = ControllerContractMapper.ToContract(payload, current: null, createId: Guid.CreateVersion7());
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Add(service)), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(service), write.NewVersion!.Value, 201, $"{ControllerManagementApiContract.ServicesPath}/{service.Id}") : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> PatchServiceAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!HasJsonBody(request, mergePatch: true)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (PatchContainsProperty(request.Body, "environment")) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var current = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound;
        if (!TryMergePatch(ControllerContractMapper.ToWrite(current, includeEnvironment: false), ControllerManagementJson.AsReadOnlyMemory(request.Body), out ControllerServiceWriteDto? payload) || payload is null) return ControllerManagementResponseBuilder.InvalidRequest;
        var service = ControllerContractMapper.ToContract(payload, current);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Select(item => item.Id == serviceId ? service : item).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(service), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> DeleteServiceAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        var service = snapshot.Services.FirstOrDefault(candidate => candidate.Id == serviceId);
        if (service is null) return ControllerManagementResponseBuilder.NotFound;
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Remove(service)), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.NoContent(write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> ReadEnvironmentAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var service = snapshot.Services.FirstOrDefault(candidate => candidate.Id == serviceId);
        return service is null ? ControllerManagementResponseBuilder.NotFound : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToEnvironment(service), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> PutEnvironmentAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!HasJsonBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        if (!ControllerManagementJson.TryDeserialize<ControllerServiceEnvironmentWriteDto>(ControllerManagementJson.AsReadOnlyMemory(request.Body), out var payload) || payload?.Environment is null) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        var current = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound;
        var writeDto = ControllerContractMapper.ToWrite(current, includeEnvironment: true);
        writeDto = new ControllerServiceWriteDto { Enabled = writeDto.Enabled, FileName = writeDto.FileName, ArgumentList = writeDto.ArgumentList, WorkingDirectory = writeDto.WorkingDirectory, Environment = payload.Environment, StartMode = writeDto.StartMode, RestartPolicy = writeDto.RestartPolicy, HealthCheck = writeDto.HealthCheck };
        var service = ControllerContractMapper.ToContract(writeDto, current);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Select(item => item.Id == serviceId ? service : item).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToEnvironment(service), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> DeleteEnvironmentAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!RequireEmptyBody(request)) return ControllerManagementResponseBuilder.InvalidRequest;
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed;
        var current = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound;
        var writeDto = ControllerContractMapper.ToWrite(current, includeEnvironment: true);
        writeDto = new ControllerServiceWriteDto { Enabled = writeDto.Enabled, FileName = writeDto.FileName, ArgumentList = writeDto.ArgumentList, WorkingDirectory = writeDto.WorkingDirectory, Environment = ImmutableDictionary<string, string>.Empty, StartMode = writeDto.StartMode, RestartPolicy = writeDto.RestartPolicy, HealthCheck = writeDto.HealthCheck };
        var service = ControllerContractMapper.ToContract(writeDto, current);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Select(item => item.Id == serviceId ? service : item).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.NoContent(write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

}

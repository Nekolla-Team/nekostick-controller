using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    private async ValueTask<ControllerManagementResponse> ReadServicesAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return ControllerManagementResponseBuilder.Success(snapshot.Services.Select(ControllerContractMapper.ToRead).ToImmutableArray(), snapshot.Version);
    }
    private async ValueTask<ControllerManagementResponse> ReadServiceRuntimesAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (ValidateNoBodyRequest(request) is { } validationError) return validationError;
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionHostApiSupport.IsApi13Supported(bridge13.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "This host does not support service runtime snapshots.");
        var read = await bridge13.Supervisor.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var snapshots = read.Value.IsDefault
            ? ImmutableArray<ControllerServiceRuntimeReadDto>.Empty
            : read.Value.Select(ControllerContractMapper.ToRead).ToImmutableArray();
        return ControllerManagementResponseBuilder.SuccessUnversioned(snapshots);
    }

    /// <summary>
    /// Opens one consumer view over the node-local runtime-state feed. Read-only and long-lived by
    /// design, so it is not gated on the mutation gate. Answers 501 when the host lacks the
    /// runtime-state subscription capability.
    /// </summary>
    internal ControllerRuntimeFeedResult OpenServiceRuntimeFeed()
    {
        if (_bridge is null)
        {
            return ControllerRuntimeFeedResult.Rejected(ControllerManagementResponseBuilder.Unavailable(reason: "bridge_unavailable", message: "The host management bridge is unavailable."));
        }

        if (!HasFullConfigurationScope() || !ExtensionHostApiSupport.IsApi13Supported(_bridge.ApiVersion))
        {
            return ControllerRuntimeFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "This host does not support service runtime state feeds."));
        }

        var feed = _runtimeFeedProvider?.Invoke();
        if (feed is null)
        {
            return ControllerRuntimeFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "runtime_state_feed_unavailable", message: "This host does not provide a service runtime state feed."));
        }

        if (feed.SubscriptionRejected)
        {
            var message = string.IsNullOrWhiteSpace(feed.SubscriptionRejectionMessage)
                ? "This host does not provide a service runtime state feed."
                : feed.SubscriptionRejectionMessage;
            return ControllerRuntimeFeedResult.Rejected(ControllerManagementResponseBuilder.Unsupported(reason: "runtime_state_feed_unavailable", message: message));
        }

        return ControllerRuntimeFeedResult.Opened(feed.Subscribe());
    }

    private async ValueTask<ControllerManagementResponse> ReadServiceRuntimeAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (ValidateNoBodyRequest(request) is { } validationError) return validationError;
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionHostApiSupport.IsApi13Supported(bridge13.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "This host does not support service runtime snapshots.");
        var read = await bridge13.Supervisor.GetAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return read.Value is { } snapshot
            ? ControllerManagementResponseBuilder.SuccessUnversioned(ControllerContractMapper.ToRead(snapshot))
            : ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString());
    }

    private async ValueTask<ControllerManagementResponse> WriteServiceRuntimeAsync(
        ControllerManagementRequest request,
        string method,
        Guid serviceId,
        string action,
        CancellationToken cancellationToken)
    {
        if (method != "POST") return ControllerManagementResponseBuilder.MethodNotAllowed(message: $"The {method} method is not allowed for this service runtime action.", parameter: method);
        if (ValidateNoBodyRequest(request) is { } validationError) return validationError;
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionHostApiSupport.IsApi133Supported(bridge13.ApiVersion))
        {
            return ControllerManagementResponseBuilder.Unsupported(reason: "host_api_unsupported", message: "This host does not support service runtime actions.");
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
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var service = snapshot.Services.FirstOrDefault(candidate => candidate.Id == serviceId);
        return service is null
            ? ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString())
            : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(service), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> CreateServiceAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (ValidateJsonBody(request) is { } bodyError) return bodyError;
        if (!ControllerManagementJson.TryDeserialize<ControllerServiceWriteDto>(ControllerManagementJson.AsReadOnlyMemory(request.Body), out var payload) || payload is null) return InvalidJsonBodyResponse(request.Body, "service");
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed(reason: "if_match_stale", message: $"The If-Match version {expectedVersion} is stale; the current configuration version is {snapshot.Version}.", parameter: "If-Match");
        var service = ControllerContractMapper.ToContract(payload, current: null, createId: Guid.CreateVersion7());
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Add(service)), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(service), write.NewVersion!.Value, 201, $"{ControllerManagementApiContract.ServicesPath}/{service.Id}") : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> PatchServiceAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (ValidateJsonBody(request, mergePatch: true) is { } bodyError) return bodyError;
        if (PatchContainsProperty(request.Body, "environment")) return ControllerManagementResponseBuilder.InvalidRequest("invalid_field", "The 'environment' field cannot be changed through this service merge-patch endpoint.", "environment");
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed(reason: "if_match_stale", message: $"The If-Match version {expectedVersion} is stale; the current configuration version is {snapshot.Version}.", parameter: "If-Match");
        var current = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString());
        if (!TryMergePatch(ControllerContractMapper.ToWrite(current, includeEnvironment: false), ControllerManagementJson.AsReadOnlyMemory(request.Body), out ControllerServiceWriteDto? payload) || payload is null) return InvalidMergePatchResponse(request.Body, "service");
        var service = ControllerContractMapper.ToContract(payload, current);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Select(item => item.Id == serviceId ? service : item).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToRead(service), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> DeleteServiceAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed(reason: "if_match_stale", message: $"The If-Match version {expectedVersion} is stale; the current configuration version is {snapshot.Version}.", parameter: "If-Match");
        var service = snapshot.Services.FirstOrDefault(candidate => candidate.Id == serviceId);
        if (service is null) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString());
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Remove(service)), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.NoContent(write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> ReadEnvironmentAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        var service = snapshot.Services.FirstOrDefault(candidate => candidate.Id == serviceId);
        return service is null ? ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString()) : ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToEnvironment(service), snapshot.Version);
    }

    private async ValueTask<ControllerManagementResponse> PutEnvironmentAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (ValidateJsonBody(request) is { } bodyError) return bodyError;
        if (!ControllerManagementJson.TryDeserialize<ControllerServiceEnvironmentWriteDto>(ControllerManagementJson.AsReadOnlyMemory(request.Body), out var payload) || payload is null) return InvalidJsonBodyResponse(request.Body, "service environment");
        if (payload.Environment is null) return MissingFieldResponse("environment");
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed(reason: "if_match_stale", message: $"The If-Match version {expectedVersion} is stale; the current configuration version is {snapshot.Version}.", parameter: "If-Match");
        var current = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString());
        var writeDto = ControllerContractMapper.ToWrite(current, includeEnvironment: true);
        writeDto = new ControllerServiceWriteDto { Enabled = writeDto.Enabled, FileName = writeDto.FileName, ArgumentList = writeDto.ArgumentList, WorkingDirectory = writeDto.WorkingDirectory, Environment = payload.Environment, StartMode = writeDto.StartMode, RestartPolicy = writeDto.RestartPolicy, HealthCheck = writeDto.HealthCheck };
        var service = ControllerContractMapper.ToContract(writeDto, current);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Select(item => item.Id == serviceId ? service : item).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.Success(ControllerContractMapper.ToEnvironment(service), write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

    private async ValueTask<ControllerManagementResponse> DeleteEnvironmentAsync(ControllerManagementRequest request, Guid serviceId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(request, out var expectedVersion, out var precondition)) return precondition!;
        if (!RequireEmptyBody(request)) return UnexpectedBodyResponse();
        var read = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        if (expectedVersion != snapshot.Version) return ControllerManagementResponseBuilder.PreconditionFailed(reason: "if_match_stale", message: $"The If-Match version {expectedVersion} is stale; the current configuration version is {snapshot.Version}.", parameter: "If-Match");
        var current = snapshot.Services.FirstOrDefault(service => service.Id == serviceId);
        if (current is null) return ControllerManagementResponseBuilder.NotFound(reason: "unknown_service", message: $"Service '{serviceId}' was not found.", parameter: serviceId.ToString());
        var writeDto = ControllerContractMapper.ToWrite(current, includeEnvironment: true);
        writeDto = new ControllerServiceWriteDto { Enabled = writeDto.Enabled, FileName = writeDto.FileName, ArgumentList = writeDto.ArgumentList, WorkingDirectory = writeDto.WorkingDirectory, Environment = ImmutableDictionary<string, string>.Empty, StartMode = writeDto.StartMode, RestartPolicy = writeDto.RestartPolicy, HealthCheck = writeDto.HealthCheck };
        var service = ControllerContractMapper.ToContract(writeDto, current);
        var write = await ReplaceAsync(expectedVersion, NewChanges(snapshot, services: snapshot.Services.Select(item => item.Id == serviceId ? service : item).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return write.IsSuccess ? ControllerManagementResponseBuilder.NoContent(write.NewVersion!.Value) : ControllerManagementResponseBuilder.FromConfigurationErrors(write.Errors);
    }

}

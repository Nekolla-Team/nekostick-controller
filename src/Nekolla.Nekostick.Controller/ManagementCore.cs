using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller;

internal readonly record struct ProvisionedHostRouteIdentity(Guid RouteId, string CanonicalPath);

/// <summary>Translates REST resource requests into the Host 1.3 full-configuration facade.</summary>
internal sealed class ControllerManagementCore
{
    private readonly ControllerOptions _options;
    private readonly IExtensionHostBridge? _bridge;
    private static readonly SearchValues<char> InvalidPathCharacters = SearchValues.Create("?#\0");

    internal ControllerManagementCore(ControllerOptions options, IExtensionHostBridge? bridge)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _bridge = bridge;
    }

    internal async ValueTask<ControllerManagementResponse> DispatchAsync(ControllerManagementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_bridge is null) return ControllerManagementResponseBuilder.Unavailable;
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(_bridge.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported;

        var method = request.Method.Trim().ToUpperInvariant();
        var path = NormalizePath(request.Path, request.Transport);
        if (path is null || method.Length == 0) return ControllerManagementResponseBuilder.InvalidRequest;

        try
        {
            if (path == ControllerManagementApiContract.RootPath)
                return method == "GET" ? await ReadRootAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;
            if (path == ControllerManagementApiContract.GlobalSettingsPath)
                return method switch
                {
                    "GET" => await ReadGlobalSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                    "PATCH" => await PatchGlobalSettingsAsync(request, cancellationToken).ConfigureAwait(false),
                    _ => ControllerManagementResponseBuilder.MethodNotAllowed
                };
            if (path == ControllerManagementApiContract.RoutesPath)
                return method switch
                {
                    "GET" => await ReadRoutesAsync(request, cancellationToken).ConfigureAwait(false),
                    "POST" => await CreateRouteAsync(request, cancellationToken).ConfigureAwait(false),
                    _ => ControllerManagementResponseBuilder.MethodNotAllowed
                };
            if (path == ControllerManagementApiContract.ServicesRuntimePath)
                return method == "GET" ? await ReadServiceRuntimesAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

            if (path == ControllerManagementApiContract.ServicesPath)
                return method switch
                {
                    "GET" => await ReadServicesAsync(request, cancellationToken).ConfigureAwait(false),
                    "POST" => await CreateServiceAsync(request, cancellationToken).ConfigureAwait(false),
                    _ => ControllerManagementResponseBuilder.MethodNotAllowed
                };
            if (path == ControllerManagementApiContract.ExtensionsPath)
                return method == "GET" ? await ReadExtensionsAsync(request, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

            if (TryGetEnvironmentPath(path, out var environmentServiceId))
                return method switch
                {
                    "GET" => await ReadEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                    "PUT" => await PutEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                    "DELETE" => await DeleteEnvironmentAsync(request, environmentServiceId, cancellationToken).ConfigureAwait(false),
                    _ => ControllerManagementResponseBuilder.MethodNotAllowed
                };
            if (TryGetSettingsPath(path, out var settingsExtensionId))
                return method switch
                {
                    "GET" => await ReadExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                    "PUT" => await PutExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                    "DELETE" => await DeleteExtensionSettingsAsync(request, settingsExtensionId, cancellationToken).ConfigureAwait(false),
                    _ => ControllerManagementResponseBuilder.MethodNotAllowed
                };
            if (TryGetMemberPath(path, ControllerManagementApiContract.RoutesPath, out var routeId))
                return method switch
                {
                    "GET" => await ReadRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                    "PATCH" => await PatchRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                    "DELETE" => await DeleteRouteAsync(request, routeId, cancellationToken).ConfigureAwait(false),
                    _ => ControllerManagementResponseBuilder.MethodNotAllowed
                };
            if (TryGetServiceRuntimePath(path, out var runtimeServiceId))
                return method == "GET" ? await ReadServiceRuntimeAsync(request, runtimeServiceId, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

            if (TryGetMemberPath(path, ControllerManagementApiContract.ServicesPath, out var serviceId))
                return method switch
                {
                    "GET" => await ReadServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                    "PATCH" => await PatchServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                    "DELETE" => await DeleteServiceAsync(request, serviceId, cancellationToken).ConfigureAwait(false),
                    _ => ControllerManagementResponseBuilder.MethodNotAllowed
                };
            if (TryGetExtensionMemberPath(path, out var extensionId))
                return method == "GET" ? await ReadExtensionAsync(request, extensionId, cancellationToken).ConfigureAwait(false) : ControllerManagementResponseBuilder.MethodNotAllowed;

            return ControllerManagementResponseBuilder.NotFound;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return ControllerManagementResponseBuilder.InvalidRequest;
        }
        catch (NotSupportedException)
        {
            return ControllerManagementResponseBuilder.Unsupported;
        }
        catch (InvalidOperationException)
        {
            return ControllerManagementResponseBuilder.Unavailable;
        }
        catch
        {
            return ControllerManagementResponseBuilder.Unavailable;
        }
    }

    /// <summary>Provisions the private HostRoute bridge route; this is not a public dispatch path.</summary>
    internal async ValueTask ProvisionHostRouteAsync(string handlerId, CancellationToken cancellationToken)
    {
        _ = await ProvisionHostRouteAsync(handlerId, ownershipMarker: null, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Provisions a private HostRoute and optionally stamps an internal ownership marker.</summary>
    internal async ValueTask<ProvisionedHostRouteIdentity?> ProvisionHostRouteAsync(string handlerId, string? ownershipMarker, CancellationToken cancellationToken)
    {
        if (!_options.EnableHostRoute) return null;
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");

        var path = _options.HostRoutePath ?? throw new InvalidOperationException("The Host route path is unavailable.");
        if (ownershipMarker is null)
        {
            var ownerRead = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!ownerRead.IsSuccess || ownerRead.Value is not { } ownerSnapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
            var handlerRoutes = ownerSnapshot.Routes.Where(route => route.Target is ExtensionHandlerRouteTarget target && string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)).ToArray();
            if (handlerRoutes.Length > 1) throw new InvalidOperationException("Duplicate controller handler routes are configured.");
            if (ownerSnapshot.Routes.Any(route => string.Equals(route.Matcher.Pattern, path, StringComparison.Ordinal) && (route.Target is not ExtensionHandlerRouteTarget target || !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)))) throw new InvalidOperationException("The configured controller route is already owned by another target.");
            var existing = handlerRoutes.SingleOrDefault();
            var route = new ExtensionRouteConfiguration(existing?.Id ?? Guid.CreateVersion7(), true, new RouteMatcherConfiguration(RouteMatcherType.Prefix, path, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty), new ExtensionHandlerRouteTarget(handlerId), int.MaxValue);
            if (existing is not null && existing.Enabled == route.Enabled && existing.Priority == route.Priority && existing.Matcher.Type == route.Matcher.Type && string.Equals(existing.Matcher.Pattern, route.Matcher.Pattern, StringComparison.Ordinal) && existing.Matcher.HostPatterns.SequenceEqual(route.Matcher.HostPatterns) && existing.Matcher.Methods.SequenceEqual(route.Matcher.Methods)) return null;
            var ownerChanges = new ExtensionConfigurationChangeSet(ImmutableArray.Create(route), ImmutableArray<Guid>.Empty, ImmutableArray<ExtensionServiceConfiguration>.Empty, ImmutableArray<Guid>.Empty, settings: null);
            var ownerWrite = await bridge.ConfigurationApi.ApplyAsync(ownerSnapshot.Version, ownerChanges, cancellationToken).ConfigureAwait(false);
            if (!ownerWrite.IsSuccess) throw new InvalidOperationException("The controller route could not be provisioned.");
            return null;
        }

        ArgumentException.ThrowIfNullOrEmpty(ownershipMarker);
        if (!ownershipMarker.StartsWith(ControllerOptions.BootstrapOwnershipPrefix, StringComparison.Ordinal) || ownershipMarker.Length <= ControllerOptions.BootstrapOwnershipPrefix.Length)
        {
            throw new ArgumentException("The route ownership marker is invalid.", nameof(ownershipMarker));
        }
        // Marker-bearing bootstrap routes use the complete Host 1.3 aggregate so cleanup can prove
        // both the handler identity and the private marker before removing a route.
        var read = await bridge.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
        var markedRoutes = snapshot.Routes.Where(route => route.Target is ExtensionHandlerRouteTargetConfiguration target && string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)).ToArray();
        if (markedRoutes.Length > 1) throw new InvalidOperationException("Duplicate controller handler routes are configured.");
        if (snapshot.Routes.Any(route => string.Equals(route.Matcher.Pattern, path, StringComparison.Ordinal) && (route.Target is not ExtensionHandlerRouteTargetConfiguration target || !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)))) throw new InvalidOperationException("The configured controller route is already owned by another target.");
        var existingMarked = markedRoutes.SingleOrDefault();
        var metadataMarker = JsonSerializer.Serialize(new { controller = ControllerOptions.ExtensionId, handler = handlerId, ownership = ownershipMarker }, ControllerManagementJson.Options);
        if (Encoding.UTF8.GetByteCount(metadataMarker) > ControllerManagementJson.MaximumEmbeddedJsonBytes) throw new ArgumentException("The route ownership marker is too large.", nameof(ownershipMarker));
        if (existingMarked is not null && !HasOwnershipMarker(existingMarked.MetadataJson, handlerId, ownershipMarker)) throw new InvalidOperationException("The existing controller route is not owned by this bootstrap instance.");
        var routeId = existingMarked?.Id ?? Guid.CreateVersion7();
        var identity = new ProvisionedHostRouteIdentity(routeId, path);
        var stampedRoute = new RouteConfiguration(routeId, true, new RouteMatcherConfiguration(RouteMatcherType.Prefix, path, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty), new ExtensionHandlerRouteTargetConfiguration(handlerId), int.MaxValue, new ForwardingConfiguration(ForwardingMode.Preserve, null), ImmutableArray<HeaderRewriteConfiguration>.Empty, ImmutableArray<HeaderRewriteConfiguration>.Empty, metadataMarker, existingMarked?.CreatedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, existingMarked?.Version ?? 0);
        if (existingMarked is not null && string.Equals(existingMarked.MetadataJson, metadataMarker, StringComparison.Ordinal) && existingMarked.Enabled && existingMarked.Priority == int.MaxValue && string.Equals(existingMarked.Matcher.Pattern, path, StringComparison.Ordinal)) return identity;
        var routes = snapshot.Routes.Where(route => existingMarked is null || route.Id != existingMarked.Id).Append(stampedRoute).ToImmutableArray();
        var changes = new ConfigurationChangeSet(snapshot.GlobalSettings, routes, snapshot.Services, snapshot.ExtensionRecords, snapshot.ExtensionSettings);
        var write = await bridge.FullConfiguration.ReplaceAsync(snapshot.Version, changes, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("The controller route could not be provisioned.");
        return identity;
    }


    /// <summary>Removes only stale routes carrying the private bootstrap marker format.</summary>
    internal async ValueTask CleanupStaleBootstrapRoutesAsync(
        string handlerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");
        var read = await bridge.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
        var stale = snapshot.Routes.Where(route => route.Target is ExtensionHandlerRouteTargetConfiguration target && string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) && HasBootstrapOwnershipMarker(route.MetadataJson, handlerId)).ToImmutableHashSet();
        if (stale.Count == 0) return;
        var routes = snapshot.Routes.Where(route => !stale.Contains(route)).ToImmutableArray();
        var write = await bridge.FullConfiguration.ReplaceAsync(snapshot.Version, new ConfigurationChangeSet(snapshot.GlobalSettings, routes, snapshot.Services, snapshot.ExtensionRecords, snapshot.ExtensionSettings), cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("Stale controller bootstrap routes could not be removed.");
    }

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
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionAbi.IsApi13Supported(bridge13.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported;
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
        if (_bridge is not IExtensionHostBridge13 bridge13 || !ExtensionAbi.IsApi13Supported(bridge13.ApiVersion)) return ControllerManagementResponseBuilder.Unsupported;
        var read = await bridge13.Supervisor.GetAsync(serviceId, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess) return ControllerManagementResponseBuilder.FromConfigurationErrors(read.Errors);
        return read.Value is { } snapshot
            ? ControllerManagementResponseBuilder.SuccessUnversioned(ControllerContractMapper.ToRead(snapshot))
            : ControllerManagementResponseBuilder.NotFound;
    }

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
    /// <summary>Removes only the exact private bootstrap route identity after rechecking ownership.</summary>
    internal async ValueTask RemoveProvisionedHostRouteAsync(
        string handlerId,
        ProvisionedHostRouteIdentity identity,
        string ownershipMarker,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        ArgumentException.ThrowIfNullOrEmpty(ownershipMarker);
        if (!string.Equals(handlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal)) throw new InvalidOperationException("The bootstrap handler identity is not fixed.");
        if (identity.RouteId == Guid.Empty || string.IsNullOrEmpty(identity.CanonicalPath)) throw new ArgumentException("The provisioned route identity is invalid.", nameof(identity));
        if (!ownershipMarker.StartsWith(ControllerOptions.BootstrapOwnershipPrefix, StringComparison.Ordinal) || ownershipMarker.Length <= ControllerOptions.BootstrapOwnershipPrefix.Length) throw new ArgumentException("The route ownership marker is invalid.", nameof(ownershipMarker));
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");
        var read = await bridge.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
        var candidate = snapshot.Routes.SingleOrDefault(route => route.Id == identity.RouteId);
        if (candidate is null) return;
        if (candidate.Target is not ExtensionHandlerRouteTargetConfiguration target ||
            !string.Equals(target.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal) ||
            !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) ||
            !HasOwnershipMarker(candidate.MetadataJson, handlerId, ownershipMarker) ||
            candidate.Matcher.Type != RouteMatcherType.Prefix ||
            !string.Equals(candidate.Matcher.Pattern, identity.CanonicalPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The provisioned controller route ownership proof no longer matches.");
        }

        var routes = snapshot.Routes.Where(route => route.Id != identity.RouteId).ToImmutableArray();
        var write = await bridge.FullConfiguration.ReplaceAsync(snapshot.Version, new ConfigurationChangeSet(snapshot.GlobalSettings, routes, snapshot.Services, snapshot.ExtensionRecords, snapshot.ExtensionSettings), cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("The controller route could not be removed.");
    }

    private static bool HasOwnershipMarker(string metadataJson, string handlerId, string ownershipMarker) =>
        TryReadOwnershipMetadata(metadataJson, out var controllerId, out var metadataHandlerId, out var metadataOwnership) &&
        string.Equals(controllerId, ControllerOptions.ExtensionId, StringComparison.Ordinal) &&
        string.Equals(metadataHandlerId, handlerId, StringComparison.Ordinal) &&
        string.Equals(metadataOwnership, ownershipMarker, StringComparison.Ordinal);

    private static bool HasBootstrapOwnershipMarker(string metadataJson, string handlerId) =>
        TryReadOwnershipMetadata(metadataJson, out var controllerId, out var metadataHandlerId, out var metadataOwnership) &&
        string.Equals(controllerId, ControllerOptions.ExtensionId, StringComparison.Ordinal) &&
        string.Equals(metadataHandlerId, handlerId, StringComparison.Ordinal) &&
        metadataOwnership is not null &&
        metadataOwnership.StartsWith(ControllerOptions.BootstrapOwnershipPrefix, StringComparison.Ordinal) &&
        metadataOwnership.Length > ControllerOptions.BootstrapOwnershipPrefix.Length;

    private static bool TryReadOwnershipMetadata(
        string metadataJson,
        out string? controllerId,
        out string? handlerId,
        out string? ownershipMarker)
    {
        controllerId = null;
        handlerId = null;
        ownershipMarker = null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("controller", out var controller) || controller.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("handler", out var handler) || handler.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("ownership", out var ownership) || ownership.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            controllerId = controller.GetString();
            handlerId = handler.GetString();
            ownershipMarker = ownership.GetString();
            return controllerId is not null && handlerId is not null && ownershipMarker is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }


    private ValueTask<ConfigurationReadResult<HostConfigurationSnapshot>> ReadSnapshotAsync(CancellationToken cancellationToken) => _bridge!.FullConfiguration.ReadAsync(cancellationToken);
    private ValueTask<ConfigurationWriteResult> ReplaceAsync(long expectedVersion, ConfigurationChangeSet changes, CancellationToken cancellationToken) => _bridge!.FullConfiguration.ReplaceAsync(expectedVersion, changes, cancellationToken);
    private static ConfigurationChangeSet NewChanges(HostConfigurationSnapshot snapshot, GlobalSettingsConfiguration? globalSettings = null, ImmutableArray<RouteConfiguration>? routes = null, ImmutableArray<ServiceConfiguration>? services = null, ImmutableArray<ExtensionRecordConfiguration>? extensionRecords = null, ImmutableArray<ExtensionSettingsConfiguration>? extensionSettings = null) => new(globalSettings ?? snapshot.GlobalSettings, routes ?? snapshot.Routes, services ?? snapshot.Services, extensionRecords ?? snapshot.ExtensionRecords, extensionSettings ?? snapshot.ExtensionSettings);
    private bool HasFullConfigurationScope() => (_options.ApiScope & ControllerApiScope.FullConfiguration) == ControllerApiScope.FullConfiguration;

    private string? NormalizePath(string rawPath, ControllerTransport transport)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 8192 || rawPath.AsSpan().IndexOfAny(InvalidPathCharacters) >= 0 || !rawPath.StartsWith('/')) return null;
        var path = rawPath;
        while (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
        if (transport == ControllerTransport.HostRoute && _options.EnableHostRoute)
        {
            var hostPrefix = _options.HostRoutePath;
            if (hostPrefix is null || !ControllerOptions.IsCanonicalManagementPath(hostPrefix)) return null;
            if (!string.Equals(hostPrefix, ControllerManagementApiContract.RootPath, StringComparison.Ordinal))
            {
                var prefixedPath = hostPrefix + ControllerManagementApiContract.RootPath;
                if (string.Equals(path, prefixedPath, StringComparison.Ordinal)) path = ControllerManagementApiContract.RootPath;
                else if (path.StartsWith(prefixedPath + "/", StringComparison.Ordinal)) path = path[hostPrefix.Length..];
            }
        }
        return path;
    }

    private bool IsReservedRoute(RouteConfiguration route)
    {
        if (route.Target is ExtensionHandlerRouteTargetConfiguration handler && string.Equals(handler.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal)) return true;
        return _options.HostRoutePath is { } reservedPath && (string.Equals(route.Matcher.Pattern, reservedPath, StringComparison.Ordinal) || route.Matcher.Pattern.StartsWith(reservedPath + "/", StringComparison.Ordinal));
    }

    private bool IsReservedRoutePayload(ControllerRouteWriteDto route)
    {
        if (route.Target is { Type: ControllerRouteTargetKind.ExtensionHandler, HandlerId: not null } && string.Equals(route.Target.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal)) return true;
        return _options.HostRoutePath is { } reservedPath && route.Matcher is not null && (string.Equals(route.Matcher.Pattern, reservedPath, StringComparison.Ordinal) || route.Matcher.Pattern.StartsWith(reservedPath + "/", StringComparison.Ordinal));
    }

    private static bool RequireEmptyBody(ControllerManagementRequest request) => request.Body.IsEmpty;
    private static bool RequireNoIfMatch(ControllerManagementRequest request) => !request.Headers.ContainsKey(ControllerManagementApiContract.IfMatchHeaderName);
    private static bool HasJsonBody(ControllerManagementRequest request, bool mergePatch = false)
    {
        if (request.Body.IsEmpty || request.Body.Length > ControllerAdmissionLimits.MaximumRequestBodyBytes || !request.Headers.TryGetValue("content-type", out var values) || values.Length != 1) return false;
        var mediaType = values[0].Split(';', 2)[0].Trim();
        return string.Equals(mediaType, ControllerManagementApiContract.JsonMediaType, StringComparison.OrdinalIgnoreCase) || (mergePatch && string.Equals(mediaType, "application/merge-patch+json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadIfMatch(ControllerManagementRequest request, out long version, out ControllerManagementResponse? error)
    {
        version = 0;
        error = null;
        if (!request.Headers.TryGetValue(ControllerManagementApiContract.IfMatchHeaderName, out var values)) { error = ControllerManagementResponseBuilder.PreconditionRequired; return false; }
        if (values.Length != 1) { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        var value = values[0];
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        var text = value[1..^1];
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        foreach (var character in text) if (character is < '0' or > '9') { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version)) { error = ControllerManagementResponseBuilder.InvalidRequest; return false; }
        return true;
    }

    private static bool PatchContainsProperty(ImmutableArray<byte> body, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(ControllerManagementJson.AsReadOnlyMemory(body), new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, propertyName, StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }

    private static bool TryMergePatch<T>(T current, ReadOnlyMemory<byte> patchBytes, out T? result)
    {
        result = default;
        try
        {
            var patch = JsonNode.Parse(Encoding.UTF8.GetString(patchBytes.Span), nodeOptions: null, documentOptions: new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (patch is not JsonObject) return false;
            var target = JsonSerializer.SerializeToNode(current, ControllerManagementJson.Options);
            if (target is not JsonObject targetObject || !ValidatePatchShape(patch.AsObject(), targetObject)) return false;
            var merged = MergePatch(targetObject, patch);
            if (merged is not JsonObject) return false;
            var bytes = Encoding.UTF8.GetBytes(merged.ToJsonString(ControllerManagementJson.Options));
            return ControllerManagementJson.TryDeserialize(bytes, out result);
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static JsonNode? MergePatch(JsonNode? target, JsonNode? patch)
    {
        if (patch is null || patch is not JsonObject patchObject) return patch?.DeepClone();
        var targetObject = target as JsonObject ?? new JsonObject();
        foreach (var property in patchObject)
        {
            if (property.Value is null) targetObject.Remove(property.Key);
            else if (property.Value is JsonObject) targetObject[property.Key] = MergePatch(targetObject[property.Key], property.Value);
            else targetObject[property.Key] = property.Value.DeepClone();
        }
        return targetObject;
    }

    private static bool ValidatePatchShape(JsonObject patch, JsonObject target)
    {
        foreach (var property in patch)
        {
            if (!target.ContainsKey(property.Key)) return false;
            if (property.Value is JsonObject patchObject && target[property.Key] is JsonObject targetObject && !ValidatePatchShape(patchObject, targetObject)) return false;
        }
        return true;
    }

    private static bool TryGetMemberPath(string path, string prefix, out Guid id)
    {
        id = default;
        if (!path.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
        var suffix = path[(prefix.Length + 1)..];
        return suffix.Length > 0 && !suffix.Contains('/', StringComparison.Ordinal) && Guid.TryParse(suffix, out id);
    }
    private static bool TryGetServiceRuntimePath(string path, out Guid id)
    {
        id = default;
        var prefix = ControllerManagementApiContract.ServicesPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        const string tail = "/runtime";
        if (!suffix.EndsWith(tail, StringComparison.Ordinal)) return false;
        var idText = suffix[..^tail.Length];
        return idText.Length > 0 && !idText.Contains('/', StringComparison.Ordinal) && Guid.TryParse(idText, out id);
    }

    private static bool TryGetEnvironmentPath(string path, out Guid id)
    {
        id = default;
        var prefix = ControllerManagementApiContract.ServicesPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        const string tail = "/environment";
        if (!suffix.EndsWith(tail, StringComparison.Ordinal)) return false;
        var idText = suffix[..^tail.Length];
        return idText.Length > 0 && !idText.Contains('/', StringComparison.Ordinal) && Guid.TryParse(idText, out id);
    }

    private static bool TryGetSettingsPath(string path, out string extensionId)
    {
        extensionId = string.Empty;
        var prefix = ControllerManagementApiContract.ExtensionsPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = path[prefix.Length..];
        const string tail = "/settings";
        if (!suffix.EndsWith(tail, StringComparison.Ordinal)) return false;
        extensionId = suffix[..^tail.Length];
        return extensionId.Length > 0 && !extensionId.Contains('/', StringComparison.Ordinal);
    }

    private static bool TryGetExtensionMemberPath(string path, out string extensionId)
    {
        extensionId = string.Empty;
        var prefix = ControllerManagementApiContract.ExtensionsPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        extensionId = path[prefix.Length..];
        return extensionId.Length > 0 && !extensionId.Contains('/', StringComparison.Ordinal);
    }
}

/// <summary>Maps bridge result categories into bounded response envelopes.</summary>
internal static class ControllerManagementResponseBuilder
{
    private static readonly KeyValuePair<string, IEnumerable<string>> JsonContentType = new("content-type", new[] { "application/json; charset=utf-8" });
    internal static ControllerManagementResponse Success(object? data, long version, int statusCode = 200, string? location = null) => Create(statusCode, ControllerDispatchCode.Success, new ControllerResponseEnvelope { Ok = true, Code = "ok", Message = "The operation completed.", Data = data, Version = version }, version, location);
    internal static ControllerManagementResponse SuccessUnversioned(object? data) => Create(200, ControllerDispatchCode.Success, new ControllerResponseEnvelope { Ok = true, Code = "ok", Message = "The operation completed.", Data = data, Version = null });
    internal static ControllerManagementResponse NoContent(long version) => new(204, ControllerDispatchCode.Success, new[] { JsonContentType, ETag(version) });
    internal static ControllerManagementResponse Error(int statusCode, ControllerDispatchCode dispatchCode, string code, string message) => Create(statusCode, dispatchCode, new ControllerResponseEnvelope { Ok = false, Code = code, Message = message });
    private static ControllerManagementResponse Create(int statusCode, ControllerDispatchCode dispatchCode, ControllerResponseEnvelope envelope, long? version = null, string? location = null)
    {
        var headers = new List<KeyValuePair<string, IEnumerable<string>>> { JsonContentType };
        if (version is { } currentVersion) headers.Add(ETag(currentVersion));
        if (location is not null) headers.Add(new KeyValuePair<string, IEnumerable<string>>(ControllerManagementApiContract.LocationHeaderName, new[] { location }));
        if (ControllerManagementJson.TrySerialize(envelope, out var body)) return new ControllerManagementResponse(statusCode, dispatchCode, headers, body);
        return ResponseTooLarge;
    }
    private static KeyValuePair<string, IEnumerable<string>> ETag(long version) => new(ControllerManagementApiContract.ETagHeaderName, new[] { $"\"{version.ToString(CultureInfo.InvariantCulture)}\"" });
    private static ControllerManagementResponse ResponseTooLarge => new(503, ControllerDispatchCode.Unavailable, new[] { JsonContentType }, ControllerManagementJson.ResponseTooLargeBody);
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
    internal static ControllerManagementResponse Conflict => Error(409, ControllerDispatchCode.Conflict, "conflict", "The management resource conflicts with current state.");
    internal static ControllerManagementResponse PreconditionRequired => Error(428, ControllerDispatchCode.InvalidRequest, "precondition_required", "Exactly one If-Match precondition is required.");
    internal static ControllerManagementResponse PreconditionFailed => Error(412, ControllerDispatchCode.Conflict, "precondition_failed", "The supplied If-Match precondition is stale.");
    internal static ControllerManagementResponse ReservedRoute => Error(409, ControllerDispatchCode.Conflict, "reserved_route", "The controller management route is reserved.");
    internal static ControllerManagementResponse Unsupported => Error(501, ControllerDispatchCode.Unsupported, "unsupported", "The management operation is unsupported.");
    internal static ControllerManagementResponse MethodNotAllowed => Error(405, ControllerDispatchCode.InvalidRequest, "method_not_allowed", "The management method is not supported.");
    internal static ControllerManagementResponse Unavailable => Error(503, ControllerDispatchCode.Unavailable, "unavailable", "The controller management service is unavailable.");
    internal static ControllerManagementResponse StorageUnavailable => Error(503, ControllerDispatchCode.Unavailable, "storage_unavailable", "The configuration store is unavailable.");
}

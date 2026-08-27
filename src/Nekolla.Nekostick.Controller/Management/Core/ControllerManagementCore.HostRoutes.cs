using System.Collections.Immutable;
using System.Linq;
using Nekolla.Nekostick.Contracts;

namespace Nekolla.Nekostick.Controller.Management;

internal sealed partial class ControllerManagementCore
{
    /// <summary>Provisions the private HostRoute bridge route; this is not a public dispatch path.</summary>
    internal async ValueTask ProvisionHostRouteAsync(string handlerId, CancellationToken cancellationToken)
    {
        _ = await ProvisionHostRouteAsync(handlerId, _options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Provisions a private HostRoute using candidate options during reconciliation.</summary>
    internal async ValueTask<ProvisionedHostRouteIdentity?> ProvisionHostRouteAsync(
        string handlerId,
        ControllerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.EnableHostRoute) return null;
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope(options) || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");

        var path = options.HostRoutePath ?? throw new InvalidOperationException("The Host route path is unavailable.");
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

    /// <summary>Provisions the ephemeral private HostRoute and returns its in-memory ownership identity.</summary>
    internal async ValueTask<ProvisionedHostRouteIdentity?> ProvisionBootstrapRouteAsync(
        string handlerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        var options = _options;
        if (!options.EnableHostRoute) return null;
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");

        var path = options.HostRoutePath ?? throw new InvalidOperationException("The Host route path is unavailable.");
        var read = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
        var handlerRoutes = snapshot.Routes.Where(route => route.Target is ExtensionHandlerRouteTarget target && string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)).ToArray();
        if (handlerRoutes.Length != 0) throw new InvalidOperationException("A controller handler route already exists after the stale-route sweep.");
        if (snapshot.Routes.Any(route => string.Equals(route.Matcher.Pattern, path, StringComparison.Ordinal) && (route.Target is not ExtensionHandlerRouteTarget target || !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)))) throw new InvalidOperationException("The configured controller route is already owned by another target.");

        var route = new ExtensionRouteConfiguration(Guid.CreateVersion7(), true, new RouteMatcherConfiguration(RouteMatcherType.Prefix, path, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty), new ExtensionHandlerRouteTarget(handlerId), int.MaxValue);
        var changes = new ExtensionConfigurationChangeSet(ImmutableArray.Create(route), ImmutableArray<Guid>.Empty, ImmutableArray<ExtensionServiceConfiguration>.Empty, ImmutableArray<Guid>.Empty, settings: null);
        var write = await bridge.ConfigurationApi.ApplyAsync(snapshot.Version, changes, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("The controller route could not be provisioned.");
        return new ProvisionedHostRouteIdentity(route.Id, path);
    }


    /// <summary>Removes stale bootstrap routes targeting this controller handler.</summary>
    internal async ValueTask CleanupStaleBootstrapRoutesAsync(
        string handlerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");
        var read = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
        var staleRouteIds = snapshot.Routes
            .Where(route => route.Target is ExtensionHandlerRouteTarget target && string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal))
            .Select(route => route.Id)
            .ToImmutableArray();
        if (staleRouteIds.Length == 0) return;
        var changes = new ExtensionConfigurationChangeSet(
            ImmutableArray<ExtensionRouteConfiguration>.Empty,
            staleRouteIds,
            ImmutableArray<ExtensionServiceConfiguration>.Empty,
            ImmutableArray<Guid>.Empty,
            settings: null);
        var write = await bridge.ConfigurationApi.ApplyAsync(snapshot.Version, changes, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("Stale controller bootstrap routes could not be removed.");
    }

    /// <summary>Atomically replaces a verified bootstrap route with a configured HostRoute.</summary>
    internal async ValueTask ReplaceBootstrapWithConfiguredHostRouteAsync(
        string handlerId,
        ProvisionedHostRouteIdentity identity,
        ControllerOptions options,
        CancellationToken cancellationToken)
    {
        if (identity.RouteId == Guid.Empty || string.IsNullOrEmpty(identity.CanonicalPath) || !ControllerOptions.IsCanonicalManagementPath(identity.CanonicalPath))
        {
            throw new ArgumentException("The provisioned route identity is invalid.", nameof(identity));
        }
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(handlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal) ||
            !options.EnableHostRoute || options.HostRoutePath is not { } path ||
            !ControllerOptions.IsCanonicalManagementPath(path))
        {
            throw new InvalidOperationException("The HostRoute transition identity is invalid.");
        }
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope(options) || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");
        var read = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");

        var bootstrapRoute = snapshot.Routes.SingleOrDefault(route => route.Id == identity.RouteId);
        if (bootstrapRoute is not null &&
            (bootstrapRoute.Target is not ExtensionHandlerRouteTarget target ||
             !string.Equals(target.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal) ||
             !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) ||
             bootstrapRoute.Matcher.Type != RouteMatcherType.Prefix ||
             !string.Equals(bootstrapRoute.Matcher.Pattern, identity.CanonicalPath, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The bootstrap route ownership proof no longer matches.");
        }

        var handlerRoutes = snapshot.Routes.Where(route =>
            route.Target is ExtensionHandlerRouteTarget target &&
            string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)).ToArray();
        var candidateRoute = handlerRoutes.Where(route =>
            route.Matcher.Type == RouteMatcherType.Prefix &&
            string.Equals(route.Matcher.Pattern, path, StringComparison.Ordinal)).ToArray();
        if (candidateRoute.Length > 1) throw new InvalidOperationException("Duplicate configured controller routes are configured.");
        if (snapshot.Routes.Any(route =>
            string.Equals(route.Matcher.Pattern, path, StringComparison.Ordinal) &&
            (route.Target is not ExtensionHandlerRouteTarget target || !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("The configured controller route is already owned by another target.");
        }

        if (bootstrapRoute is null)
        {
            if (candidateRoute.Length == 1) return;
            if (handlerRoutes.Length != 0) throw new InvalidOperationException("A non-bootstrap controller route already exists.");
        }
        else if (handlerRoutes.Any(route => route.Id != identity.RouteId))
        {
            throw new InvalidOperationException("A non-bootstrap controller route already exists.");
        }

        var configuredRoute = new ExtensionRouteConfiguration(
            bootstrapRoute?.Id ?? Guid.CreateVersion7(),
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Prefix, path, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            new ExtensionHandlerRouteTarget(handlerId),
            int.MaxValue);
        var changes = new ExtensionConfigurationChangeSet(
            ImmutableArray.Create(configuredRoute),
            ImmutableArray<Guid>.Empty,
            ImmutableArray<ExtensionServiceConfiguration>.Empty,
            ImmutableArray<Guid>.Empty,
            settings: null);
        var write = await bridge.ConfigurationApi.ApplyAsync(snapshot.Version, changes, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("The configured controller route could not be provisioned.");
    }

    /// <summary>Atomically ensures the desired configured HostRoute and removes only its old path.</summary>
    internal async ValueTask EnsureConfiguredHostRouteAsync(
        string handlerId,
        string? oldCanonicalPath,
        ControllerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        if (!string.Equals(handlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal) ||
            !options.EnableHostRoute || options.HostRoutePath is not { } newPath ||
            !ControllerOptions.IsCanonicalManagementPath(newPath) ||
            (oldCanonicalPath is not null && !ControllerOptions.IsCanonicalManagementPath(oldCanonicalPath)))
        {
            throw new InvalidOperationException("The configured controller route identity is invalid.");
        }

        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope(options) || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");
        var read = await bridge.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");

        var handlerRoutes = snapshot.Routes.Where(route =>
            route.Target is ExtensionHandlerRouteTargetConfiguration target &&
            string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal)).ToArray();
        if (handlerRoutes.Any(route =>
            !string.Equals(route.Matcher.Pattern, newPath, StringComparison.Ordinal) &&
            (oldCanonicalPath is null || !string.Equals(route.Matcher.Pattern, oldCanonicalPath, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("A controller route exists at an unexpected path.");
        }

        var oldMatches = oldCanonicalPath is null
            ? Array.Empty<RouteConfiguration>()
            : handlerRoutes.Where(route => string.Equals(route.Matcher.Pattern, oldCanonicalPath, StringComparison.Ordinal)).ToArray();
        if (oldMatches.Length > 1) throw new InvalidOperationException("Duplicate configured controller routes are configured.");
        var newMatches = handlerRoutes.Where(route => string.Equals(route.Matcher.Pattern, newPath, StringComparison.Ordinal)).ToArray();
        if (newMatches.Length > 1) throw new InvalidOperationException("Duplicate configured controller routes are configured.");
        if (snapshot.Routes.Any(route =>
            string.Equals(route.Matcher.Pattern, newPath, StringComparison.Ordinal) &&
            (route.Target is not ExtensionHandlerRouteTargetConfiguration target || !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("The configured controller route is already owned by another target.");
        }

        var oldRoute = oldMatches.SingleOrDefault();
        var newRouteExisting = newMatches.SingleOrDefault();
        if (handlerRoutes.Any(route =>
            (oldRoute is null || route.Id != oldRoute.Id) &&
            (newRouteExisting is null || route.Id != newRouteExisting.Id)))
        {
            throw new InvalidOperationException("Duplicate configured controller routes are configured.");
        }
        if (oldRoute is null && newRouteExisting is not null && IsDesiredConfiguredRoute(newRouteExisting, handlerId, newPath)) return;

        var desiredRoute = new RouteConfiguration(
            newRouteExisting?.Id ?? oldRoute?.Id ?? Guid.CreateVersion7(),
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Prefix, newPath, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            new ExtensionHandlerRouteTargetConfiguration(handlerId),
            int.MaxValue,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            string.Empty,
            newRouteExisting?.CreatedAt ?? oldRoute?.CreatedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            newRouteExisting?.Version ?? oldRoute?.Version ?? 0);
        var removedIds = new HashSet<Guid>();
        if (oldRoute is not null) removedIds.Add(oldRoute.Id);
        if (newRouteExisting is not null) removedIds.Add(newRouteExisting.Id);
        var routes = snapshot.Routes.Where(route => !removedIds.Contains(route.Id)).Append(desiredRoute).ToImmutableArray();
        var write = await bridge.FullConfiguration.ReplaceAsync(
            snapshot.Version,
            new ConfigurationChangeSet(snapshot.GlobalSettings, routes, snapshot.Services, snapshot.ExtensionRecords, snapshot.ExtensionSettings),
            cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("The configured controller route could not be provisioned.");
    }

    /// <summary>Removes only the exact configured controller route for a handler and path.</summary>
    internal async ValueTask RemoveConfiguredHostRouteAsync(
        string handlerId,
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        if (!string.Equals(handlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal) ||
            !ControllerOptions.IsCanonicalManagementPath(canonicalPath))
        {
            throw new InvalidOperationException("The configured controller route identity is invalid.");
        }

        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");
        var read = await bridge.FullConfiguration.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
        var candidates = snapshot.Routes.Where(route =>
            route.Target is ExtensionHandlerRouteTargetConfiguration target &&
            string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) &&
            route.Matcher.Type == RouteMatcherType.Prefix &&
            string.Equals(route.Matcher.Pattern, canonicalPath, StringComparison.Ordinal)).ToArray();
        if (candidates.Length == 0) return;
        if (candidates.Length != 1) throw new InvalidOperationException("Duplicate configured controller routes are configured.");

        var routeId = candidates[0].Id;
        var routes = snapshot.Routes.Where(route => route.Id != routeId).ToImmutableArray();
        var write = await bridge.FullConfiguration.ReplaceAsync(
            snapshot.Version,
            new ConfigurationChangeSet(snapshot.GlobalSettings, routes, snapshot.Services, snapshot.ExtensionRecords, snapshot.ExtensionSettings),
            cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("The configured controller route could not be removed.");
    }

    /// <summary>Removes only the exact private bootstrap route identity after rechecking ownership.</summary>
    internal async ValueTask RemoveProvisionedHostRouteAsync(
        string handlerId,
        ProvisionedHostRouteIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerId);
        if (!string.Equals(handlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal)) throw new InvalidOperationException("The bootstrap handler identity is not fixed.");
        if (identity.RouteId == Guid.Empty || string.IsNullOrEmpty(identity.CanonicalPath) || !ControllerOptions.IsCanonicalManagementPath(identity.CanonicalPath)) throw new ArgumentException("The provisioned route identity is invalid.", nameof(identity));
        var bridge = _bridge ?? throw new InvalidOperationException("The controller bridge is unavailable.");
        if (!HasFullConfigurationScope() || !ExtensionAbi.IsApi13Supported(bridge.ApiVersion)) throw new NotSupportedException("The private Host route capability is unavailable.");
        var read = await bridge.ConfigurationApi.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot) throw new InvalidOperationException("The controller route configuration is unavailable.");
        var candidate = snapshot.Routes.SingleOrDefault(route => route.Id == identity.RouteId);
        if (candidate is null) return;
        if (candidate.Target is not ExtensionHandlerRouteTarget target ||
            !string.Equals(target.HandlerId, ControllerManagementApiContract.HandlerId, StringComparison.Ordinal) ||
            !string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) ||
            candidate.Matcher.Type != RouteMatcherType.Prefix ||
            !string.Equals(candidate.Matcher.Pattern, identity.CanonicalPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The provisioned controller route ownership proof no longer matches.");
        }

        var changes = new ExtensionConfigurationChangeSet(
            ImmutableArray<ExtensionRouteConfiguration>.Empty,
            ImmutableArray.Create(identity.RouteId),
            ImmutableArray<ExtensionServiceConfiguration>.Empty,
            ImmutableArray<Guid>.Empty,
            settings: null);
        var write = await bridge.ConfigurationApi.ApplyAsync(snapshot.Version, changes, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess) throw new InvalidOperationException("The controller route could not be removed.");
    }


    private static bool IsDesiredConfiguredRoute(RouteConfiguration route, string handlerId, string path) =>
        route.Enabled &&
        route.Target is ExtensionHandlerRouteTargetConfiguration target &&
        string.Equals(target.HandlerId, handlerId, StringComparison.Ordinal) &&
        route.Matcher.Type == RouteMatcherType.Prefix &&
        string.Equals(route.Matcher.Pattern, path, StringComparison.Ordinal) &&
        route.Matcher.HostPatterns.IsDefaultOrEmpty &&
        route.Matcher.Methods.IsDefaultOrEmpty &&
        route.Priority == int.MaxValue &&
        route.Forwarding.Mode == ForwardingMode.Preserve &&
        route.Forwarding.ReplaceTemplate is null &&
        route.RequestHeaderRewrites.IsDefaultOrEmpty &&
        route.ResponseHeaderRewrites.IsDefaultOrEmpty &&
        string.Equals(route.MetadataJson, string.Empty, StringComparison.Ordinal);


}

using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ControllerEntrypointTests
{
    [Fact]
    public async Task StartAsync_WhenOwnSettingsAreMissing_CreatesDefaultSettingsAndBootstraps()
    {
        var host = new FakeHostBridge(new HostConfigurationSnapshot(
            version: 1,
            globalSettings: new GlobalSettingsConfiguration(
                version: 1,
                autoPortRangeStart: 20_000,
                autoPortRangeEnd: 29_999,
                maxRequestBodyBytes: 1_048_576,
                maxConcurrentRequests: 128,
                configurationPollInterval: TimeSpan.FromSeconds(5),
                trustedProxyCidrs: ImmutableArray<string>.Empty,
                proxyTimeouts: ProxyTimeoutConfiguration.Default,
                maxRequestHeaderBytes: 32_768,
                requestReadTimeout: TimeSpan.FromSeconds(30),
                clientIpRatePolicy: null,
                proxyRetries: ProxyRetryConfiguration.Default),
            routes: ImmutableArray<RouteConfiguration>.Empty,
            services: ImmutableArray<ServiceConfiguration>.Empty,
            extensionRecords: ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings: ImmutableArray<ExtensionSettingsConfiguration>.Empty));
        var registration = new FakeExtensionRegistration();
        using var entrypoint = new ControllerEntrypoint();

        await entrypoint.StartAsync(
            new FakeExtensionStartContext(host, registration),
            TestContext.Current.CancellationToken);

        var settings = Assert.Single(host.ReadSnapshot().ExtensionSettings);
        Assert.Equal(ControllerOptions.ExtensionId, settings.ExtensionId);
        Assert.Equal(ControllerOptions.ConfigurationSchemaVersion, settings.SchemaVersion);
        Assert.Equal("{}", settings.SettingsJson);
        Assert.Equal(0, settings.Version);
        Assert.NotNull(registration.Handler);

        // The '{}' bootstrap settings default CORS to allow-any so a hosted web UI can reach the
        // ephemeral route cross-origin; preflight is answered without an API key.
        var preflight = await registration.Handler.HandleAsync(
            new ExtensionHandlerRequest(
                "OPTIONS",
                "/v1",
                ImmutableDictionary<string, IEnumerable<string>>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("Origin", ImmutableArray.Create("http://hosted-webui.example"))
                    .Add("Access-Control-Request-Method", ImmutableArray.Create("GET")),
                ReadOnlyMemory<byte>.Empty,
                isHttps: false),
            TestContext.Current.CancellationToken);
        Assert.Equal(204, preflight.StatusCode);
        Assert.Contains(preflight.Headers, pair =>
            string.Equals(pair.Key, "Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase) &&
            pair.Value.Single() == "*");

        await entrypoint.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_WhenBootstrapRouteProvisioningWriteFails_PreservesOriginalFailureAndCleansUp()
    {
        var host = new FakeHostBridge(new HostConfigurationSnapshot(
            version: 1,
            globalSettings: new GlobalSettingsConfiguration(
                version: 1,
                autoPortRangeStart: 20_000,
                autoPortRangeEnd: 29_999,
                maxRequestBodyBytes: 1_048_576,
                maxConcurrentRequests: 128,
                configurationPollInterval: TimeSpan.FromSeconds(5),
                trustedProxyCidrs: ImmutableArray<string>.Empty,
                proxyTimeouts: ProxyTimeoutConfiguration.Default,
                maxRequestHeaderBytes: 32_768,
                requestReadTimeout: TimeSpan.FromSeconds(30),
                clientIpRatePolicy: null,
                proxyRetries: ProxyRetryConfiguration.Default),
            routes: ImmutableArray<RouteConfiguration>.Empty,
            services: ImmutableArray<ServiceConfiguration>.Empty,
            extensionRecords: ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings: ImmutableArray.Create(new ExtensionSettingsConfiguration(
                ControllerOptions.ExtensionId,
                ControllerOptions.ConfigurationSchemaVersion,
                "{}",
                0))));
        host.FailNextApply(ConfigurationErrorCode.StorageUnavailable);
        using var entrypoint = new ControllerEntrypoint();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            entrypoint.StartAsync(
                new FakeExtensionStartContext(host, new FakeExtensionRegistration()),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("The controller route could not be provisioned.", exception.Message);
        Assert.Empty(host.ReadSnapshot().Routes);
        var settings = Assert.Single(host.ReadSnapshot().ExtensionSettings);
        Assert.Equal("{}", settings.SettingsJson);

        await entrypoint.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_BootstrapThenReloadWithHostRoute_MorphsBootstrapRouteInPlace()
    {
        var host = new FakeHostBridge(CreateOwnedSnapshot(ImmutableArray<RouteConfiguration>.Empty));
        var registration = new FakeExtensionRegistration();
        using var entrypoint = new ControllerEntrypoint();
        var cancellationToken = TestContext.Current.CancellationToken;

        await entrypoint.StartAsync(
            new FakeExtensionStartContext(host, registration),
            cancellationToken);

        var bootstrapRoute = Assert.Single(host.ReadSnapshot().Routes);
        var bootstrapApiKey = ReadBootstrapApiKey(host);
        const string configuredPath = "/configured-controller";
        const string configuredApiKey = "configured-integration-key-0123456789abcdef";
        var settings = new ExtensionSettingsConfiguration(
            ControllerOptions.ExtensionId,
            ControllerOptions.ConfigurationSchemaVersion,
            "{\"enableHostRoute\":true,\"hostRoutePath\":\"/configured-controller\",\"apiKey\":\"configured-integration-key-0123456789abcdef\",\"apiScope\":\"FullConfiguration\"}",
            1);
        var snapshot = host.ReadSnapshot();
        var settingsWrite = await host.ConfigurationApi.ApplyAsync(
            snapshot.Version,
            new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                settings),
            cancellationToken);
        Assert.True(settingsWrite.IsSuccess);

        var handler = registration.Handler ?? throw new InvalidOperationException("The HostRoute handler is not registered.");
        var rootResponse = await InvokeHandlerAsync(
            handler,
            bootstrapApiKey,
            "GET",
            ControllerManagementApiContract.RootPath,
            cancellationToken: cancellationToken);
        Assert.Equal(200, rootResponse.StatusCode);
        var etag = ReadHeader(rootResponse, ControllerManagementApiContract.ETagHeaderName);

        var reloadResponse = await InvokeHandlerAsync(
            handler,
            bootstrapApiKey,
            "POST",
            ControllerManagementApiContract.ReloadSettingsPath,
            ifMatch: etag,
            cancellationToken: cancellationToken);
        Assert.Equal(200, reloadResponse.StatusCode);

        var configuredRoute = Assert.Single(host.ReadSnapshot().Routes);
        Assert.Equal(bootstrapRoute.Id, configuredRoute.Id);
        Assert.Equal(configuredPath, configuredRoute.Matcher.Pattern);

        var stateResponse = await InvokeHandlerAsync(
            handler,
            configuredApiKey,
            "GET",
            ControllerManagementApiContract.StatePath,
            cancellationToken: cancellationToken);
        Assert.Equal(200, stateResponse.StatusCode);
        using var stateDocument = JsonDocument.Parse(stateResponse.Body.ToArray());
        Assert.False(stateDocument.RootElement.GetProperty("data").GetProperty("bootstrapMode").GetBoolean());

        await entrypoint.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task StartAsync_WhenStaleRouteSweepFails_SurfacesOriginalFailureAndRecoversOnRetry()
    {
        var staleRoute = CreateHandlerRoute("/stale-bootstrap");
        var host = new FakeHostBridge(CreateOwnedSnapshot(ImmutableArray.Create(staleRoute)));
        host.FailNextApply(ConfigurationErrorCode.StorageUnavailable);
        using var entrypoint = new ControllerEntrypoint();
        var cancellationToken = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            entrypoint.StartAsync(
                new FakeExtensionStartContext(host, new FakeExtensionRegistration()),
                cancellationToken).AsTask());

        Assert.Equal("Stale controller bootstrap routes could not be removed.", exception.Message);
        Assert.Equal(staleRoute.Id, Assert.Single(host.ReadSnapshot().Routes).Id);

        await entrypoint.StartAsync(
            new FakeExtensionStartContext(host, new FakeExtensionRegistration()),
            cancellationToken);

        var provisionedRoute = Assert.Single(host.ReadSnapshot().Routes);
        Assert.NotEqual(staleRoute.Id, provisionedRoute.Id);
        Assert.Equal(ControllerManagementApiContract.HandlerId, ((ExtensionHandlerRouteTargetConfiguration)provisionedRoute.Target).HandlerId);

        await entrypoint.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task StartAsync_WhenReplacingHttpListener_DefersBindUntilPreviousStopped()
    {
        var port = ControllerApiFixture.ReserveDualStackLoopbackPort();
        const string firstKey = "first-generation-key-0123456789abcdef";
        const string secondKey = "second-generation-key-0123456789abc";
        var host = new FakeHostBridge(CreateOwnedSnapshot(
            ImmutableArray<RouteConfiguration>.Empty,
            extensionRecords: ImmutableArray.Create(CreateControllerRecord())));
        using var first = new ControllerEntrypoint(new ControllerOptions
        {
            EnableHttpJson = true,
            HttpPort = port,
            ApiKey = firstKey
        });
        using var second = new ControllerEntrypoint(new ControllerOptions
        {
            EnableHttpJson = true,
            HttpPort = port,
            ApiKey = secondKey
        });
        var cancellationToken = TestContext.Current.CancellationToken;

        await first.StartAsync(
            new FakeExtensionStartContext(host, new FakeExtensionRegistration()),
            cancellationToken);

        // The management listing now reports the running previous generation, exactly as the
        // host would while the replacement candidate starts.
        host.SetExtensionRunning(ControllerOptions.ExtensionId, running: true);

        // The host starts the replacement while the previous generation still holds the port;
        // binding now would collide, so this start must complete without touching the listener.
        await second.StartAsync(
            new FakeExtensionStartContext(host, new FakeExtensionRegistration(), reloading: true),
            cancellationToken);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Assert.Equal(HttpStatusCode.OK, await GetStateStatusAsync(client, firstKey, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStateStatusAsync(client, secondKey, cancellationToken));

        await first.StopAsync(cancellationToken);
        await second.OnPreviousStoppedAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, await GetStateStatusAsync(client, secondKey, cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStateStatusAsync(client, firstKey, cancellationToken));

        await second.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task OnPreviousStoppedAsync_WhenDeferredAcquisitionFails_ReportsDegradedAndContinues()
    {
        var port = ControllerApiFixture.ReserveDualStackLoopbackPort();
        const string apiKey = "replacement-failure-key-0123456789abcdef";
        var host = new FakeHostBridge(CreateOwnedSnapshot(
            ImmutableArray<RouteConfiguration>.Empty,
            extensionRecords: ImmutableArray.Create(CreateControllerRecord())));
        using var replacement = new ControllerEntrypoint(new ControllerOptions
        {
            EnableHttpJson = true,
            HttpPort = port,
            ApiKey = apiKey
        });
        var cancellationToken = TestContext.Current.CancellationToken;

        host.SetExtensionRunning(ControllerOptions.ExtensionId, running: true);

        await replacement.StartAsync(
            new FakeExtensionStartContext(host, new FakeExtensionRegistration(), reloading: true),
            cancellationToken);

        // A foreign holder grabbed the port while the replacement waited for the previous
        // generation. The commit is already past the point of no return, so the hook must
        // report the failure and continue instead of throwing: the contract keeps the old
        // generation stopped either way, and continuing leaves the registered HostRoute
        // handler serving so the conflict stays fixable through the API.
        using var foreign = new TcpListener(IPAddress.Loopback, port);
        foreign.Start();

        await replacement.OnPreviousStoppedAsync(cancellationToken);
        Assert.Contains(host.ReportedStatuses, status =>
            status.Kind == ExtensionStatusKind.Degraded &&
            string.Equals(status.Code, "startup.deferred_acquisition_failed", StringComparison.Ordinal));
        Assert.Contains("deferred resource acquisition failed", host.LastLogText, StringComparison.OrdinalIgnoreCase);

        await replacement.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task StartAsync_BootstrapReplacement_DefersRouteProvisioningUntilPreviousStopped()
    {
        var host = new FakeHostBridge(CreateOwnedSnapshot(
            ImmutableArray<RouteConfiguration>.Empty,
            extensionRecords: ImmutableArray.Create(CreateControllerRecord())));
        var firstRegistration = new FakeExtensionRegistration();
        var secondRegistration = new FakeExtensionRegistration();
        using var first = new ControllerEntrypoint();
        using var second = new ControllerEntrypoint();
        var cancellationToken = TestContext.Current.CancellationToken;

        await first.StartAsync(
            new FakeExtensionStartContext(host, firstRegistration),
            cancellationToken);

        host.SetExtensionRunning(ControllerOptions.ExtensionId, running: true);
        var bootstrapRoute = Assert.Single(host.ReadSnapshot().Routes);

        const string configuredPath = "/configured-controller";
        const string configuredApiKey = "configured-integration-key-0123456789abcdef";
        var snapshot = host.ReadSnapshot();
        var settingsWrite = await host.ConfigurationApi.ApplyAsync(
            snapshot.Version,
            new ExtensionConfigurationChangeSet(
                ImmutableArray<ExtensionRouteConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                ImmutableArray<ExtensionServiceConfiguration>.Empty,
                ImmutableArray<Guid>.Empty,
                new ExtensionSettingsConfiguration(
                    ControllerOptions.ExtensionId,
                    ControllerOptions.ConfigurationSchemaVersion,
                    "{\"enableHostRoute\":true,\"hostRoutePath\":\"/configured-controller\",\"apiKey\":\"configured-integration-key-0123456789abcdef\",\"apiScope\":\"FullConfiguration\"}",
                    1)),
            cancellationToken);
        Assert.True(settingsWrite.IsSuccess);

        // The replacement hydrates the configured options and must not rewrite the bootstrap
        // route the previous generation still owns; doing so breaks the stop-time ownership proof.
        await second.StartAsync(
            new FakeExtensionStartContext(host, secondRegistration, reloading: true),
            cancellationToken);
        var untouchedRoute = Assert.Single(host.ReadSnapshot().Routes);
        Assert.Equal(bootstrapRoute.Id, untouchedRoute.Id);
        Assert.Equal(bootstrapRoute.Matcher.Pattern, untouchedRoute.Matcher.Pattern);

        await first.StopAsync(cancellationToken);
        Assert.Empty(host.ReadSnapshot().Routes);

        await second.OnPreviousStoppedAsync(cancellationToken);
        var configuredRoute = Assert.Single(host.ReadSnapshot().Routes);
        Assert.Equal(configuredPath, configuredRoute.Matcher.Pattern);

        var handler = secondRegistration.Handler
            ?? throw new InvalidOperationException("The replacement HostRoute handler is not registered.");
        var stateResponse = await InvokeHandlerAsync(
            handler,
            configuredApiKey,
            "GET",
            ControllerManagementApiContract.StatePath,
            cancellationToken: cancellationToken);
        Assert.Equal(200, stateResponse.StatusCode);
        using var stateDocument = JsonDocument.Parse(stateResponse.Body.ToArray());
        Assert.False(stateDocument.RootElement.GetProperty("data").GetProperty("bootstrapMode").GetBoolean());

        await second.StopAsync(cancellationToken);
    }

    private static async Task<HttpStatusCode> GetStateStatusAsync(
        HttpClient client,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ControllerManagementApiContract.StatePath);
        request.Headers.Add(ControllerManagementApiContract.ApiKeyHeaderName, apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    private static HostConfigurationSnapshot CreateOwnedSnapshot(
        ImmutableArray<RouteConfiguration> routes,
        string settingsJson = "{}",
        ImmutableArray<ExtensionRecordConfiguration>? extensionRecords = null) =>
        new(
            version: 1,
            globalSettings: new GlobalSettingsConfiguration(
                version: 1,
                autoPortRangeStart: 20_000,
                autoPortRangeEnd: 29_999,
                maxRequestBodyBytes: 1_048_576,
                maxConcurrentRequests: 128,
                configurationPollInterval: TimeSpan.FromSeconds(5),
                trustedProxyCidrs: ImmutableArray<string>.Empty,
                proxyTimeouts: ProxyTimeoutConfiguration.Default,
                maxRequestHeaderBytes: 32_768,
                requestReadTimeout: TimeSpan.FromSeconds(30),
                clientIpRatePolicy: null,
                proxyRetries: ProxyRetryConfiguration.Default),
            routes: routes,
            services: ImmutableArray<ServiceConfiguration>.Empty,
            extensionRecords: extensionRecords ?? ImmutableArray<ExtensionRecordConfiguration>.Empty,
            extensionSettings: ImmutableArray.Create(new ExtensionSettingsConfiguration(
                ControllerOptions.ExtensionId,
                ControllerOptions.ConfigurationSchemaVersion,
                settingsJson,
                0)));

    private static ExtensionRecordConfiguration CreateControllerRecord() =>
        new(
            ControllerOptions.ExtensionId,
            "1.0.0",
            ExtensionLoadState.Loaded,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            recordVersion: 1);

    private static RouteConfiguration CreateHandlerRoute(string path)
    {
        var now = DateTimeOffset.UtcNow;
        return new RouteConfiguration(
            Guid.CreateVersion7(),
            true,
            new RouteMatcherConfiguration(
                RouteMatcherType.Prefix,
                path,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            new ExtensionHandlerRouteTargetConfiguration(ControllerManagementApiContract.HandlerId),
            int.MaxValue,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            string.Empty,
            now,
            now,
            1,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static string ReadBootstrapApiKey(FakeHostBridge host)
    {
        var message = host.LastLogText ?? throw new InvalidOperationException("The bootstrap secret was not logged.");
        const string marker = "; API key: ";
        var markerIndex = message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        return message[(markerIndex + marker.Length)..];
    }

    private static string ReadHeader(ExtensionHandlerResponse response, string name)
    {
        var header = response.Headers.Single(value => string.Equals(value.Key, name, StringComparison.OrdinalIgnoreCase));
        return Assert.Single(header.Value);
    }

    private static async ValueTask<ExtensionHandlerResponse> InvokeHandlerAsync(
        IExtensionHandler handler,
        string apiKey,
        string method,
        string path,
        string? body = null,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        var headers = ImmutableDictionary<string, IEnumerable<string>>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add(ControllerManagementApiContract.ApiKeyHeaderName, ImmutableArray.Create(apiKey));
        if (ifMatch is not null)
        {
            headers = headers.Add(ControllerManagementApiContract.IfMatchHeaderName, ImmutableArray.Create(ifMatch));
        }

        var requestBody = body is null
            ? ReadOnlyMemory<byte>.Empty
            : Encoding.UTF8.GetBytes(body).AsMemory();
        return await handler.HandleAsync(
            new ExtensionHandlerRequest(method, path, headers, requestBody, isHttps: false),
            cancellationToken);
    }
}

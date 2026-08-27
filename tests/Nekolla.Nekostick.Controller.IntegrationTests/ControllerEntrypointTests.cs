using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller;
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

        await entrypoint.StopAsync(TestContext.Current.CancellationToken);
    }
}

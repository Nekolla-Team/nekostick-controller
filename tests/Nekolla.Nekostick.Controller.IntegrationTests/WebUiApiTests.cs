using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

[CollectionDefinition("controller-web-ui", DisableParallelization = true)]
public sealed class ControllerWebUiCollectionDefinition
{
    public const string Name = "controller-web-ui";
}

public sealed class ControllerWebUiSettingsTests
{
    [Fact]
    public void ControllerOptions_EnableWebUiDefaultsFalseAndRoundTripsTrue()
    {
        var defaultSettings = new ExtensionSettingsConfiguration(
            ControllerOptions.ExtensionId,
            ControllerOptions.ConfigurationSchemaVersion,
            "{}",
            version: 0);
        Assert.True(ControllerOptions.TryParseHostSettings(defaultSettings, out var defaults));
        var parsedDefaults = defaults ?? throw new InvalidOperationException("Default options were not parsed.");
        Assert.False(parsedDefaults.EnableWebUi);

        var enabledSettings = new ExtensionSettingsConfiguration(
            ControllerOptions.ExtensionId,
            ControllerOptions.ConfigurationSchemaVersion,
            "{\"enableWebUi\":true}",
            version: 1);
        Assert.True(ControllerOptions.TryParseHostSettings(enabledSettings, out var enabled));
        var parsedEnabled = enabled ?? throw new InvalidOperationException("Enabled options were not parsed.");
        Assert.True(parsedEnabled.EnableWebUi);
    }
}

[Collection(ControllerWebUiCollectionDefinition.Name)]
public sealed class ControllerWebUiHttpTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    private const string Body = "<!doctype html><html><body>web-ui-test</body></html>";

    [Fact]
    public async Task HttpJson_GetRootServesFreshEmbeddedResourceWithoutApiKey()
    {
        SetResource(Body);
        try
        {
            await SetWebUiAsync(fixture, enabled: true);
            using var client = fixture.CreateHttpClient(withApiKey: false);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("Origin", ControllerApiFixture.CorsOrigin);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal((long)Encoding.UTF8.GetByteCount(Body), response.Content.Headers.ContentLength ?? -1);
            Assert.Equal(Body, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    [Fact]
    public async Task HttpJson_GetRootOpensFreshShellStreamPerRequest()
    {
        SetResource(Body);
        try
        {
            await SetWebUiAsync(fixture, enabled: true);
            using var client = fixture.CreateHttpClient(withApiKey: false);
            using (var first = await client.GetAsync("/", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Equal(Body, await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

            using var second = await client.GetAsync("/", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(Body, await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    [Fact]
    public async Task HttpJson_LegacyUiPathFallsThroughEvenWhenEnabled()
    {
        SetResource(Body);
        try
        {
            await SetWebUiAsync(fixture, enabled: true);
            using var client = fixture.CreateHttpClient();
            using var response = await client.GetAsync("/v1/ui", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    [Fact]
    public async Task HttpJson_GetUiWhenDisabledFallsThroughToNormalApiPipeline()
    {
        SetResource(Body);
        try
        {
            await SetWebUiAsync(fixture, enabled: false);
            using var client = fixture.CreateHttpClient();
            using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    [Fact]
    public async Task HttpJson_PostUiDoesNotServeTheShell()
    {
        SetResource(Body);
        try
        {
            await SetWebUiAsync(fixture, enabled: true);
            using var client = fixture.CreateHttpClient();
            using var response = await client.PostAsync("/", content: null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    [Fact]
    public async Task HttpJson_ReloadingEnableWebUiTogglesTheShellWithoutRestart()
    {
        SetResource(Body);
        try
        {
            using var client = fixture.CreateHttpClient();
            await SetWebUiAsync(fixture, enabled: false);
            using (var disabled = await client.GetAsync("/", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
            }

            await SetWebUiAsync(fixture, enabled: true);
            using var enabled = await client.GetAsync("/", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.Equal(Body, await enabled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    [Fact]
    public async Task HttpJson_GetUiWhenResourceAbsentFallsThroughEvenWhenEnabled()
    {
        ControllerWebUiResource.StreamFactory = static () => null;
        try
        {
            await SetWebUiAsync(fixture, enabled: true);
            using var client = fixture.CreateHttpClient();
            using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    [Fact]
    public async Task ControllerState_ReportsEmbeddedAndEnabledCombinations()
    {
        SetResource(Body);
        try
        {
            await SetWebUiAsync(fixture, enabled: false);
            using var client = fixture.CreateHttpClient();
            using (var disabledResponse = await client.GetAsync("/v1/controller/state", TestContext.Current.CancellationToken))
            {
                using var document = JsonDocument.Parse(await disabledResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                var webUi = document.RootElement.GetProperty("data").GetProperty("webUi");
                Assert.True(webUi.GetProperty("embedded").GetBoolean());
                Assert.False(webUi.GetProperty("enabled").GetBoolean());
            }

            await SetWebUiAsync(fixture, enabled: true);
            using var enabledResponse = await client.GetAsync("/v1/controller/state", TestContext.Current.CancellationToken);
            using var enabledDocument = JsonDocument.Parse(await enabledResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var enabledWebUi = enabledDocument.RootElement.GetProperty("data").GetProperty("webUi");
            Assert.True(enabledWebUi.GetProperty("embedded").GetBoolean());
            Assert.True(enabledWebUi.GetProperty("enabled").GetBoolean());
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }

    private static void SetResource(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ControllerWebUiResource.StreamFactory = () => new MemoryStream(bytes, writable: false);
    }

    internal static async Task SetWebUiAsync(
        ControllerApiFixture fixture,
        bool enabled)
    {
        var snapshot = fixture.Host.ReadSnapshot();
        var settings = snapshot.ExtensionSettings.Single(setting =>
            string.Equals(setting.ExtensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal));
        var document = JsonNode.Parse(settings.SettingsJson)?.AsObject()
            ?? throw new InvalidOperationException("The controller settings document is not an object.");
        document["enableWebUi"] = enabled;
        var updatedSettings = new ExtensionSettingsConfiguration(
            settings.ExtensionId,
            settings.SchemaVersion,
            document.ToJsonString(),
            settings.Version);
        var replacement = snapshot.ExtensionSettings
            .Select(setting => string.Equals(setting.ExtensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal)
                ? updatedSettings
                : setting)
            .ToImmutableArray();
        var write = fixture.Host.ReplaceSnapshot(snapshot.Version, new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            snapshot.Routes,
            snapshot.Services,
            snapshot.ExtensionRecords,
            replacement));
        Assert.True(write.IsSuccess);
        Assert.NotNull(write.NewVersion);

        using var client = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/controller/reload-settings");
        request.Headers.TryAddWithoutValidation(
            ControllerManagementApiContract.IfMatchHeaderName,
            $"\"{write.NewVersion!.Value}\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

[Collection(ControllerWebUiCollectionDefinition.Name)]
public sealed class ControllerWebUiHostRouteTests(ControllerApiFixture fixture, ControllerApi133Fixture api133Fixture)
    : IClassFixture<ControllerApiFixture>, IClassFixture<ControllerApi133Fixture>
{
    [Fact]
    public void HostRouteRegistration_UsesBufferedHandlerBeforeStreamingMinimum()
    {
        Assert.NotNull(fixture.Registration.Handler);
        Assert.Null(fixture.Registration.StreamingHandler);
    }

    [Fact]
    public async Task BufferedHostRoute_RootFallsThroughOnOlderHost()
    {
        await ControllerWebUiHttpTests.SetWebUiAsync(fixture, enabled: true);
        var response = await fixture.InvokeHostRouteAsync(
            "GET",
            ControllerApiFixture.HostRoutePrefix,
            withApiKey: true);

        Assert.Equal(404, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body.ToArray());
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void HostRouteRegistration_UsesStreamingHandlerOnApi133Host()
    {
        Assert.Null(api133Fixture.Registration.Handler);
        Assert.NotNull(api133Fixture.Registration.StreamingHandler);
    }

    [Fact]
    public async Task StreamingHostRoute_ServesWebUiAtRouteRootAndDispatchesManagement()
    {
        const string body = "<html><body>streaming-web-ui</body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        ControllerWebUiResource.StreamFactory = () => new MemoryStream(bytes, writable: false);
        try
        {
            await ControllerWebUiHttpTests.SetWebUiAsync(api133Fixture, enabled: true);
            var handler = api133Fixture.Registration.StreamingHandler
                ?? throw new InvalidOperationException("The streaming HostRoute handler is not registered.");

            var uiResponse = await handler.HandleStreamingAsync(
                new ExtensionStreamingRequest("GET", ControllerApiFixture.HostRoutePrefix, bodyStream: Stream.Null),
                TestContext.Current.CancellationToken);
            using (uiResponse.BodyStream)
            using (var reader = new StreamReader(uiResponse.BodyStream, Encoding.UTF8))
            {
                Assert.Equal(200, uiResponse.StatusCode);
                Assert.Equal("text/html", uiResponse.Headers["content-type"].Single());
                Assert.Equal(bytes.Length.ToString(CultureInfo.InvariantCulture), uiResponse.Headers["content-length"].Single());
                Assert.Equal(body, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
            }

            var trailingUiResponse = await handler.HandleStreamingAsync(
                new ExtensionStreamingRequest("GET", ControllerApiFixture.HostRoutePrefix + "/", bodyStream: Stream.Null),
                TestContext.Current.CancellationToken);
            using (trailingUiResponse.BodyStream)
            using (var trailingReader = new StreamReader(trailingUiResponse.BodyStream, Encoding.UTF8))
            {
                Assert.Equal(200, trailingUiResponse.StatusCode);
                Assert.Equal(body, await trailingReader.ReadToEndAsync(TestContext.Current.CancellationToken));
            }

            var headers = new[]
            {
                new KeyValuePair<string, IEnumerable<string>>(
                    ControllerManagementApiContract.ApiKeyHeaderName,
                    new[] { ControllerApiFixture.ApiKey })
            };

            var legacyResponse = await handler.HandleStreamingAsync(
                new ExtensionStreamingRequest(
                    "GET",
                    ControllerApiFixture.HostRoutePrefix + "/v1/ui",
                    headers,
                    Stream.Null),
                TestContext.Current.CancellationToken);
            using (legacyResponse.BodyStream)
            {
                Assert.Equal(404, legacyResponse.StatusCode);
                using var legacyDocument = JsonDocument.Parse(legacyResponse.BodyStream);
                Assert.Equal("not_found", legacyDocument.RootElement.GetProperty("code").GetString());
            }
            var managementResponse = await handler.HandleStreamingAsync(
                new ExtensionStreamingRequest(
                    "GET",
                    ControllerApiFixture.HostRoutePrefix + "/v1/services",
                    headers,
                    Stream.Null),
                TestContext.Current.CancellationToken);
            using (managementResponse.BodyStream)
            using (var reader = new StreamReader(managementResponse.BodyStream, Encoding.UTF8))
            {
                Assert.Equal(200, managementResponse.StatusCode);
                using var document = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            }
        }
        finally
        {
            ControllerWebUiResource.ResetStreamFactory();
        }
    }
}

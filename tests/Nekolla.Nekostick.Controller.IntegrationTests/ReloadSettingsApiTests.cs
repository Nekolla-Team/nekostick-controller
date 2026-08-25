using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ReloadSettingsApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task ReloadSettings_WithFreshEtagReturnsSuccessAndLeavesListenersRunning()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var etag = await ReadEtagAsync(client, "/v1", cancellationToken);
        Assert.True(long.TryParse(etag.Trim('\"'), out var version));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/controller/reload-settings");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, etag);
        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(etag, Assert.Single(response.Headers.GetValues(ControllerManagementApiContract.ETagHeaderName)));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        Assert.Equal(version, envelope.GetProperty("version").GetInt64());
        AssertListeners(envelope.GetProperty("data"));

        using var state = await client.GetAsync("/v1/controller/state", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        using var stateDocument = JsonDocument.Parse(await state.Content.ReadAsStringAsync(cancellationToken));
        AssertListeners(stateDocument.RootElement.GetProperty("data"));
    }

    [Fact]
    public async Task ReloadSettings_RequiresIfMatchAndEmptyBody()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using (var missing = new HttpRequestMessage(HttpMethod.Post, "/v1/controller/reload-settings"))
        using (var response = await client.SendAsync(missing, cancellationToken))
        {
            Assert.Equal((HttpStatusCode)428, response.StatusCode);
            await AssertErrorAsync(response, "precondition_required", cancellationToken);
        }

        var etag = await ReadEtagAsync(client, "/v1", cancellationToken);
        using var withBody = new HttpRequestMessage(HttpMethod.Post, "/v1/controller/reload-settings")
        {
            Content = new StringContent("{}", Encoding.UTF8, ControllerManagementApiContract.JsonMediaType)
        };
        withBody.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, etag);
        using var bodyResponse = await client.SendAsync(withBody, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bodyResponse.StatusCode);
        await AssertErrorAsync(bodyResponse, "invalid_request", cancellationToken);
    }

    [Fact]
    public async Task ReloadSettings_WithInvalidControllerSettings_ReturnsInvalidRequestAndKeepsListenersRunning()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var etag = await ReadEtagAsync(client, "/v1", cancellationToken);
        Assert.True(long.TryParse(etag.Trim('\"'), out var expectedVersion));

        var snapshot = fixture.Host.ReadSnapshot();
        Assert.Equal(expectedVersion, snapshot.Version);
        var originalSettings = snapshot.ExtensionSettings;
        var replacementSettings = originalSettings
            .Select(settings => string.Equals(settings.ExtensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal)
                ? new ExtensionSettingsConfiguration(settings.ExtensionId, 99, settings.SettingsJson, settings.Version)
                : settings)
            .ToImmutableArray();
        var write = fixture.Host.ReplaceSnapshot(expectedVersion, new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            snapshot.Routes,
            snapshot.Services,
            snapshot.ExtensionRecords,
            replacementSettings));
        Assert.True(write.IsSuccess);

        var freshEtag = await ReadEtagAsync(client, "/v1", cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/controller/reload-settings");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, freshEtag);
        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "invalid_request", cancellationToken);

        using var state = await client.GetAsync("/v1/controller/state", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        using var stateDocument = JsonDocument.Parse(await state.Content.ReadAsStringAsync(cancellationToken));
        AssertListeners(stateDocument.RootElement.GetProperty("data"));

        var restoreEtag = await ReadEtagAsync(client, "/v1", cancellationToken);
        Assert.True(long.TryParse(restoreEtag.Trim('\"'), out var restoreVersion));
        var current = fixture.Host.ReadSnapshot();
        Assert.Equal(restoreVersion, current.Version);
        var restore = fixture.Host.ReplaceSnapshot(restoreVersion, new ConfigurationChangeSet(
            current.GlobalSettings,
            current.Routes,
            current.Services,
            current.ExtensionRecords,
            originalSettings));
        Assert.True(restore.IsSuccess);
    }

    private static async Task<string> ReadEtagAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(ControllerManagementApiContract.ETagHeaderName, out var values));
        var etag = Assert.Single(values);
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        return etag;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, string code, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var envelope = document.RootElement;
        Assert.Equal(ControllerManagementApiContract.Version, envelope.GetProperty("apiVersion").GetInt32());
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(code, envelope.GetProperty("code").GetString());
    }

    private static void AssertSuccessfulEnvelope(JsonElement envelope)
    {
        Assert.Equal(ControllerManagementApiContract.Version, envelope.GetProperty("apiVersion").GetInt32());
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", envelope.GetProperty("code").GetString());
    }

    private static void AssertListeners(JsonElement data)
    {
        Assert.False(data.GetProperty("bootstrapMode").GetBoolean());
        var listeners = data.GetProperty("listeners");
        AssertListener(listeners.GetProperty("hostRoute"));
        AssertListener(listeners.GetProperty("httpJson"));
        AssertListener(listeners.GetProperty("grpc"));
        AssertListener(listeners.GetProperty("unixSocket"));
    }

    private static void AssertListener(JsonElement listener)
    {
        Assert.True(listener.GetProperty("enabled").GetBoolean());
        Assert.True(listener.GetProperty("running").GetBoolean());
    }
}

using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ExtensionsApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task GetExtensions_ReturnsConfiguredExtensionRecord()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1/extensions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        var extensions = envelope.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, extensions.ValueKind);
        var extension = extensions.EnumerateArray().Single(item => item.GetProperty("extensionId").GetString() == ControllerApiFixture.TestExtensionId);
        Assert.Equal("Loaded", extension.GetProperty("loadState").GetString());
    }

    [Fact]
    public async Task GetExtensionMember_ReturnsSeededRecord()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync($"/v1/extensions/{ControllerApiFixture.TestExtensionId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(ControllerManagementApiContract.ETagHeaderName, out _));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        Assert.True(envelope.GetProperty("version").GetInt64() >= 2);
        var extension = envelope.GetProperty("data");
        Assert.Equal(ControllerApiFixture.TestExtensionId, extension.GetProperty("extensionId").GetString());
        Assert.Equal("Loaded", extension.GetProperty("loadState").GetString());
    }

    [Fact]
    public async Task GetExtensionMember_WithUnknownId_ReturnsNotFound()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1/extensions/nekolla.nekostick.ghost-extension", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertErrorEnvelope(document.RootElement, "not_found");
    }

    [Fact]
    public async Task ExtensionSettings_PutReadUpdateDeleteOverRealHttp()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var settingsPath = $"/v1/extensions/{ControllerApiFixture.TestExtensionId}/settings";

        var firstVersion = await ReadEtagAsync(client, "/v1/extensions", cancellationToken);
        using var firstRequest = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"dark"}}""", firstVersion);
        using var first = await client.SendAsync(firstRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync(cancellationToken));
        AssertSettings(firstDocument.RootElement, "dark");

        using var read = await client.GetAsync(settingsPath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readDocument = JsonDocument.Parse(await read.Content.ReadAsStringAsync(cancellationToken));
        AssertSettings(readDocument.RootElement, "dark");

        var secondVersion = await ReadEtagAsync(client, settingsPath, cancellationToken);
        using var secondRequest = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"light"}}""", secondVersion);
        using var second = await client.SendAsync(secondRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync(cancellationToken));
        AssertSettings(secondDocument.RootElement, "light");

        var deleteVersion = await ReadEtagAsync(client, settingsPath, cancellationToken);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, settingsPath);
        deleteRequest.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, deleteVersion);
        using var delete = await client.SendAsync(deleteRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var gone = await client.GetAsync(settingsPath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var goneDocument = JsonDocument.Parse(await gone.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(goneDocument.RootElement, "not_found");
    }

    [Fact]
    public async Task GhostExtensionSettings_ReturnNotFound()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string ghostId = "nekolla.nekostick.ghost";
        var settingsPath = $"/v1/extensions/{ghostId}/settings";
        var etag = await ReadEtagAsync(client, "/v1/extensions", cancellationToken);

        using var putRequest = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"dark"}}""", etag);
        using var put = await client.SendAsync(putRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        using var putDocument = JsonDocument.Parse(await put.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(putDocument.RootElement, "not_found");

        using var get = await client.GetAsync(settingsPath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var getDocument = JsonDocument.Parse(await get.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(getDocument.RootElement, "not_found");
    }

    [Fact]
    public async Task PutExtensionSettings_WithoutIfMatch_ReturnsPreconditionRequired()
    {
        using var client = fixture.CreateHttpClient();
        var settingsPath = $"/v1/extensions/{ControllerApiFixture.TestExtensionId}/settings";
        using var request = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"missing-if-match"}}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertErrorEnvelope(document.RootElement, "precondition_required");
    }

    private static void AssertSettings(JsonElement envelope, string theme)
    {
        AssertSuccessfulEnvelope(envelope);
        var data = envelope.GetProperty("data");
        Assert.Equal(ControllerApiFixture.TestExtensionId, data.GetProperty("extensionId").GetString());
        Assert.Equal(1, data.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(theme, data.GetProperty("settings").GetProperty("theme").GetString());
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, string body, string? etag = null, string mediaType = "application/json")
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, etag);
        }

        return request;
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

    private static void AssertSuccessfulEnvelope(JsonElement envelope)
    {
        Assert.Equal(1, envelope.GetProperty("apiVersion").GetInt32());
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", envelope.GetProperty("code").GetString());
    }

    private static void AssertErrorEnvelope(JsonElement envelope, string code)
    {
        Assert.Equal(1, envelope.GetProperty("apiVersion").GetInt32());
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(code, envelope.GetProperty("code").GetString());
    }
}

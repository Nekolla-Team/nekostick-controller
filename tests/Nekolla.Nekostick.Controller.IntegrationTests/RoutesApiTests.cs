using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class RoutesApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task GetRoutes_ReturnsSuccessfulArrayEnvelope()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1/routes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task RouteLifecycle_CreateReadPatchDeleteOverRealHttp()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string body = """
        {
            "enabled": true,
            "matcher": { "type": "prefix", "pattern": "/res-test" },
            "target": { "type": "staticFile", "rootPath": "/srv/res-test" },
            "priority": 10,
            "forwarding": { "mode": "preserve" },
            "metadataJson": "{}"
        }
        """;

        var createVersion = await ReadEtagAsync(client, "/v1/routes", cancellationToken);
        using var createRequest = CreateJsonRequest(HttpMethod.Post, "/v1/routes", body, createVersion);
        using var create = await client.SendAsync(createRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.True(create.Headers.TryGetValues(ControllerManagementApiContract.LocationHeaderName, out var locations));
        var location = Assert.Single(locations);

        using var createDocument = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
        var createdEnvelope = createDocument.RootElement;
        AssertSuccessfulEnvelope(createdEnvelope);
        var routeId = createdEnvelope.GetProperty("data").GetProperty("id").GetGuid();
        Assert.Equal('7', routeId.ToString("N")[12]);
        var routePath = $"/v1/routes/{routeId}";
        Assert.EndsWith(routePath, location);

        using var memberRead = await client.GetAsync(routePath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, memberRead.StatusCode);
        using var memberDocument = JsonDocument.Parse(await memberRead.Content.ReadAsStringAsync(cancellationToken));
        var memberEnvelope = memberDocument.RootElement;
        AssertSuccessfulEnvelope(memberEnvelope);
        var member = memberEnvelope.GetProperty("data");
        Assert.Equal(routeId, member.GetProperty("id").GetGuid());
        Assert.True(member.GetProperty("enabled").GetBoolean());
        Assert.Equal("Prefix", member.GetProperty("matcher").GetProperty("type").GetString());
        Assert.Equal("/res-test", member.GetProperty("matcher").GetProperty("pattern").GetString());
        Assert.Equal("StaticFile", member.GetProperty("target").GetProperty("type").GetString());
        Assert.Equal("/srv/res-test", member.GetProperty("target").GetProperty("rootPath").GetString());
        Assert.Equal(10, member.GetProperty("priority").GetInt32());
        Assert.Equal("Preserve", member.GetProperty("forwarding").GetProperty("mode").GetString());
        Assert.Equal("{}", member.GetProperty("metadataJson").GetString());

        var patchVersion = await ReadEtagAsync(client, routePath, cancellationToken);
        using var patchRequest = CreateJsonRequest(HttpMethod.Patch, routePath, """{"priority":20}""", patchVersion, "application/merge-patch+json");
        using var patch = await client.SendAsync(patchRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var patchDocument = JsonDocument.Parse(await patch.Content.ReadAsStringAsync(cancellationToken));
        var patchEnvelope = patchDocument.RootElement;
        AssertSuccessfulEnvelope(patchEnvelope);
        Assert.Equal(20, patchEnvelope.GetProperty("data").GetProperty("priority").GetInt32());

        var deleteVersion = await ReadEtagAsync(client, routePath, cancellationToken);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, routePath);
        deleteRequest.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, deleteVersion);
        using var delete = await client.SendAsync(deleteRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var gone = await client.GetAsync(routePath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var goneDocument = JsonDocument.Parse(await gone.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(goneDocument.RootElement, "not_found");
    }

    [Fact]
    public async Task RouteMutations_RequireIfMatchAndRejectStaleVersion()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string missingIfMatchBody = """
        {
            "enabled": true,
            "matcher": { "type": "prefix", "pattern": "/res-missing-if-match" },
            "target": { "type": "staticFile", "rootPath": "/srv/res-missing-if-match" },
            "priority": 10,
            "forwarding": { "mode": "preserve" },
            "metadataJson": "{}"
        }
        """;

        using var missingRequest = CreateJsonRequest(HttpMethod.Post, "/v1/routes", missingIfMatchBody);
        using var missing = await client.SendAsync(missingRequest, cancellationToken);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var missingDocument = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(missingDocument.RootElement, "precondition_required");

        var firstVersion = await ReadEtagAsync(client, "/v1/routes", cancellationToken);
        using var firstRequest = CreateJsonRequest(HttpMethod.Post, "/v1/routes", RouteBody("/res-stale-first", "/srv/res-stale-first"), firstVersion);
        using var first = await client.SendAsync(firstRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var secondVersion = await ReadEtagAsync(client, "/v1/routes", cancellationToken);
        using var secondRequest = CreateJsonRequest(HttpMethod.Post, "/v1/routes", RouteBody("/res-stale-second", "/srv/res-stale-second"), secondVersion);
        using var second = await client.SendAsync(secondRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var replayRequest = CreateJsonRequest(HttpMethod.Post, "/v1/routes", RouteBody("/res-stale-replay", "/srv/res-stale-replay"), firstVersion);
        using var replay = await client.SendAsync(replayRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(replayDocument.RootElement, "precondition_failed");
    }

    [Fact]
    public async Task GetRoute_WithUnknownGuid_ReturnsNotFound()
    {
        using var client = fixture.CreateHttpClient();
        var routePath = $"/v1/routes/{Guid.NewGuid()}";

        using var response = await client.GetAsync(routePath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertErrorEnvelope(document.RootElement, "not_found");
    }

    private static string RouteBody(string pattern, string rootPath) => $$"""
    {
        "enabled": true,
        "matcher": { "type": "prefix", "pattern": "{{pattern}}" },
        "target": { "type": "staticFile", "rootPath": "{{rootPath}}" },
        "priority": 10,
        "forwarding": { "mode": "preserve" },
        "metadataJson": "{}"
    }
    """;

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

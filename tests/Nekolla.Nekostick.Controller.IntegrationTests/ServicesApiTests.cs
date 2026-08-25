using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ServicesApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task ServiceLifecycle_CreateListReadPatchDeleteOverRealHttp()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = await CreateServiceAsync(client, cancellationToken);
        var servicePath = $"/v1/services/{serviceId}";

        using var list = await client.GetAsync("/v1/services", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync(cancellationToken));
        var listEnvelope = listDocument.RootElement;
        AssertSuccessfulEnvelope(listEnvelope);
        var services = listEnvelope.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, services.ValueKind);
        Assert.Contains(services.EnumerateArray(), item => item.GetProperty("id").GetGuid() == serviceId);

        using var read = await client.GetAsync(servicePath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readDocument = JsonDocument.Parse(await read.Content.ReadAsStringAsync(cancellationToken));
        var readEnvelope = readDocument.RootElement;
        AssertSuccessfulEnvelope(readEnvelope);
        var service = readEnvelope.GetProperty("data");
        Assert.Equal(serviceId, service.GetProperty("id").GetGuid());
        Assert.True(service.GetProperty("enabled").GetBoolean());
        Assert.Equal("/bin/echo", service.GetProperty("fileName").GetString());
        Assert.Equal("hello", service.GetProperty("argumentList")[0].GetString());
        Assert.Equal("/tmp", service.GetProperty("workingDirectory").GetString());
        Assert.Equal("Eager", service.GetProperty("startMode").GetString());
        Assert.Equal("Never", service.GetProperty("restartPolicy").GetString());
        var healthCheck = service.GetProperty("healthCheck");
        Assert.Equal("Process", healthCheck.GetProperty("type").GetString());
        Assert.Equal(5000L, healthCheck.GetProperty("timeoutMs").GetInt64());

        var patchVersion = await ReadEtagAsync(client, servicePath, cancellationToken);
        using var patchRequest = CreateJsonRequest(HttpMethod.Patch, servicePath, """{"restartPolicy":"always"}""", patchVersion, "application/merge-patch+json");
        using var patch = await client.SendAsync(patchRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var patchDocument = JsonDocument.Parse(await patch.Content.ReadAsStringAsync(cancellationToken));
        var patchEnvelope = patchDocument.RootElement;
        AssertSuccessfulEnvelope(patchEnvelope);
        Assert.Equal("Always", patchEnvelope.GetProperty("data").GetProperty("restartPolicy").GetString());

        var deleteVersion = await ReadEtagAsync(client, servicePath, cancellationToken);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, servicePath);
        deleteRequest.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, deleteVersion);
        using var delete = await client.SendAsync(deleteRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var gone = await client.GetAsync(servicePath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var goneDocument = JsonDocument.Parse(await gone.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(goneDocument.RootElement, "not_found");
    }

    [Fact]
    public async Task ServiceEnvironment_GetReplaceAndDeleteOverRealHttp()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = await CreateServiceAsync(client, cancellationToken);
        var environmentPath = $"/v1/services/{serviceId}/environment";

        using var initial = await client.GetAsync(environmentPath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var initialDocument = JsonDocument.Parse(await initial.Content.ReadAsStringAsync(cancellationToken));
        var initialEnvelope = initialDocument.RootElement;
        AssertSuccessfulEnvelope(initialEnvelope);
        var initialEnvironment = initialEnvelope.GetProperty("data");
        Assert.Equal(serviceId, initialEnvironment.GetProperty("serviceId").GetGuid());
        Assert.Equal("value1", initialEnvironment.GetProperty("environment").GetProperty("KEY").GetString());

        var putVersion = await ReadEtagAsync(client, environmentPath, cancellationToken);
        using var putRequest = CreateJsonRequest(HttpMethod.Put, environmentPath, """{"environment":{"OTHER":"2"}}""", putVersion);
        using var put = await client.SendAsync(putRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var putDocument = JsonDocument.Parse(await put.Content.ReadAsStringAsync(cancellationToken));
        AssertSuccessfulEnvelope(putDocument.RootElement);
        var putEnvironment = putDocument.RootElement.GetProperty("data").GetProperty("environment");
        Assert.Equal("2", putEnvironment.GetProperty("OTHER").GetString());
        Assert.False(putEnvironment.TryGetProperty("KEY", out _));
        Assert.Single(putEnvironment.EnumerateObject());

        using var replaced = await client.GetAsync(environmentPath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        using var replacedDocument = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync(cancellationToken));
        AssertSuccessfulEnvelope(replacedDocument.RootElement);
        var replacedEnvironment = replacedDocument.RootElement.GetProperty("data").GetProperty("environment");
        Assert.Equal("2", replacedEnvironment.GetProperty("OTHER").GetString());
        Assert.False(replacedEnvironment.TryGetProperty("KEY", out _));
        Assert.Single(replacedEnvironment.EnumerateObject());

        var deleteVersion = await ReadEtagAsync(client, environmentPath, cancellationToken);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, environmentPath);
        deleteRequest.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, deleteVersion);
        using var delete = await client.SendAsync(deleteRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var cleared = await client.GetAsync(environmentPath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var clearedDocument = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync(cancellationToken));
        AssertSuccessfulEnvelope(clearedDocument.RootElement);
        var clearedEnvironment = clearedDocument.RootElement.GetProperty("data").GetProperty("environment");
        Assert.Equal(JsonValueKind.Object, clearedEnvironment.ValueKind);
        Assert.False(clearedEnvironment.EnumerateObject().Any());
    }

    [Fact]
    public async Task PatchService_WithEnvironmentProperty_ReturnsInvalidRequest()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = await CreateServiceAsync(client, cancellationToken);
        var servicePath = $"/v1/services/{serviceId}";
        var etag = await ReadEtagAsync(client, servicePath, cancellationToken);

        using var request = CreateJsonRequest(HttpMethod.Patch, servicePath, """{"environment":{"KEY":"value2"}}""", etag, "application/merge-patch+json");
        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(document.RootElement, "invalid_request");
    }

    [Fact]
    public async Task ServiceRuntime_ReturnsSupervisorTelemetryAndUnknownIsNotFound()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = await CreateServiceAsync(client, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        fixture.Host.SetSupervisorSnapshots(ImmutableArray.Create(new ExtensionServiceRuntimeSnapshot(
            serviceId,
            1234,
            now - TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2),
            ExtensionServiceLifecycleState.Running,
            ExtensionServiceHealthState.Healthy,
            42,
            3,
            now,
            now - TimeSpan.FromSeconds(1))));

        using var list = await client.GetAsync("/v1/services/runtime", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.False(list.Headers.Contains(ControllerManagementApiContract.ETagHeaderName));
        using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync(cancellationToken));
        var listEnvelope = listDocument.RootElement;
        AssertSuccessfulEnvelope(listEnvelope);
        Assert.Equal(JsonValueKind.Null, listEnvelope.GetProperty("version").ValueKind);
        var snapshots = listEnvelope.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, snapshots.ValueKind);
        var snapshot = snapshots.EnumerateArray().Single(item => item.GetProperty("serviceId").GetGuid() == serviceId);
        Assert.Equal(1234, snapshot.GetProperty("processId").GetInt32());
        Assert.Equal("Running", snapshot.GetProperty("lifecycleState").GetString());
        Assert.Equal("Healthy", snapshot.GetProperty("healthState").GetString());
        Assert.Equal(42L, snapshot.GetProperty("forwardedRequestCount").GetInt64());
        Assert.Equal(3L, snapshot.GetProperty("activeForwardedRequestCount").GetInt64());

        var runtimePath = $"/v1/services/{serviceId}/runtime";
        using var member = await client.GetAsync(runtimePath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.False(member.Headers.Contains(ControllerManagementApiContract.ETagHeaderName));
        using var memberDocument = JsonDocument.Parse(await member.Content.ReadAsStringAsync(cancellationToken));
        var memberEnvelope = memberDocument.RootElement;
        AssertSuccessfulEnvelope(memberEnvelope);
        Assert.Equal(JsonValueKind.Null, memberEnvelope.GetProperty("version").ValueKind);
        Assert.Equal(serviceId, memberEnvelope.GetProperty("data").GetProperty("serviceId").GetGuid());

        var unknownPath = $"/v1/services/{Guid.NewGuid()}/runtime";
        using var unknown = await client.GetAsync(unknownPath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unknownDocument = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(unknownDocument.RootElement, "not_found");
    }

    private static async Task<Guid> CreateServiceAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var etag = await ReadEtagAsync(client, "/v1/services", cancellationToken);
        const string body = """
        {
            "enabled": true,
            "fileName": "/bin/echo",
            "argumentList": ["hello"],
            "workingDirectory": "/tmp",
            "environment": { "KEY": "value1" },
            "startMode": "eager",
            "restartPolicy": "never",
            "healthCheck": { "type": "process", "timeoutMs": 5000 }
        }
        """;
        using var request = CreateJsonRequest(HttpMethod.Post, "/v1/services", body, etag);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(ControllerManagementApiContract.LocationHeaderName, out var locations));
        var location = Assert.Single(locations);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        AssertSuccessfulEnvelope(document.RootElement);
        var serviceId = document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        Assert.EndsWith($"/v1/services/{serviceId}", location);
        return serviceId;
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

using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class GlobalSettingsApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task GetGlobalSettings_ReturnsVersionedSettings()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1/global-settings", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(ControllerManagementApiContract.ETagHeaderName, out var values));
        var etag = Assert.Single(values);
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        Assert.True(long.TryParse(etag.Trim('\"'), out var version));
        Assert.True(version >= 2);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        Assert.Equal(version, envelope.GetProperty("version").GetInt64());
        Assert.Equal(128, envelope.GetProperty("data").GetProperty("maxConcurrentRequests").GetInt32());
    }

    [Fact]
    public async Task PatchGlobalSettings_EnforcesCasAndMergePatchSemantics()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var etag = await ReadEtagAsync(client, "/v1/global-settings", cancellationToken);
        Assert.True(long.TryParse(etag.Trim('\"'), out var version));

        using (var missing = CreatePatchRequest("{\"maxConcurrentRequests\":256}"))
        using (var response = await client.SendAsync(missing, cancellationToken))
        {
            Assert.Equal((HttpStatusCode)428, response.StatusCode);
            await AssertErrorAsync(response, "precondition_required", cancellationToken);
        }

        foreach (var malformed in new[] { version.ToString(), "\"01\"", "*" })
        {
            using var malformedRequest = CreatePatchRequest("{\"maxConcurrentRequests\":256}", malformed);
            using var malformedResponse = await client.SendAsync(malformedRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
            await AssertErrorAsync(malformedResponse, "invalid_request", cancellationToken);
        }

        long newVersion;
        using (var correct = CreatePatchRequest("{\"maxConcurrentRequests\":256}", etag))
        using (var response = await client.SendAsync(correct, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var newEtag = Assert.Single(response.Headers.GetValues(ControllerManagementApiContract.ETagHeaderName));
            Assert.True(long.TryParse(newEtag.Trim('\"'), out newVersion));
            Assert.NotEqual(version, newVersion);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var envelope = document.RootElement;
            AssertSuccessfulEnvelope(envelope);
            Assert.Equal(256, envelope.GetProperty("data").GetProperty("maxConcurrentRequests").GetInt32());
            Assert.Equal(newVersion, envelope.GetProperty("version").GetInt64());
        }

        var snapshotAfterWrite = fixture.Host.ReadSnapshot();
        Assert.Equal(256, snapshotAfterWrite.GlobalSettings.MaxConcurrentRequests);
        Assert.Equal(newVersion, snapshotAfterWrite.Version);

        using (var stale = CreatePatchRequest("{\"maxConcurrentRequests\":512}", etag))
        using (var response = await client.SendAsync(stale, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            await AssertErrorAsync(response, "precondition_failed", cancellationToken);
        }

        var snapshotAfterStale = fixture.Host.ReadSnapshot();
        Assert.Equal(256, snapshotAfterStale.GlobalSettings.MaxConcurrentRequests);
        Assert.Equal(snapshotAfterWrite.Version, snapshotAfterStale.Version);
        var restoreEtag = await ReadEtagAsync(client, "/v1/global-settings", cancellationToken);
        using var restore = CreatePatchRequest("{\"maxConcurrentRequests\":128}", restoreEtag);
        using var restoreResponse = await client.SendAsync(restore, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        Assert.Equal(128, fixture.Host.ReadSnapshot().GlobalSettings.MaxConcurrentRequests);
    }

    [Fact]
    public async Task PatchGlobalSettings_WithTextPlainContentType_ReturnsInvalidRequest()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var etag = await ReadEtagAsync(client, "/v1/global-settings", cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/global-settings")
        {
            Content = new StringContent("{\"maxConcurrentRequests\":256}", Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, etag);
        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "invalid_request", cancellationToken);
    }

    [Fact]
    public async Task PatchGlobalSettings_WhenHostWriteConflicts_ReturnsPreconditionFailedWithoutMutating()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var etag = await ReadEtagAsync(client, "/v1/global-settings", cancellationToken);
        var snapshotBefore = fixture.Host.ReadSnapshot();

        // The If-Match check passes against the current snapshot, but the Host-side
        // write fails (as a real Host can when the aggregate moves between read and write).
        fixture.Host.FailNextReplace(ConfigurationErrorCode.ConcurrencyConflict);
        using var request = CreatePatchRequest("{\"maxConcurrentRequests\":256}", etag);
        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        await AssertErrorAsync(response, "precondition_failed", cancellationToken);

        var snapshotAfter = fixture.Host.ReadSnapshot();
        Assert.Equal(snapshotBefore.Version, snapshotAfter.Version);
        Assert.Equal(snapshotBefore.GlobalSettings.MaxConcurrentRequests, snapshotAfter.GlobalSettings.MaxConcurrentRequests);
    }

    private static HttpRequestMessage CreatePatchRequest(string body, string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/global-settings")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/merge-patch+json")
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
}

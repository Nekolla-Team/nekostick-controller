using System.Net;
using System.Text.Json;
using Nekolla.Nekostick.Controller.Grpc;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class TransportTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task UnixSocketTransport_ServesRootAndRequiresApiKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var client = fixture.CreateUnixSocketClient())
        using (var response = await client.GetAsync("/v1", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }

        using var noKeyClient = fixture.CreateUnixSocketClient(withApiKey: false);
        using var unauthorized = await noKeyClient.GetAsync("/v1", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task GrpcTransport_InvokesRootWithMetadataAndRejectsMissingMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new InvokeRequest { Method = "GET", Path = "/v1" };

        var client = fixture.CreateGrpcClient();
        var response = await client.InvokeAsync(request, headers: fixture.CreateGrpcMetadata(), cancellationToken: cancellationToken);
        Assert.Equal(200, response.StatusCode);
        using (var document = JsonDocument.Parse(response.Body.ToByteArray()))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }

        var noKeyClient = fixture.CreateGrpcClient(withApiKey: false);
        var unauthorized = await noKeyClient.InvokeAsync(request, headers: fixture.CreateGrpcMetadata(withApiKey: false), cancellationToken: cancellationToken);
        Assert.Equal(401, unauthorized.StatusCode);
    }

    [Fact]
    public async Task HttpJsonTransport_AnswersCorsPreflightWithoutApiKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1");
        preflight.Headers.Add("Origin", ControllerApiFixture.CorsOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-nekostick-controller-key");

        using var response = await client.SendAsync(preflight, cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ControllerApiFixture.CorsOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(
            response.Headers.GetValues("Access-Control-Allow-Headers"),
            value => value.Split(',').Select(static name => name.Trim()).Contains("x-nekostick-controller-key", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HttpJsonTransport_AddsCorsHeadersForAllowedOriginOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = fixture.CreateHttpClient();
        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/v1");
        allowed.Headers.Add("Origin", ControllerApiFixture.CorsOrigin);

        using var allowedResponse = await client.SendAsync(allowed, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Equal(ControllerApiFixture.CorsOrigin, allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("no-store", allowedResponse.Headers.GetValues("cache-control").Single());
        Assert.Contains(
            allowedResponse.Headers.GetValues("Access-Control-Expose-Headers"),
            value => value.Split(',').Select(static name => name.Trim()).Contains("etag", StringComparer.OrdinalIgnoreCase));

        using var disallowed = new HttpRequestMessage(HttpMethod.Get, "/v1");
        disallowed.Headers.Add("Origin", "http://unexpected.example");

        using var disallowedResponse = await client.SendAsync(disallowed, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, disallowedResponse.StatusCode);
        Assert.False(disallowedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task HostRouteTransport_NormalizesPrefixAndRequiresApiKey()
    {
        var response = await fixture.InvokeHostRouteAsync("GET", ControllerApiFixture.HostRoutePrefix + "/v1");
        Assert.Equal(200, response.StatusCode);
        Assert.Contains(response.Headers, pair =>
            string.Equals(pair.Key, "cache-control", StringComparison.OrdinalIgnoreCase) &&
            pair.Value.Single() == "no-store");
        using (var document = JsonDocument.Parse(response.Body.ToArray()))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }

        var unauthorized = await fixture.InvokeHostRouteAsync("GET", ControllerApiFixture.HostRoutePrefix + "/v1", withApiKey: false);
        Assert.Equal(401, unauthorized.StatusCode);
    }

    [Fact]
    public async Task HostRouteTransport_AnswersCorsPreflightWithoutApiKey()
    {
        var preflight = await fixture.InvokeHostRouteAsync(
            "OPTIONS",
            ControllerApiFixture.HostRoutePrefix + "/v1",
            withApiKey: false,
            extraHeaders: new[]
            {
                new KeyValuePair<string, string>("Origin", ControllerApiFixture.CorsOrigin),
                new KeyValuePair<string, string>("Access-Control-Request-Method", "GET"),
                new KeyValuePair<string, string>("Access-Control-Request-Headers", "x-nekostick-controller-key")
            });

        Assert.Equal(204, preflight.StatusCode);
        Assert.Contains(preflight.Headers, pair =>
            string.Equals(pair.Key, "Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase) &&
            pair.Value.Single() == ControllerApiFixture.CorsOrigin);
        Assert.Contains(preflight.Headers, pair =>
            string.Equals(pair.Key, "Access-Control-Allow-Headers", StringComparison.OrdinalIgnoreCase) &&
            pair.Value.Single().Contains("x-nekostick-controller-key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostRouteTransport_ExposesErrorsToAllowedBrowserOrigins()
    {
        var allowed = await fixture.InvokeHostRouteAsync(
            "GET",
            ControllerApiFixture.HostRoutePrefix + "/v1",
            withApiKey: false,
            extraHeaders: new[] { new KeyValuePair<string, string>("Origin", ControllerApiFixture.CorsOrigin) });

        Assert.Equal(401, allowed.StatusCode);
        Assert.Contains(allowed.Headers, pair =>
            string.Equals(pair.Key, "Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase) &&
            pair.Value.Single() == ControllerApiFixture.CorsOrigin);
        Assert.Contains(allowed.Headers, pair =>
            string.Equals(pair.Key, "Access-Control-Expose-Headers", StringComparison.OrdinalIgnoreCase) &&
            pair.Value.Single().Contains("etag", StringComparison.OrdinalIgnoreCase));

        var disallowed = await fixture.InvokeHostRouteAsync(
            "GET",
            ControllerApiFixture.HostRoutePrefix + "/v1",
            withApiKey: false,
            extraHeaders: new[] { new KeyValuePair<string, string>("Origin", "http://unexpected.example") });

        Assert.Equal(401, disallowed.StatusCode);
        Assert.DoesNotContain(disallowed.Headers, pair =>
            string.Equals(pair.Key, "Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase));
    }
}

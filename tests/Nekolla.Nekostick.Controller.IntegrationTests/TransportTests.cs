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
    public async Task HostRouteTransport_NormalizesPrefixAndRequiresApiKey()
    {
        var response = await fixture.InvokeHostRouteAsync("GET", ControllerApiFixture.HostRoutePrefix + "/v1");
        Assert.Equal(200, response.StatusCode);
        using (var document = JsonDocument.Parse(response.Body.ToArray()))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }

        var unauthorized = await fixture.InvokeHostRouteAsync("GET", ControllerApiFixture.HostRoutePrefix + "/v1", withApiKey: false);
        Assert.Equal(401, unauthorized.StatusCode);
    }
}

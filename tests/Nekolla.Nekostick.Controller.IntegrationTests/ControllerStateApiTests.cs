using System.Net;
using System.Text.Json;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ControllerStateApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task GetControllerState_ReportsAllFourRunningListeners()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1/controller/state", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(ControllerManagementApiContract.ETagHeaderName));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("version").ValueKind);

        var data = envelope.GetProperty("data");
        Assert.False(data.GetProperty("bootstrapMode").GetBoolean());
        var listeners = data.GetProperty("listeners");
        AssertListener(listeners.GetProperty("hostRoute"), enabled: true, running: true);
        AssertListener(listeners.GetProperty("httpJson"), enabled: true, running: true);
        AssertListener(listeners.GetProperty("grpc"), enabled: true, running: true);
        AssertListener(listeners.GetProperty("unixSocket"), enabled: true, running: true);
    }

    [Fact]
    public async Task GetControllerState_WithIfMatch_ReturnsInvalidRequest()
    {
        using var client = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/controller/state");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, "\"0\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());
    }

    private static void AssertListener(JsonElement listener, bool enabled, bool running)
    {
        Assert.Equal(enabled, listener.GetProperty("enabled").GetBoolean());
        Assert.Equal(running, listener.GetProperty("running").GetBoolean());
    }

    private static void AssertSuccessfulEnvelope(JsonElement envelope)
    {
        Assert.Equal(ControllerManagementApiContract.Version, envelope.GetProperty("apiVersion").GetInt32());
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", envelope.GetProperty("code").GetString());
    }
}

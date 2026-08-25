using System.Net;
using System.Text.Json;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class AdmissionTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task GetRoot_WithoutApiKey_ReturnsUnauthorized()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var response = await client.GetAsync("/v1", TestContext.Current.CancellationToken);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task GetRoot_WithWrongStrongApiKey_ReturnsUnauthorized()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.ApiKeyHeaderName, new string('x', 32));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task GetRoot_WithDuplicateApiKeyHeaders_ReturnsUnauthorized()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.ApiKeyHeaderName, ControllerApiFixture.ApiKey);
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.ApiKeyHeaderName, ControllerApiFixture.ApiKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task GetRoot_WithWeakApiKey_ReturnsUnauthorized()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.ApiKeyHeaderName, new string('x', 31));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task UnknownPath_ReturnsNotFoundAndRootPostReturnsMethodNotAllowed()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using (var unknown = await client.GetAsync("/v1/nope", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using var document = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(cancellationToken));
            AssertErrorEnvelope(document.RootElement, "not_found");
        }

        using var post = new HttpRequestMessage(HttpMethod.Post, "/v1");
        using var methodResponse = await client.SendAsync(post, cancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, methodResponse.StatusCode);
        using var methodDocument = JsonDocument.Parse(await methodResponse.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(methodDocument.RootElement, "method_not_allowed");
    }

    [Fact]
    public async Task GetServiceRuntime_WithIfMatchHeader_ReturnsInvalidRequest()
    {
        using var client = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/services/runtime");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, "\"0\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertErrorEnvelope(document.RootElement, "invalid_request");
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertErrorEnvelope(document.RootElement, "unauthorized");
    }

    private static void AssertErrorEnvelope(JsonElement envelope, string code)
    {
        Assert.Equal(ControllerManagementApiContract.Version, envelope.GetProperty("apiVersion").GetInt32());
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(code, envelope.GetProperty("code").GetString());
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class RootApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task GetRoot_ReturnsConfiguredAggregateAndHidesReservedHostRoute()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(ControllerManagementApiContract.ETagHeaderName, out var values));
        var etag = Assert.Single(values);
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        Assert.True(long.TryParse(etag.Trim('\"'), out var version));
        Assert.True(version >= 2);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        Assert.Equal(ControllerManagementApiContract.Version, envelope.GetProperty("apiVersion").GetInt32());
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", envelope.GetProperty("code").GetString());
        Assert.Equal(version, envelope.GetProperty("version").GetInt64());

        var data = envelope.GetProperty("data");
        Assert.Equal(version, data.GetProperty("version").GetInt64());
        Assert.Equal(20000, data.GetProperty("globalSettings").GetProperty("autoPortRangeStart").GetInt32());
        Assert.Contains(data.GetProperty("extensions").EnumerateArray(), extension =>
            string.Equals(extension.GetProperty("extensionId").GetString(), ControllerApiFixture.TestExtensionId, StringComparison.Ordinal));
        Assert.DoesNotContain(data.GetProperty("routes").EnumerateArray(), route =>
        {
            var pattern = route.GetProperty("matcher").GetProperty("pattern").GetString();
            return pattern is not null && (string.Equals(pattern, ControllerApiFixture.HostRoutePrefix, StringComparison.Ordinal) ||
                pattern.StartsWith(ControllerApiFixture.HostRoutePrefix + "/", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task GetRoot_WithRequestBody_ReturnsInvalidRequest()
    {
        using var client = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1")
        {
            Content = new StringContent("{}", Encoding.UTF8, ControllerManagementApiContract.JsonMediaType)
        };
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }
}

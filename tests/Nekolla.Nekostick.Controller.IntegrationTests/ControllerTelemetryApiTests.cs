using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ControllerTelemetryApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    private static readonly bool IsPosixHost =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    [Fact]
    public async Task GetTelemetry_ReturnsRuntimeProcessAndHostMetrics()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        // The first sample leaves CPU percentages null; the second computes them from deltas.
        await client.GetAsync("/v1/controller/telemetry", cancellationToken);
        using var response = await client.GetAsync("/v1/controller/telemetry", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(ControllerManagementApiContract.ETagHeaderName));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("version").ValueKind);

        var data = envelope.GetProperty("data");
        Assert.True(data.GetProperty("timestampUnixMs").GetInt64() > 0);
        Assert.True(data.GetProperty("uptimeSeconds").GetDouble() >= 0);

        var runtime = data.GetProperty("runtime");
        Assert.True(runtime.GetProperty("managedHeapBytes").GetInt64() > 0);
        Assert.True(runtime.GetProperty("heapCommittedBytes").GetInt64() > 0);
        Assert.True(runtime.GetProperty("totalAllocatedBytes").GetInt64() > 0);
        Assert.True(runtime.GetProperty("gen0Collections").GetInt32() >= 0);
        Assert.True(runtime.GetProperty("gen1Collections").GetInt32() >= 0);
        Assert.True(runtime.GetProperty("gen2Collections").GetInt32() >= 0);
        Assert.True(runtime.GetProperty("pauseTimePercentage").GetDouble() >= 0);
        Assert.True(runtime.GetProperty("threadCount").GetInt32() > 0);
        var handleCount = runtime.GetProperty("handleCount");
        if (handleCount.ValueKind == JsonValueKind.Number)
        {
            Assert.True(handleCount.GetInt32() > 0);
        }

        var process = data.GetProperty("process");
        Assert.True(process.GetProperty("workingSetBytes").GetInt64() > 0);
        var privateMemory = process.GetProperty("privateMemoryBytes");
        if (privateMemory.ValueKind == JsonValueKind.Number)
        {
            Assert.True(privateMemory.GetInt64() > 0);
        }
        AssertCpuPercent(process.GetProperty("cpuPercent"));

        if (IsPosixHost)
        {
            var host = data.GetProperty("host");
            Assert.Equal(JsonValueKind.Object, host.ValueKind);
            var memoryTotal = host.GetProperty("memoryTotalBytes").GetInt64();
            var memoryUsed = host.GetProperty("memoryUsedBytes").GetInt64();
            Assert.True(memoryTotal > 0);
            Assert.InRange(memoryUsed, 0, memoryTotal);
            AssertCpuPercent(host.GetProperty("cpuPercent"));
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, data.GetProperty("host").ValueKind);
        }
    }

    [Fact]
    public async Task GetTelemetry_WithIfMatch_ReturnsInvalidRequest()
    {
        using var client = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/controller/telemetry");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, "\"0\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Telemetry_WithPost_ReturnsMethodNotAllowed()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.PostAsync("/v1/controller/telemetry", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static void AssertCpuPercent(JsonElement cpuPercent)
    {
        Assert.Equal(JsonValueKind.Number, cpuPercent.ValueKind);
        Assert.InRange(cpuPercent.GetDouble(), 0, 100);
    }

    private static void AssertSuccessfulEnvelope(JsonElement envelope)
    {
        Assert.Equal(ControllerManagementApiContract.Version, envelope.GetProperty("apiVersion").GetInt32());
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", envelope.GetProperty("code").GetString());
    }
}

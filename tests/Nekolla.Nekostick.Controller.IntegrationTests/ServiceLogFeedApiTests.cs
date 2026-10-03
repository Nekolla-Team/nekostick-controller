using System.Collections.Immutable;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ServiceLogFeedSseApiTests(ControllerApi14Fixture fixture) : IClassFixture<ControllerApi14Fixture>
{
    [Fact]
    public async Task HostRouteLogFeed_StreamsOutputLifecycleAndTerminationEntries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        var payload = Encoding.UTF8.GetBytes("log-feed-output");
        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            $"/v1/services/{serviceId}/output/stream",
            withApiKey: true,
            cancellationToken);
        Assert.Equal(200, response.StatusCode);
        await using var body = response.BodyStream;
        using var reader = new StreamReader(body, Encoding.UTF8);

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.Output,
            serviceId,
            21,
            data: payload,
            stream: ExtensionServiceOutputStream.Stdout));
        var outputFrame = await ReadSseFrameAsync(reader, cancellationToken);
        Assert.Equal($"id:21\ndata:{Convert.ToBase64String(payload)}\n\n", outputFrame);

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.GenerationStarted,
            serviceId,
            22));
        var generationFrame = await ReadSseFrameAsync(reader, cancellationToken);
        Assert.StartsWith("id:22\nevent:state\ndata:", generationFrame);
        var generation = ParseEntry(generationFrame);
        Assert.Equal("generationStarted", generation.GetProperty("kind").GetString());

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.ProcessExited,
            serviceId,
            23,
            processExitCode: 7));
        var exitFrame = await ReadSseFrameAsync(reader, cancellationToken);
        Assert.StartsWith("id:23\nevent:state\ndata:", exitFrame);
        var processExited = ParseEntry(exitFrame);
        Assert.Equal("processExited", processExited.GetProperty("kind").GetString());
        Assert.Equal(7, processExited.GetProperty("processExitCode").GetInt32());

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.Termination,
            serviceId,
            24,
            terminationReason: ExtensionServiceLogTerminationReason.ServiceRemoved));
        var terminationFrame = await ReadSseFrameAsync(reader, cancellationToken);
        Assert.StartsWith("id:24\nevent:state\ndata:", terminationFrame);
        Assert.Equal("termination", ParseEntry(terminationFrame).GetProperty("kind").GetString());
        Assert.Equal(
            "event:end\ndata:{\"reason\":\"serviceRemoved\"}\n\n",
            await ReadSseFrameAsync(reader, cancellationToken));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("37", 37L)]
    public async Task HostRouteLogFeed_PassesNonnegativeSinceCursor(string cursor, long expected)
    {
        var serviceId = Guid.CreateVersion7();
        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            $"/v1/services/{serviceId}/output/stream?since={cursor}",
            withApiKey: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(200, response.StatusCode);
        var request = Assert.Single(fixture.Host.ServiceOutputFake.Subscriptions, entry => entry.ServiceId == serviceId);
        Assert.Equal(expected, request.SinceSequence);
        await response.BodyStream.DisposeAsync();
    }

    [Fact]
    public async Task HostRouteLogFeed_UsesLastEventIdWhenSinceQueryIsAbsent()
    {
        var serviceId = Guid.CreateVersion7();
        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            $"/v1/services/{serviceId}/output/stream",
            withApiKey: true,
            TestContext.Current.CancellationToken,
            new[] { new KeyValuePair<string, string>("Last-Event-ID", "81") });

        Assert.Equal(200, response.StatusCode);
        var request = Assert.Single(fixture.Host.ServiceOutputFake.Subscriptions, entry => entry.ServiceId == serviceId);
        Assert.Equal(81, request.SinceSequence);
        await response.BodyStream.DisposeAsync();
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task HostRouteLogFeed_RejectsMalformedOrNegativeSinceCursor(string cursor)
    {
        var serviceId = Guid.CreateVersion7();
        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            $"/v1/services/{serviceId}/output/stream?since={cursor}",
            withApiKey: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(400, response.StatusCode);
        Assert.DoesNotContain(fixture.Host.ServiceOutputFake.Subscriptions, entry => entry.ServiceId == serviceId);
        await response.BodyStream.DisposeAsync();
    }

    [Fact]
    public async Task HostRouteLogFeed_UnsupportedFallsBackToLegacySseFraming()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        var payload = Encoding.UTF8.GetBytes("legacy-output");
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(payload)));

        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            $"/v1/services/{serviceId}/output/stream",
            withApiKey: true,
            cancellationToken);
        Assert.Equal(200, response.StatusCode);
        await using var body = response.BodyStream;
        var contents = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync(cancellationToken);

        Assert.Contains($"data:{Convert.ToBase64String(payload)}\n\n", contents, StringComparison.Ordinal);
        Assert.Contains("event:end\ndata:{\"reason\":\"processExited\"}\n\n", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("id:", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("event:state", contents, StringComparison.Ordinal);
        Assert.Contains(fixture.Host.ServiceOutputFake.Subscriptions, entry => entry.ServiceId == serviceId);
        Assert.Contains(fixture.Host.ServiceOutputFake.Opens, entry => entry.ServiceId == serviceId);
    }

    [Fact]
    public async Task HostRouteLogFeed_UnsupportedFallbackReturnsNotRunningConflictWhenLegacyStreamCannotOpen()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(false, ExtensionServiceOutputCode.NotRunning, id, null));

        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            $"/v1/services/{serviceId}/output/stream",
            withApiKey: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(409, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            response.BodyStream,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("not_running", document.RootElement.GetProperty("code").GetString());
        await response.BodyStream.DisposeAsync();
    }

    [Fact]
    public async Task HostRouteLogFeed_StreamsWhenLegacyServiceIsNotRunning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        var payload = Encoding.UTF8.GetBytes("stopped-service-feed");
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(false, ExtensionServiceOutputCode.NotRunning, id, null));

        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            $"/v1/services/{serviceId}/output/stream",
            withApiKey: true,
            cancellationToken);
        Assert.Equal(200, response.StatusCode);
        await using var body = response.BodyStream;
        using var reader = new StreamReader(body, Encoding.UTF8);

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.Output,
            serviceId,
            91,
            data: payload,
            stream: ExtensionServiceOutputStream.Stdout));
        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.Termination,
            serviceId,
            92,
            terminationReason: ExtensionServiceLogTerminationReason.ServiceDisabled));
        var contents = await reader.ReadToEndAsync(cancellationToken);

        Assert.Contains($"id:91\ndata:{Convert.ToBase64String(payload)}\n\n", contents, StringComparison.Ordinal);
        Assert.Contains("event:end\ndata:{\"reason\":\"serviceDisabled\"}\n\n", contents, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.ServiceOutputFake.Opens, entry => entry.ServiceId == serviceId);
    }

    private static ExtensionServiceLogEntry CreateEntry(
        ExtensionServiceLogEntryKind kind,
        Guid serviceId,
        long? sequence,
        byte[]? data = null,
        ExtensionServiceOutputStream? stream = null,
        int? processExitCode = null,
        ExtensionServiceLogTerminationReason? terminationReason = null) =>
        new(
            kind,
            serviceId,
            sequence,
            new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            stream,
            data is null ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(data),
            serviceId,
            1,
            processExitCode,
            null,
            ExtensionServiceFailureStage.None,
            ExtensionServiceFailureCode.None,
            string.Empty,
            null,
            null,
            terminationReason);

    private static async Task<string> ReadSseFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new EndOfStreamException("The service-log stream ended before the expected frame.");
            }

            if (line.Length == 0)
            {
                return text.Append('\n').ToString();
            }

            text.Append(line).Append('\n');
        }
    }

    private static JsonElement ParseEntry(string frame)
    {
        var dataLine = frame.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("data:", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(dataLine[5..]);
        return document.RootElement.Clone();
    }
}

public sealed class ServiceLogFeedWebSocketApiTests(ControllerApi14Fixture fixture) : IClassFixture<ControllerApi14Fixture>
{
    [Fact]
    public async Task KestrelLogFeed_SendsBinaryOutputStateTextAndMappedTerminationClose()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        var payload = Encoding.UTF8.GetBytes("websocket-feed-output");
        using var socket = await ConnectAsync(fixture, serviceId, cancellationToken);

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.Output,
            serviceId,
            31,
            data: payload,
            stream: ExtensionServiceOutputStream.Stdout));
        var output = await ReceiveFrameAsync(socket, cancellationToken);
        Assert.Equal(WebSocketMessageType.Binary, output.Type);
        Assert.Equal(payload, output.Payload);

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.GenerationStarted,
            serviceId,
            32));
        var generation = await ReceiveFrameAsync(socket, cancellationToken);
        Assert.Equal(WebSocketMessageType.Text, generation.Type);
        using (var generationDocument = JsonDocument.Parse(Encoding.UTF8.GetString(generation.Payload)))
        {
            Assert.Equal("generationStarted", generationDocument.RootElement.GetProperty("kind").GetString());
            Assert.Equal(32, generationDocument.RootElement.GetProperty("sequence").GetInt64());
        }

        fixture.Host.ServiceOutputFake.Push(CreateEntry(
            ExtensionServiceLogEntryKind.Termination,
            serviceId,
            33,
            terminationReason: ExtensionServiceLogTerminationReason.ServiceRemoved));
        var termination = await ReceiveFrameAsync(socket, cancellationToken);
        Assert.Equal(WebSocketMessageType.Text, termination.Type);
        using (var terminationDocument = JsonDocument.Parse(Encoding.UTF8.GetString(termination.Payload)))
        {
            Assert.Equal("termination", terminationDocument.RootElement.GetProperty("kind").GetString());
            Assert.Equal("serviceRemoved", terminationDocument.RootElement.GetProperty("terminationReason").GetString());
        }

        var close = await ReceiveFrameAsync(socket, cancellationToken);
        Assert.Equal(WebSocketMessageType.Close, close.Type);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
        Assert.Equal("serviceRemoved", close.CloseDescription);
    }

    [Fact]
    public async Task KestrelLogFeed_UnsupportedFallsBackToLegacyBinaryOnlyOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        var payload = Encoding.UTF8.GetBytes("legacy-websocket-output");
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(payload)));

        using var socket = await ConnectAsync(fixture, serviceId, cancellationToken);
        using var received = new MemoryStream();
        while (true)
        {
            var frame = await ReceiveFrameAsync(socket, cancellationToken);
            if (frame.Type == WebSocketMessageType.Close)
            {
                Assert.Equal(WebSocketCloseStatus.NormalClosure, frame.CloseStatus);
                break;
            }

            Assert.Equal(WebSocketMessageType.Binary, frame.Type);
            received.Write(frame.Payload);
        }

        Assert.Equal(payload, received.ToArray());
        Assert.Contains(fixture.Host.ServiceOutputFake.Subscriptions, entry => entry.ServiceId == serviceId);
        Assert.Contains(fixture.Host.ServiceOutputFake.Opens, entry => entry.ServiceId == serviceId);
    }

    private static async Task<ClientWebSocket> ConnectAsync(
        ControllerApi14Fixture fixture,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ControllerManagementApiContract.ApiKeyHeaderName, ControllerApiFixture.ApiKey);
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{fixture.HttpPort}/v1/services/{serviceId}/output/stream"),
            cancellationToken);
        return socket;
    }

    private static async Task<WebSocketFrame> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var payload = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new WebSocketFrame(
                    result.MessageType,
                    Array.Empty<byte>(),
                    result.CloseStatus,
                    result.CloseStatusDescription);
            }

            payload.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return new WebSocketFrame(result.MessageType, payload.ToArray(), null, null);
    }

    private static ExtensionServiceLogEntry CreateEntry(
        ExtensionServiceLogEntryKind kind,
        Guid serviceId,
        long? sequence,
        byte[]? data = null,
        ExtensionServiceOutputStream? stream = null,
        int? processExitCode = null,
        ExtensionServiceLogTerminationReason? terminationReason = null) =>
        new(
            kind,
            serviceId,
            sequence,
            new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            stream,
            data is null ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange(data),
            serviceId,
            1,
            processExitCode,
            null,
            ExtensionServiceFailureStage.None,
            ExtensionServiceFailureCode.None,
            string.Empty,
            null,
            null,
            terminationReason);

    private readonly record struct WebSocketFrame(
        WebSocketMessageType Type,
        byte[] Payload,
        WebSocketCloseStatus? CloseStatus,
        string? CloseDescription);
}

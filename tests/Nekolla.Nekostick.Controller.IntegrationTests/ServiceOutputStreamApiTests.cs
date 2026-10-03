using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

/// <summary>
/// Exercises the service-output WebSocket endpoint against a host API 1.4 bridge. Connecting
/// subscribes to the Host service-output stream; disconnecting disposes it.
/// </summary>
public sealed class ServiceOutputStreamApi14Tests(ControllerApi14Fixture fixture) : IClassFixture<ControllerApi14Fixture>
{
    [Fact]
    public async Task OutputStream_DeliversBinaryFramesAndClosesNormallyOnProcessExit()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        var payload = Encoding.UTF8.GetBytes("hello\nworld\n");
        fixture.Host.ServiceOutputFake.OnOpen((id, stream) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(payload)));

        using var socket = await ConnectAsync(fixture.HttpPort, serviceId, TestContext.Current.CancellationToken);
        var received = await ReceiveUntilCloseAsync(socket, TestContext.Current.CancellationToken);

        Assert.Equal(payload, received);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        var open = Assert.Single(fixture.Host.ServiceOutputFake.Opens, entry => entry.ServiceId == serviceId);
        Assert.Equal(serviceId, open.ServiceId);
        Assert.Equal(ExtensionServiceOutputStream.Stdout, open.Stream);
    }

    [Fact]
    public async Task OutputStream_StderrQuerySelectsStderr()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, stream) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(Array.Empty<byte>())));

        using var socket = await ConnectAsync(fixture.HttpPort, serviceId, TestContext.Current.CancellationToken, "?stream=stderr");
        await ReceiveUntilCloseAsync(socket, TestContext.Current.CancellationToken);

        var open = Assert.Single(fixture.Host.ServiceOutputFake.Opens, entry => entry.ServiceId == serviceId);
        Assert.Equal(ExtensionServiceOutputStream.Stderr, open.Stream);
    }

    [Fact]
    public async Task OutputStream_AuthenticatesViaKeySubProtocol()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        var payload = Encoding.UTF8.GetBytes("via-subprotocol\n");
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(payload)));

        using var socket = await ConnectAsync(
            fixture.HttpPort,
            serviceId,
            TestContext.Current.CancellationToken,
            useSubProtocolKey: true);
        var received = await ReceiveUntilCloseAsync(socket, TestContext.Current.CancellationToken);

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task OutputStream_HeaderKeyWinsOverSubProtocol()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        var payload = Encoding.UTF8.GetBytes("header-wins\n");
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(payload)));

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ControllerManagementApiContract.ApiKeyHeaderName, ControllerApiFixture.ApiKey);
        socket.Options.AddSubProtocol(KeySubProtocol("garbage-garbage-garbage-garbage-0"));
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{fixture.HttpPort}/v1/services/{serviceId}/output/stream"),
            TestContext.Current.CancellationToken);
        var received = await ReceiveUntilCloseAsync(socket, TestContext.Current.CancellationToken);

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task OutputStream_WrongKeySubProtocolRejected()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var request = CreateUpgradeRequest($"/v1/services/{Guid.CreateVersion7()}/output/stream", key: null);
        request.Headers.TryAddWithoutValidation(
            "Sec-WebSocket-Protocol",
            KeySubProtocol("wrong-key-wrong-key-wrong-key-00"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OutputStream_InvalidStreamQueryRejected()
    {
        using var client = fixture.CreateHttpClient();
        using var request = CreateUpgradeRequest($"/v1/services/{Guid.CreateVersion7()}/output/stream?stream=foo", ControllerApiFixture.ApiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OutputStream_NonWebSocketGetRejected()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync(
            $"/v1/services/{Guid.CreateVersion7()}/output/stream",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OutputStream_RequiresApiKey()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var request = CreateUpgradeRequest($"/v1/services/{Guid.CreateVersion7()}/output/stream", key: null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OutputStream_OverlappingPathRejectedBeforeAuth()
    {
        // "/v1/services/output/stream" overlaps the endpoint prefix/suffix; it must not throw.
        using var client = fixture.CreateHttpClient();
        using var request = CreateUpgradeRequest("/v1/services/output/stream", ControllerApiFixture.ApiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OutputStream_UnknownServiceReturnsNotFound()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(false, ExtensionServiceOutputCode.NotFound, id, null));

        using var client = fixture.CreateHttpClient();
        using var request = CreateUpgradeRequest($"/v1/services/{serviceId}/output/stream", ControllerApiFixture.ApiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OutputStream_NotRunningReturnsConflict()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(false, ExtensionServiceOutputCode.NotRunning, id, null));

        using var client = fixture.CreateHttpClient();
        using var request = CreateUpgradeRequest($"/v1/services/{serviceId}/output/stream", ControllerApiFixture.ApiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("not_running", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OutputStream_ClientCloseDisposesHostStream()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        var output = new BlockingOutputStream();
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, output));

        using var socket = await ConnectAsync(fixture.HttpPort, serviceId, TestContext.Current.CancellationToken);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);

        Assert.True(output.Disposed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "The Host output stream must be disposed once the WebSocket closes.");
    }

    private static async Task<ClientWebSocket> ConnectAsync(
        int port,
        Guid serviceId,
        CancellationToken cancellationToken,
        string? query = null,
        bool useSubProtocolKey = false)
    {
        var socket = new ClientWebSocket();
        if (useSubProtocolKey)
        {
            // Browsers cannot set upgrade headers, so the Web UI presents the key as a subprotocol.
            socket.Options.AddSubProtocol(KeySubProtocol(ControllerApiFixture.ApiKey));
        }
        else
        {
            socket.Options.SetRequestHeader(ControllerManagementApiContract.ApiKeyHeaderName, ControllerApiFixture.ApiKey);
        }

        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}/v1/services/{serviceId}/output/stream{query}"),
            cancellationToken);
        return socket;
    }

    private static string KeySubProtocol(string key) =>
        ControllerManagementApiContract.ServiceOutputKeySubProtocolPrefix +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static HttpRequestMessage CreateUpgradeRequest(string path, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Connection.Add("Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.ApiKeyHeaderName, key);
        }

        return request;
    }

    private static async Task<byte[]> ReceiveUntilCloseAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return received.ToArray();
            }

            Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
            received.Write(buffer, 0, result.Count);
        }
    }

    [Fact]
    public async Task HostRoute_OutputStream_SseDeliversBase64ChunksAndEndEventOnProcessExit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        var payload = Encoding.UTF8.GetBytes("hello\nworld\n");
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(payload)));

        var response = await InvokeHostRouteStreamAsync(
            $"/v1/services/{serviceId}/output/stream?stream=stdout", withApiKey: true, cancellationToken);
        Assert.Equal(200, response.StatusCode);
        var contentType = Assert.Single(response.Headers
            .Where(pair => string.Equals(pair.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value));
        Assert.StartsWith(ControllerManagementApiContract.ServiceOutputEventStreamMediaType, contentType);

        await using (response.BodyStream.ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var body = await new StreamReader(response.BodyStream).ReadToEndAsync(timeout.Token);
            Assert.Contains($"data:{Convert.ToBase64String(payload)}\n\n", body);
            Assert.Contains("event:end\ndata:{\"reason\":\"processExited\"}\n\n", body);
        }

        var open = Assert.Single(fixture.Host.ServiceOutputFake.Opens, entry => entry.ServiceId == serviceId);
        Assert.Equal(ExtensionServiceOutputStream.Stdout, open.Stream);
    }

    [Fact]
    public async Task HostRoute_OutputStream_SseStderrQuerySelectsStderr()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, new MemoryStream(Array.Empty<byte>())));

        var response = await InvokeHostRouteStreamAsync(
            $"/v1/services/{serviceId}/output/stream?stream=stderr", withApiKey: true, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);
        await response.BodyStream.DisposeAsync();

        var open = Assert.Single(fixture.Host.ServiceOutputFake.Opens, entry => entry.ServiceId == serviceId);
        Assert.Equal(ExtensionServiceOutputStream.Stderr, open.Stream);
    }

    [Fact]
    public async Task HostRoute_OutputStream_RequiresApiKey()
    {
        var response = await InvokeHostRouteStreamAsync(
            $"/v1/services/{Guid.CreateVersion7()}/output/stream", withApiKey: false, TestContext.Current.CancellationToken);
        Assert.Equal(401, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(response.BodyStream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("unauthorized", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task HostRoute_OutputStream_UnknownServiceReturnsNotFound()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(false, ExtensionServiceOutputCode.NotFound, id, null));

        var response = await InvokeHostRouteStreamAsync(
            $"/v1/services/{serviceId}/output/stream", withApiKey: true, TestContext.Current.CancellationToken);
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HostRoute_OutputStream_NotRunningReturnsConflict()
    {
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(false, ExtensionServiceOutputCode.NotRunning, id, null));

        var response = await InvokeHostRouteStreamAsync(
            $"/v1/services/{serviceId}/output/stream", withApiKey: true, TestContext.Current.CancellationToken);
        Assert.Equal(409, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(response.BodyStream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("not_running", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task HostRoute_OutputStream_InvalidStreamQueryRejected()
    {
        var response = await InvokeHostRouteStreamAsync(
            $"/v1/services/{Guid.CreateVersion7()}/output/stream?stream=bogus", withApiKey: true, TestContext.Current.CancellationToken);
        Assert.Equal(400, response.StatusCode);
    }

    private async Task<ExtensionStreamingResponse> InvokeHostRouteStreamAsync(
        string logicalPathAndQuery,
        bool withApiKey,
        CancellationToken cancellationToken)
    {
        var handler = fixture.Registration.StreamingHandler
            ?? throw new InvalidOperationException("The streaming HostRoute handler is not registered.");
        var headers = withApiKey
            ? new[]
            {
                new KeyValuePair<string, IEnumerable<string>>(
                    ControllerManagementApiContract.ApiKeyHeaderName,
                    new[] { ControllerApiFixture.ApiKey })
            }
            : Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>();
        return await handler.HandleStreamingAsync(
            new ExtensionStreamingRequest(
                "GET",
                ControllerApiFixture.HostRoutePrefix + logicalPathAndQuery,
                headers,
                Stream.Null),
            cancellationToken);
    }

}

/// <summary>Host API 1.3.x has no service-output capability; the endpoint must answer 501.</summary>
public sealed class ServiceOutputStreamApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task OutputStream_UnsupportedBeforeHostApi14()
    {
        using var client = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/services/{Guid.CreateVersion7()}/output/stream");
        request.Headers.Connection.Add("Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.ApiKeyHeaderName, ControllerApiFixture.ApiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }
}

/// <summary>A live output session must survive a key-preserving reload and end on key rotation.</summary>
public sealed class ServiceOutputStreamSessionTests(ControllerApi14Fixture fixture) : IClassFixture<ControllerApi14Fixture>
{
    private const string RotatedApiKey = "integration-test-key-rotated-9876543210";

    [Fact]
    public async Task OutputStream_ReloadKeepsSessionAndKeyRotationEndsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        fixture.Host.ServiceOutputFake.Reject(serviceId, ExtensionServiceLogCode.Unsupported);
        var output = new BlockingOutputStream();
        fixture.Host.ServiceOutputFake.OnOpen((id, _) =>
            new ExtensionServiceOutputStreamResult(true, ExtensionServiceOutputCode.Opened, id, output));

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ControllerManagementApiContract.ApiKeyHeaderName, ControllerApiFixture.ApiKey);
        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{fixture.HttpPort}/v1/services/{serviceId}/output/stream"),
            cancellationToken);

        // A reload that keeps the key must not disturb the live session.
        await ReloadSettingsAsync(cancellationToken);
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.False(output.Disposed.IsSet);

        // Rotating the key revokes the session: the server closes the socket and disposes the stream.
        RotateSettingsApiKey();
        await ReloadSettingsAsync(cancellationToken);

        var buffer = new byte[256];
        var received = await socket.ReceiveAsync(buffer, cancellationToken);
        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.True(
            output.Disposed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "The Host output stream must be disposed once the admitting session ends.");
    }

    private async Task ReloadSettingsAsync(CancellationToken cancellationToken)
    {
        using var client = fixture.CreateHttpClient();
        using var root = await client.GetAsync("/v1", cancellationToken);
        var etag = Assert.Single(root.Headers.GetValues(ControllerManagementApiContract.ETagHeaderName));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/controller/reload-settings");
        request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, etag);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private void RotateSettingsApiKey()
    {
        var snapshot = fixture.Host.ReadSnapshot();
        var settings = snapshot.ExtensionSettings.First(
            entry => string.Equals(entry.ExtensionId, ControllerOptions.ExtensionId, StringComparison.Ordinal));
        var document = JsonNode.Parse(settings.SettingsJson)!.AsObject();
        document["apiKey"] = RotatedApiKey;
        var replacement = new ExtensionSettingsConfiguration(
            settings.ExtensionId,
            ControllerOptions.ConfigurationSchemaVersion,
            document.ToJsonString(),
            settings.Version);
        var write = fixture.Host.ReplaceSnapshot(snapshot.Version, new ConfigurationChangeSet(
            snapshot.GlobalSettings,
            snapshot.Routes,
            snapshot.Services,
            snapshot.ExtensionRecords,
            snapshot.ExtensionSettings.Replace(settings, replacement)));
        Assert.True(write.IsSuccess);
    }
}

    /// <summary>A readable stream that never yields bytes, honors cancellation, and tracks disposal.</summary>
internal sealed class BlockingOutputStream : Stream
    {
        public ManualResetEventSlim Disposed { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed.Set();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Disposed.Set();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

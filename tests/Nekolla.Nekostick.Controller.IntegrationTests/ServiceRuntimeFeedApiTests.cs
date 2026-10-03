using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ServiceRuntimeFeedApiTests(ControllerApi14Fixture fixture) : IClassFixture<ControllerApi14Fixture>
{
    [Fact]
    public async Task KestrelRuntimeFeed_ReplaysInitialSnapshotThenStreamsLiveUpsertsAndRemoval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        const string ownerExtensionId = "nekolla.nekostick.runtime-feed-test";
        fixture.Host.ServiceRuntimeStateFake.Push(CreateChange(
            1,
            serviceId,
            ExtensionServiceRuntimeStateChangeKind.Snapshot,
            CreateSnapshot(serviceId, ExtensionServiceLifecycleState.Running, ownerExtensionId),
            ownerExtensionId,
            isInitialSnapshot: true));

        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync(
            "/v1/services/runtime/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);

        var initial = await ReadFrameForServiceAsync(reader, serviceId, cancellationToken);
        Assert.Equal(1, initial.Sequence);
        Assert.Equal("snapshot", initial.Entry.GetProperty("kind").GetString());
        Assert.True(initial.Entry.GetProperty("isInitialSnapshot").GetBoolean());
        Assert.Equal(ownerExtensionId, initial.Entry.GetProperty("ownerExtensionId").GetString());
        Assert.Equal(serviceId, initial.Entry.GetProperty("snapshot").GetProperty("serviceId").GetGuid());
        Assert.StartsWith("id:1\nevent:state\ndata:", initial.Text);

        fixture.Host.ServiceRuntimeStateFake.Push(CreateChange(
            2,
            serviceId,
            ExtensionServiceRuntimeStateChangeKind.Snapshot,
            CreateSnapshot(serviceId, ExtensionServiceLifecycleState.Stopped, ownerExtensionId),
            ownerExtensionId,
            isInitialSnapshot: false));
        var upsert = await ReadFrameForServiceAsync(reader, serviceId, cancellationToken);
        Assert.Equal(2, upsert.Sequence);
        Assert.Equal("snapshot", upsert.Entry.GetProperty("kind").GetString());
        Assert.False(upsert.Entry.GetProperty("isInitialSnapshot").GetBoolean());

        fixture.Host.ServiceRuntimeStateFake.Push(CreateChange(
            3,
            serviceId,
            ExtensionServiceRuntimeStateChangeKind.Removed,
            snapshot: null,
            ownerExtensionId,
            isInitialSnapshot: false));
        var removed = await ReadFrameForServiceAsync(reader, serviceId, cancellationToken);
        Assert.Equal(3, removed.Sequence);
        Assert.Equal("removed", removed.Entry.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, removed.Entry.GetProperty("snapshot").ValueKind);
    }

    [Fact]
    public async Task HostRouteRuntimeFeed_ReplaysInitialSnapshotThenStreamsLiveUpsertsAndRemoval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = Guid.CreateVersion7();
        const string ownerExtensionId = "nekolla.nekostick.runtime-feed-test";
        fixture.Host.ServiceRuntimeStateFake.Push(CreateChange(
            11,
            serviceId,
            ExtensionServiceRuntimeStateChangeKind.Snapshot,
            CreateSnapshot(serviceId, ExtensionServiceLifecycleState.Running, ownerExtensionId),
            ownerExtensionId,
            isInitialSnapshot: true));

        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            "/v1/services/runtime/stream",
            withApiKey: true,
            cancellationToken);
        Assert.Equal(200, response.StatusCode);
        using var reader = new StreamReader(response.BodyStream, Encoding.UTF8);

        var initial = await ReadFrameForServiceAsync(reader, serviceId, cancellationToken);
        Assert.Equal(11, initial.Sequence);
        Assert.Equal("snapshot", initial.Entry.GetProperty("kind").GetString());
        Assert.True(initial.Entry.GetProperty("isInitialSnapshot").GetBoolean());
        Assert.Equal(ownerExtensionId, initial.Entry.GetProperty("ownerExtensionId").GetString());
        Assert.Equal(serviceId, initial.Entry.GetProperty("snapshot").GetProperty("serviceId").GetGuid());

        fixture.Host.ServiceRuntimeStateFake.Push(CreateChange(
            12,
            serviceId,
            ExtensionServiceRuntimeStateChangeKind.Snapshot,
            CreateSnapshot(serviceId, ExtensionServiceLifecycleState.Stopped, ownerExtensionId),
            ownerExtensionId,
            isInitialSnapshot: false));
        var upsert = await ReadFrameForServiceAsync(reader, serviceId, cancellationToken);
        Assert.Equal(12, upsert.Sequence);
        Assert.Equal("snapshot", upsert.Entry.GetProperty("kind").GetString());
        Assert.False(upsert.Entry.GetProperty("isInitialSnapshot").GetBoolean());

        fixture.Host.ServiceRuntimeStateFake.Push(CreateChange(
            13,
            serviceId,
            ExtensionServiceRuntimeStateChangeKind.Removed,
            snapshot: null,
            ownerExtensionId,
            isInitialSnapshot: false));
        var removed = await ReadFrameForServiceAsync(reader, serviceId, cancellationToken);
        Assert.Equal(13, removed.Sequence);
        Assert.Equal("removed", removed.Entry.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, removed.Entry.GetProperty("snapshot").ValueKind);
        await response.BodyStream.DisposeAsync();
    }

    [Fact]
    public async Task KestrelRuntimeFeed_RequiresApiKey()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);
        using var response = await client.GetAsync(
            "/v1/services/runtime/stream",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HostRouteRuntimeFeed_RequiresApiKey()
    {
        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            "/v1/services/runtime/stream",
            withApiKey: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(401, response.StatusCode);
        await response.BodyStream.DisposeAsync();
    }

    private static ExtensionServiceRuntimeStateChange CreateChange(
        long sequence,
        Guid serviceId,
        ExtensionServiceRuntimeStateChangeKind kind,
        ExtensionServiceRuntimeSnapshot? snapshot,
        string? ownerExtensionId,
        bool isInitialSnapshot) =>
        new(serviceId, sequence, kind, snapshot, isInitialSnapshot, ownerExtensionId);

    private static ExtensionServiceRuntimeSnapshot CreateSnapshot(
        Guid serviceId,
        ExtensionServiceLifecycleState lifecycleState,
        string? ownerExtensionId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExtensionServiceRuntimeSnapshot(
            serviceId,
            1234,
            now,
            TimeSpan.Zero,
            lifecycleState,
            ExtensionServiceHealthState.Healthy,
            0,
            0,
            now,
            now,
            ownerExtensionId);
    }

    private static async Task<RuntimeFeedFrame> ReadFrameForServiceAsync(
        StreamReader reader,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var text = await ReadSseFrameAsync(reader, cancellationToken);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var dataLine = lines.FirstOrDefault(static line => line.StartsWith("data:", StringComparison.Ordinal));
            var sequenceLine = lines.FirstOrDefault(static line => line.StartsWith("id:", StringComparison.Ordinal));
            if (dataLine is null || sequenceLine is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(dataLine[5..]);
            var entry = document.RootElement;
            if (entry.GetProperty("serviceId").GetGuid() != serviceId)
            {
                continue;
            }

            var sequence = long.Parse(sequenceLine[3..], CultureInfo.InvariantCulture);
            return new RuntimeFeedFrame(sequence, entry.Clone(), text);
        }
    }

    private static async Task<string> ReadSseFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new EndOfStreamException("The runtime-state stream ended before the expected frame.");
            }

            if (line.Length == 0)
            {
                return text.Append('\n').ToString();
            }

            text.Append(line).Append('\n');
        }
    }

    private readonly record struct RuntimeFeedFrame(long Sequence, JsonElement Entry, string Text);
}

public sealed class ServiceRuntimeFeedThrowingHostTests(ControllerApi14RuntimeFeedThrowFixture fixture)
    : IClassFixture<ControllerApi14RuntimeFeedThrowFixture>
{
    [Fact]
    public async Task KestrelRuntimeFeed_ReturnsNotImplementedWhenHostSubscribeThrowsNotSupported()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync(
            "/v1/services/runtime/stream",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task HostRouteRuntimeFeed_ReturnsNotImplementedWhenHostSubscribeThrowsNotSupported()
    {
        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            "/v1/services/runtime/stream",
            withApiKey: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(501, response.StatusCode);
        await response.BodyStream.DisposeAsync();
    }
}

public sealed class ServiceRuntimeFeedRejectingHostTests(ControllerApi14RuntimeFeedRejectFixture fixture)
    : IClassFixture<ControllerApi14RuntimeFeedRejectFixture>
{
    [Fact]
    public async Task KestrelRuntimeFeed_ReturnsNotImplementedWhenHostRejectsUnsupported()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync(
            "/v1/services/runtime/stream",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task HostRouteRuntimeFeed_ReturnsNotImplementedWhenHostRejectsUnsupported()
    {
        var response = await HostRouteStreamingTestHelpers.InvokeAsync(
            fixture,
            "/v1/services/runtime/stream",
            withApiKey: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(501, response.StatusCode);
        await response.BodyStream.DisposeAsync();
    }
}

public sealed class ControllerApi14RuntimeFeedThrowFixture : ControllerApiFixture
{
    public ControllerApi14RuntimeFeedThrowFixture()
        : base(new HostApiVersion(1, 4, 0))
    {
    }

    protected override void ConfigureHost(FakeHostBridge host) =>
        host.ServiceRuntimeStateFake.ThrowOnSubscribe(new NotSupportedException());
}

public sealed class ControllerApi14RuntimeFeedRejectFixture : ControllerApiFixture
{
    public ControllerApi14RuntimeFeedRejectFixture()
        : base(new HostApiVersion(1, 4, 0))
    {
    }

    protected override void ConfigureHost(FakeHostBridge host) =>
        host.ServiceRuntimeStateFake.Reject(ExtensionServiceRuntimeStateSubscriptionCode.Unsupported);
}

internal static class HostRouteStreamingTestHelpers
{
    public static async Task<ExtensionStreamingResponse> InvokeAsync(
        ControllerApiFixture fixture,
        string logicalPathAndQuery,
        bool withApiKey,
        CancellationToken cancellationToken,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null)
    {
        var handler = fixture.Registration.StreamingHandler
            ?? throw new InvalidOperationException("The streaming HostRoute handler is not registered.");
        var headers = new List<KeyValuePair<string, IEnumerable<string>>>();
        if (withApiKey)
        {
            headers.Add(new KeyValuePair<string, IEnumerable<string>>(
                ControllerManagementApiContract.ApiKeyHeaderName,
                new[] { ControllerApiFixture.ApiKey }));
        }

        if (extraHeaders is not null)
        {
            headers.AddRange(extraHeaders.Select(static pair =>
                new KeyValuePair<string, IEnumerable<string>>(pair.Key, new[] { pair.Value })));
        }

        return await handler.HandleStreamingAsync(
            new ExtensionStreamingRequest(
                "GET",
                ControllerApiFixture.HostRoutePrefix + logicalPathAndQuery,
                headers,
                Stream.Null),
            cancellationToken);
    }
}

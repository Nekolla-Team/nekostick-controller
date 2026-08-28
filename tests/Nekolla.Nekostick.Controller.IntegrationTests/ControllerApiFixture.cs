using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using Grpc.Net.Client;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Grpc;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

/// <summary>
/// Boots the real <see cref="ControllerEntrypoint"/> against an in-memory fake Host bridge with
/// all four management transports listening on isolated loopback endpoints.
/// </summary>
public sealed class ControllerApiFixture : IAsyncLifetime
{
    public const string ApiKey = "integration-test-key-0123456789abcdef";
    public const string HostRoutePrefix = "/it-controller";
    public const string CorsOrigin = "http://localhost:5173";
    public const string TestExtensionId = "nekolla.nekostick.test-extension";
    public const string SpareExtensionId = "nekolla.nekostick.spare-extension";

    private readonly ConcurrentBag<GrpcChannel> _grpcChannels = new();
    private ControllerEntrypoint? _entrypoint;
    private FakeHostBridge? _host;
    private string _socketDirectory = string.Empty;

    public FakeHostBridge Host => _host ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public FakeExtensionRegistration Registration { get; } = new();

    public int HttpPort { get; private set; }

    public int GrpcPort { get; private set; }

    public string SocketPath { get; private set; } = string.Empty;

    static ControllerApiFixture() =>
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

    public async ValueTask InitializeAsync()
    {
        HttpPort = ReserveDualStackLoopbackPort();
        GrpcPort = ReserveDualStackLoopbackPort(HttpPort);
        _socketDirectory = CreateTrustedSocketDirectory();
        SocketPath = Path.Combine(_socketDirectory, "ctl.sock");

        var settings = new ExtensionSettingsConfiguration(
            ControllerOptions.ExtensionId,
            ControllerOptions.ConfigurationSchemaVersion,
            JsonSerializer.Serialize(new
            {
                loopbackOnly = true,
                enableHttpJson = true,
                enableUnixSocket = true,
                enableGrpc = true,
                enableHostRoute = true,
                httpPort = HttpPort,
                grpcPort = GrpcPort,
                unixSocketPath = SocketPath,
                unixSocketMode = ControllerOptions.RequiredUnixSocketMode,
                hostRoutePath = HostRoutePrefix,
                apiKey = ApiKey,
                apiScope = ControllerApiScope.FullConfiguration,
                corsAllowedOrigins = new[] { CorsOrigin }
            }, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }),
            version: 1);
        if (!ControllerOptions.TryParseHostSettings(settings, out var parsed) || parsed is null || !parsed.Validate().IsValid)
        {
            throw new InvalidOperationException("The integration fixture settings did not round-trip through ControllerOptions.");
        }

        var now = DateTimeOffset.UtcNow;
        _host = new FakeHostBridge(new HostConfigurationSnapshot(
            version: 1,
            globalSettings: new GlobalSettingsConfiguration(
                version: 1,
                autoPortRangeStart: 20000,
                autoPortRangeEnd: 29999,
                maxRequestBodyBytes: 1_048_576,
                maxConcurrentRequests: 128,
                configurationPollInterval: TimeSpan.FromSeconds(5),
                trustedProxyCidrs: ImmutableArray<string>.Empty,
                proxyTimeouts: ProxyTimeoutConfiguration.Default,
                maxRequestHeaderBytes: 32_768,
                requestReadTimeout: TimeSpan.FromSeconds(30),
                clientIpRatePolicy: new ClientIpRatePolicyConfiguration(
                    tokenLimit: 600,
                    tokensPerPeriod: 600,
                    replenishmentPeriod: TimeSpan.FromMinutes(1),
                    queueLimit: 0,
                    rejectionBehavior: RateLimitRejectionBehavior.Reject,
                    retryAfterBehavior: RateLimitRetryAfterBehavior.None),
                proxyRetries: ProxyRetryConfiguration.Default),
            routes: ImmutableArray<RouteConfiguration>.Empty,
            services: ImmutableArray<ServiceConfiguration>.Empty,
            extensionRecords: ImmutableArray.Create(
                new ExtensionRecordConfiguration(
                    TestExtensionId,
                    "1.0.0",
                    ExtensionLoadState.Loaded,
                    now,
                    now,
                    recordVersion: 1),
                new ExtensionRecordConfiguration(
                    SpareExtensionId,
                    "1.2.0",
                    ExtensionLoadState.Loaded,
                    now,
                    now,
                    recordVersion: 1)),
            extensionSettings: ImmutableArray.Create(settings)));

        var options = new ControllerOptions
        {
            LoopbackOnly = true,
            EnableHttpJson = true,
            HttpPort = HttpPort,
            EnableUnixSocket = true,
            UnixSocketPath = SocketPath,
            UnixSocketMode = ControllerOptions.RequiredUnixSocketMode,
            EnableGrpc = true,
            GrpcPort = GrpcPort,
            EnableHostRoute = true,
            HostRoutePath = HostRoutePrefix,
            ApiKey = ApiKey,
            ApiScope = ControllerApiScope.FullConfiguration,
            CorsAllowedOrigins = ImmutableArray.Create(CorsOrigin)
        };

        _entrypoint = new ControllerEntrypoint(options);
        await _entrypoint.StartAsync(new FakeExtensionStartContext(Host, Registration), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_entrypoint is { } entrypoint)
        {
            await entrypoint.StopAsync(CancellationToken.None);
            ((IDisposable)entrypoint).Dispose();
        }

        foreach (var channel in _grpcChannels)
        {
            channel.Dispose();
        }

        try
        {
            if (Directory.Exists(_socketDirectory))
            {
                Directory.Delete(_socketDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Creates an HTTP client for the loopback JSON listener, with or without the API key.</summary>
    public HttpClient CreateHttpClient(bool withApiKey = true)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HttpPort}") };
        if (withApiKey)
        {
            client.DefaultRequestHeaders.Add(ControllerManagementApiContract.ApiKeyHeaderName, ApiKey);
        }

        return client;
    }

    /// <summary>Creates an HTTP client that dials the Unix-socket listener.</summary>
    public HttpClient CreateUnixSocketClient(bool withApiKey = true)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        if (withApiKey)
        {
            client.DefaultRequestHeaders.Add(ControllerManagementApiContract.ApiKeyHeaderName, ApiKey);
        }

        return client;
    }

    /// <summary>Creates a plaintext h2c gRPC client for the loopback listener.</summary>
    /// <remarks>Use <see cref="CreateGrpcMetadata"/> for the per-call API-key metadata.</remarks>
    public ControllerManagement.ControllerManagementClient CreateGrpcClient(bool withApiKey = true)
    {
        _ = withApiKey;
        var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{GrpcPort}");
        _grpcChannels.Add(channel);
        return new ControllerManagement.ControllerManagementClient(channel);
    }

    /// <summary>Creates gRPC metadata containing the API key when requested.</summary>
    public Metadata CreateGrpcMetadata(bool withApiKey = true)
    {
        var metadata = new Metadata();
        if (withApiKey)
        {
            metadata.Add(ControllerManagementApiContract.ApiKeyHeaderName, ApiKey);
        }

        return metadata;
    }

    /// <summary>Invokes the registered HostRoute handler directly using an immutable request.</summary>
    public async ValueTask<ExtensionHandlerResponse> InvokeHostRouteAsync(
        string method,
        string path,
        string? jsonBody = null,
        bool withApiKey = true,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null)
    {
        var handler = Registration.Handler ?? throw new InvalidOperationException("The HostRoute handler is not registered.");
        var headers = ImmutableDictionary<string, IEnumerable<string>>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);
        if (withApiKey)
        {
            headers = headers.Add(ControllerManagementApiContract.ApiKeyHeaderName, ImmutableArray.Create(ApiKey));
        }

        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                headers = headers.Add(name, ImmutableArray.Create(value));
            }
        }

        var body = jsonBody is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(jsonBody).AsMemory();
        var request = new ExtensionHandlerRequest(method, path, headers, body, isHttps: false);
        return await handler.HandleAsync(request, TestContext.Current.CancellationToken);
    }

    private static int ReserveDualStackLoopbackPort(int excludedPort = 0)
    {
        // The HTTP and gRPC adapters bind the same port on both IPv4 and IPv6 loopback.
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var ipv4 = new TcpListener(IPAddress.Loopback, 0);
            ipv4.Start();
            var port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
            ipv4.Stop();
            if (port == excludedPort)
            {
                continue;
            }

            try
            {
                var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
                ipv6.Start();
                ipv6.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException("No loopback port available on both IPv4 and IPv6.");
    }

    private static string CreateTrustedSocketDirectory()
    {
        // The adapter rejects symlinked or group/other-writable parent directories, so the socket
        // lives in a short, private directory under the repository root instead of the OS temp dir.
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, ".test-run", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nekolla.Nekostick.Controller.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root could not be located.");
    }
}

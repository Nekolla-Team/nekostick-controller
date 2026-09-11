using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

[CollectionDefinition(ControllerExtensionInstallCollectionDefinition.Name, DisableParallelization = true)]
public sealed class ControllerExtensionInstallCollectionDefinition
{
    public const string Name = "controller-extension-install";
}

[Collection(ControllerExtensionInstallCollectionDefinition.Name)]
public sealed class ExtensionInstallApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task Install_ValidPackage_ExtractsIntoExtensionRoot()
    {
        var root = CreateExtensionsRoot();
        ControllerExtensionInstaller.SetRootPathOverride(() => root);
        try
        {
            var package = BuildPackage("nekolla.nekostick.installed", "1.4.2", ("payload/plugin.dll", "fake-bytes"));
            using var client = fixture.CreateHttpClient();

            var response = await PostPackageAsync(client, package);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
            var data = envelope.RootElement.GetProperty("data");
            Assert.Equal("nekolla.nekostick.installed", data.GetProperty("id").GetString());
            Assert.Equal("1.4.2", data.GetProperty("version").GetString());
            Assert.False(data.GetProperty("replaced").GetBoolean());
            var extensionRoot = Path.Combine(root, "nekolla.nekostick.installed");
            Assert.True(File.Exists(Path.Combine(extensionRoot, "manifest.json")));
            Assert.Equal("fake-bytes", await File.ReadAllTextAsync(Path.Combine(extensionRoot, "payload", "plugin.dll"), TestContext.Current.CancellationToken));
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    [Fact]
    public async Task Install_ExistingDirectory_IsFullyReplaced()
    {
        var root = CreateExtensionsRoot();
        ControllerExtensionInstaller.SetRootPathOverride(() => root);
        try
        {
            var extensionRoot = Path.Combine(root, "nekolla.nekostick.installed");
            Directory.CreateDirectory(extensionRoot);
            await File.WriteAllTextAsync(Path.Combine(extensionRoot, "stale.txt"), "old", TestContext.Current.CancellationToken);

            var package = BuildPackage("nekolla.nekostick.installed", "1.0.0", ("fresh.txt", "new"));
            using var client = fixture.CreateHttpClient();

            var response = await PostPackageAsync(client, package);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(envelope.RootElement.GetProperty("data").GetProperty("replaced").GetBoolean());
            Assert.False(Directory.Exists(extensionRoot + ".bak"));
            Assert.False(File.Exists(Path.Combine(extensionRoot, "stale.txt")));
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(extensionRoot, "fresh.txt"), TestContext.Current.CancellationToken));
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    [Fact]
    public async Task Install_SameVersion_Replaces()
    {
        var root = CreateExtensionsRoot();
        ControllerExtensionInstaller.SetRootPathOverride(() => root);
        try
        {
            SeedInstalledManifest(root, "nekolla.nekostick.installed", "1.0.0");
            var package = BuildPackage("nekolla.nekostick.installed", "1.0.0");
            using var client = fixture.CreateHttpClient();

            var response = await PostPackageAsync(client, package);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(envelope.RootElement.GetProperty("data").GetProperty("replaced").GetBoolean());
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    [Fact]
    public async Task Install_Downgrade_ReturnsConflictAndKeepsExistingFiles()
    {
        var root = CreateExtensionsRoot();
        ControllerExtensionInstaller.SetRootPathOverride(() => root);
        try
        {
            SeedInstalledManifest(root, "nekolla.nekostick.installed", "2.0.0");
            var package = BuildPackage("nekolla.nekostick.installed", "1.9.9");
            using var client = fixture.CreateHttpClient();

            var response = await PostPackageAsync(client, package);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("downgrade_forbidden", envelope.RootElement.GetProperty("code").GetString());
            var installedManifest = await File.ReadAllTextAsync(Path.Combine(root, "nekolla.nekostick.installed", "manifest.json"), TestContext.Current.CancellationToken);
            Assert.Contains("2.0.0", installedManifest, StringComparison.Ordinal);
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    [Fact]
    public async Task Install_NonZipBody_ReturnsInvalidRequest()
    {
        var root = CreateExtensionsRoot();
        ControllerExtensionInstaller.SetRootPathOverride(() => root);
        try
        {
            using var client = fixture.CreateHttpClient();

            var response = await PostPackageAsync(client, "definitely not a zip"u8.ToArray());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("invalid_request", envelope.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    [Fact]
    public async Task Install_MissingManifest_ReturnsInvalidRequest()
    {
        var root = CreateExtensionsRoot();
        ControllerExtensionInstaller.SetRootPathOverride(() => root);
        try
        {
            byte[] package;
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("payload/readme.txt");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("no manifest here");
                }

                package = memory.ToArray();
            }

            using var client = fixture.CreateHttpClient();

            var response = await PostPackageAsync(client, package);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("invalid_request", envelope.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    [Fact]
    public async Task Install_WhenRootUnwritable_ReturnsStorageUnavailableAndLogsFailure()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), "nekostick-install-root-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blockingFile, "not a directory", TestContext.Current.CancellationToken);
        ControllerExtensionInstaller.SetRootPathOverride(() => blockingFile);
        try
        {
            using var client = fixture.CreateHttpClient();

            var response = await PostPackageAsync(client, BuildPackage("nekolla.nekostick.installed", "1.0.0"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("storage_unavailable", envelope.RootElement.GetProperty("code").GetString());
            Assert.Contains("Extension install failed", fixture.Host.LastLogText, StringComparison.Ordinal);
        }
        finally
        {
            ControllerExtensionInstaller.SetRootPathOverride(null);
            try
            {
                File.Delete(blockingFile);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Install_WithoutApiKey_ReturnsUnauthorized()
    {
        using var client = fixture.CreateHttpClient(withApiKey: false);

        var response = await PostPackageAsync(client, BuildPackage("nekolla.nekostick.installed", "1.0.0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("unauthorized", envelope.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Install_WithGetMethod_ReturnsMethodNotAllowed()
    {
        using var client = fixture.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ControllerManagementApiContract.ExtensionsInstallPath);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Install_OverBufferedHostRoute_ReturnsUnsupported()
    {
        var response = await fixture.InvokeHostRouteAsync(
            "POST",
            ControllerApiFixture.HostRoutePrefix + ControllerManagementApiContract.ExtensionsInstallPath,
            jsonBody: null);

        Assert.Equal(501, response.StatusCode);
        using var envelope = JsonDocument.Parse(response.Body.AsMemory());
        Assert.Equal("unsupported", envelope.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> PostPackageAsync(HttpClient client, byte[] package)
    {
        using var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return await client.PostAsync(ControllerManagementApiContract.ExtensionsInstallPath, content, TestContext.Current.CancellationToken);
    }

    internal static byte[] BuildPackage(string id, string version, params (string Name, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
            {
                writer.Write($$"""{"id":"{{id}}","version":"{{version}}"}""");
            }

            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    private static void SeedInstalledManifest(string root, string id, string version)
    {
        var extensionRoot = Path.Combine(root, id);
        Directory.CreateDirectory(extensionRoot);
        File.WriteAllText(Path.Combine(extensionRoot, "manifest.json"), $$"""{"id":"{{id}}","version":"{{version}}"}""");
    }

    private static string CreateExtensionsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nekostick-install-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanupRoot(string root)
    {
        ControllerExtensionInstaller.SetRootPathOverride(null);
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

[Collection(ControllerExtensionInstallCollectionDefinition.Name)]
public sealed class ExtensionInstallApi133Tests(ControllerApi133Fixture fixture) : IClassFixture<ControllerApi133Fixture>
{
    [Fact]
    public async Task StreamingHostRoute_Install_StreamsPackage()
    {
        var root = CreateExtensionsRoot();
        ControllerExtensionInstaller.SetRootPathOverride(() => root);
        try
        {
            var handler = fixture.Registration.StreamingHandler
                ?? throw new InvalidOperationException("The streaming HostRoute handler is not registered.");
            var package = ExtensionInstallApiTests.BuildPackage("nekolla.nekostick.streamed", "0.3.1", ("content/app.js", "code"));
            var headers = new[]
            {
                new KeyValuePair<string, IEnumerable<string>>(
                    ControllerManagementApiContract.ApiKeyHeaderName,
                    new[] { ControllerApiFixture.ApiKey })
            };
            ExtensionStreamingResponse response;
            await using (var body = new MemoryStream(package, writable: false))
            {
                response = await handler.HandleStreamingAsync(
                    new ExtensionStreamingRequest(
                        "POST",
                        ControllerApiFixture.HostRoutePrefix + ControllerManagementApiContract.ExtensionsInstallPath,
                        headers,
                        body),
                    TestContext.Current.CancellationToken);
            }

            using (response.BodyStream)
            {
                Assert.Equal(200, response.StatusCode);
                using var envelope = JsonDocument.Parse(response.BodyStream);
                Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("nekolla.nekostick.streamed", envelope.RootElement.GetProperty("data").GetProperty("id").GetString());
                Assert.Equal("0.3.1", envelope.RootElement.GetProperty("data").GetProperty("version").GetString());
            }

            Assert.True(File.Exists(Path.Combine(root, "nekolla.nekostick.streamed", "content", "app.js")));
        }
        finally
        {
            CleanupRoot(root);
        }
    }

    [Fact]
    public async Task StreamingHostRoute_Install_WithoutApiKey_ReturnsUnauthorized()
    {
        var handler = fixture.Registration.StreamingHandler
            ?? throw new InvalidOperationException("The streaming HostRoute handler is not registered.");

        ExtensionStreamingResponse response;
        await using (var body = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false))
        {
            response = await handler.HandleStreamingAsync(
                new ExtensionStreamingRequest(
                    "POST",
                    ControllerApiFixture.HostRoutePrefix + ControllerManagementApiContract.ExtensionsInstallPath,
                    Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>(),
                    body),
                TestContext.Current.CancellationToken);
        }

        using (response.BodyStream)
        {
            Assert.Equal(401, response.StatusCode);
        }
    }

    private static string CreateExtensionsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nekostick-install-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanupRoot(string root)
    {
        ControllerExtensionInstaller.SetRootPathOverride(null);
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

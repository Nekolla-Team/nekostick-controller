using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Controller.Management;
using Xunit;

namespace Nekolla.Nekostick.Controller.IntegrationTests;

public sealed class ExtensionsApiTests(ControllerApiFixture fixture) : IClassFixture<ControllerApiFixture>
{
    [Fact]
    public async Task GetExtensions_ReturnsConfiguredExtensionRecord()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1/extensions", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        var extensions = envelope.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, extensions.ValueKind);
        var extension = extensions.EnumerateArray().Single(item => item.GetProperty("extensionId").GetString() == ControllerApiFixture.TestExtensionId);
        Assert.Equal("Loaded", extension.GetProperty("loadState").GetString());
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("contentHash").ValueKind);
    }

    [Fact]
    public async Task GetExtensionMember_ReturnsSeededRecord()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync($"/v1/extensions/{ControllerApiFixture.TestExtensionId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(ControllerManagementApiContract.ETagHeaderName));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("version").ValueKind);
        var extension = envelope.GetProperty("data");
        Assert.Equal(ControllerApiFixture.TestExtensionId, extension.GetProperty("extensionId").GetString());
        Assert.Equal("Loaded", extension.GetProperty("loadState").GetString());
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("contentHash").ValueKind);
    }

    [Fact]
    public async Task GetExtensionMember_WithUnknownId_ReturnsNotFound()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.GetAsync("/v1/extensions/nekolla.nekostick.ghost-extension", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertErrorEnvelope(document.RootElement, "not_found");
    }

    [Fact]
    public async Task ExtensionSettings_PutReadUpdateDeleteOverRealHttp()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var settingsPath = $"/v1/extensions/{ControllerApiFixture.TestExtensionId}/settings";

        // The record exists without any persisted document: the answer is `no_settings` (not
        // `not_found`) and still carries the aggregate ETag that the first create must supply.
        using var missing = await client.GetAsync(settingsPath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingDocument = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(missingDocument.RootElement, "no_settings");
        Assert.True(missing.Headers.TryGetValues(ControllerManagementApiContract.ETagHeaderName, out var missingEtags));
        var createVersion = Assert.Single(missingEtags);
        Assert.Equal(await ReadEtagAsync(client, "/v1/global-settings", cancellationToken), createVersion);

        using var firstRequest = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"dark"}}""", createVersion);
        using var first = await client.SendAsync(firstRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync(cancellationToken));
        AssertSettings(firstDocument.RootElement, "dark");

        using var read = await client.GetAsync(settingsPath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readDocument = JsonDocument.Parse(await read.Content.ReadAsStringAsync(cancellationToken));
        AssertSettings(readDocument.RootElement, "dark");

        var secondVersion = await ReadEtagAsync(client, settingsPath, cancellationToken);
        using var secondRequest = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"light"}}""", secondVersion);
        using var second = await client.SendAsync(secondRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync(cancellationToken));
        AssertSettings(secondDocument.RootElement, "light");

        var deleteVersion = await ReadEtagAsync(client, settingsPath, cancellationToken);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, settingsPath);
        deleteRequest.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, deleteVersion);
        using var delete = await client.SendAsync(deleteRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // DELETE removes only the document; the existing record reports `no_settings` again and
        // keeps offering the aggregate ETag for a later re-create.
        using var cleared = await client.GetAsync(settingsPath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, cleared.StatusCode);
        using var clearedDocument = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(clearedDocument.RootElement, "no_settings");
        Assert.True(cleared.Headers.Contains(ControllerManagementApiContract.ETagHeaderName));
    }

    [Fact]
    public async Task GhostExtensionSettings_ReturnNotFound()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string ghostId = "nekolla.nekostick.ghost";
        var settingsPath = $"/v1/extensions/{ghostId}/settings";
        var etag = await ReadEtagAsync(client, "/v1/global-settings", cancellationToken);

        using var putRequest = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"dark"}}""", etag);
        using var put = await client.SendAsync(putRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        using var putDocument = JsonDocument.Parse(await put.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(putDocument.RootElement, "not_found");

        using var get = await client.GetAsync(settingsPath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var getDocument = JsonDocument.Parse(await get.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(getDocument.RootElement, "not_found");
    }

    [Fact]
    public async Task PutExtensionSettings_WithoutIfMatch_ReturnsPreconditionRequired()
    {
        using var client = fixture.CreateHttpClient();
        var settingsPath = $"/v1/extensions/{ControllerApiFixture.TestExtensionId}/settings";
        using var request = CreateJsonRequest(HttpMethod.Put, settingsPath, """{"schemaVersion":1,"settings":{"theme":"missing-if-match"}}""");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertErrorEnvelope(document.RootElement, "precondition_required");
    }

    [Fact]
    public async Task ExtensionLifecycle_DisableEnableReloadDelete_RoundTrips()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var memberPath = $"/v1/extensions/{ControllerApiFixture.SpareExtensionId}";

        using var initial = await client.GetAsync(memberPath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var initialDocument = JsonDocument.Parse(await initial.Content.ReadAsStringAsync(cancellationToken));
        var initialData = initialDocument.RootElement.GetProperty("data");
        Assert.Equal("Loaded", initialData.GetProperty("loadState").GetString());
        Assert.False(initialData.GetProperty("isRunning").GetBoolean());
        Assert.Equal("1.2.0", initialData.GetProperty("manifestVersion").GetString());

        using var disabled = await client.PostAsync($"{memberPath}/disable", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using var afterDisable = await client.GetAsync(memberPath, cancellationToken);
        using var afterDisableDocument = JsonDocument.Parse(await afterDisable.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("Disabled", afterDisableDocument.RootElement.GetProperty("data").GetProperty("loadState").GetString());

        using var reloadWhileDisabled = await client.PostAsync($"{memberPath}/reload", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, reloadWhileDisabled.StatusCode);
        using var reloadDocument = JsonDocument.Parse(await reloadWhileDisabled.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(reloadDocument.RootElement, "invalid_request");

        using var enabled = await client.PostAsync($"{memberPath}/enable", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var afterEnable = await client.GetAsync(memberPath, cancellationToken);
        using var afterEnableDocument = JsonDocument.Parse(await afterEnable.Content.ReadAsStringAsync(cancellationToken));
        var enabledData = afterEnableDocument.RootElement.GetProperty("data");
        Assert.Equal("Loaded", enabledData.GetProperty("loadState").GetString());
        Assert.True(enabledData.GetProperty("isRunning").GetBoolean());

        using var reloaded = await client.PostAsync($"{memberPath}/reload", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);

        using var deleted = await client.DeleteAsync($"{memberPath}/record", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var gone = await client.GetAsync(memberPath, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ExtensionReload_OnControllerTransport_ReportsCompletedReload()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Host.ClearReloadObservations();

        using var response = await client.PostAsync(
            $"/v1/extensions/{ControllerApiFixture.TestExtensionId}/reload",
            content: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        AssertReloadOutcome(document.RootElement, "reloaded");
        Assert.Equal(new[] { ControllerApiFixture.TestExtensionId }, fixture.Host.SynchronousReloads);
        Assert.Empty(fixture.Host.ScheduledReloads);
    }

    [Fact]
    public async Task ExtensionReload_OnHostRoute_SchedulesInsteadOfCallingTheVetoedOperation()
    {
        fixture.Host.ClearReloadObservations();

        var response = await fixture.InvokeHostRouteAsync(
            "POST",
            $"{ControllerApiFixture.HostRoutePrefix}/v1/extensions/{ControllerApiFixture.TestExtensionId}/reload");

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body.ToArray());
        AssertReloadOutcome(document.RootElement, "scheduled");
        Assert.Equal(new[] { ControllerApiFixture.TestExtensionId }, fixture.Host.ScheduledReloads);
        Assert.Empty(fixture.Host.SynchronousReloads);
    }

    [Fact]
    public async Task ExtensionReload_OnHostRouteForUnknownExtension_ReturnsNotFound()
    {
        fixture.Host.ClearReloadObservations();

        var response = await fixture.InvokeHostRouteAsync(
            "POST",
            $"{ControllerApiFixture.HostRoutePrefix}/v1/extensions/nekolla.nekostick.ghost-reload/reload");

        Assert.Equal(404, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body.ToArray());
        AssertErrorEnvelope(document.RootElement, "not_found");
        Assert.Empty(fixture.Host.ScheduledReloads);
    }

    [Fact]
    public async Task ExtensionReload_OnHostRouteForDisabledExtension_ReturnsInvalidRequest()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        fixture.Host.ClearReloadObservations();
        using (var disabled = await client.PostAsync(
            $"/v1/extensions/{ControllerApiFixture.TestExtensionId}/disable",
            content: null,
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        }

        try
        {
            var response = await fixture.InvokeHostRouteAsync(
                "POST",
                $"{ControllerApiFixture.HostRoutePrefix}/v1/extensions/{ControllerApiFixture.TestExtensionId}/reload");

            Assert.Equal(400, response.StatusCode);
            using var document = JsonDocument.Parse(response.Body.ToArray());
            AssertErrorEnvelope(document.RootElement, "invalid_request");
            Assert.Empty(fixture.Host.ScheduledReloads);
        }
        finally
        {
            using var enabled = await client.PostAsync(
                $"/v1/extensions/{ControllerApiFixture.TestExtensionId}/enable",
                content: null,
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        }
    }

    [Fact]
    public async Task ExtensionReload_OnHostRouteWhenSchedulingIsRefused_ReturnsUnsupported()
    {
        fixture.Host.ClearReloadObservations();
        fixture.Host.SetReloadScheduleRefused(true);
        try
        {
            var response = await fixture.InvokeHostRouteAsync(
                "POST",
                $"{ControllerApiFixture.HostRoutePrefix}/v1/extensions/{ControllerApiFixture.TestExtensionId}/reload");

            Assert.Equal(501, response.StatusCode);
            using var document = JsonDocument.Parse(response.Body.ToArray());
            AssertErrorEnvelope(document.RootElement, "unsupported");
            Assert.Empty(fixture.Host.SynchronousReloads);
        }
        finally
        {
            fixture.Host.SetReloadScheduleRefused(false);
        }
    }

    [Fact]
    public async Task ExtensionLifecycle_UnknownId_ReturnsNotFound()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string ghostPath = "/v1/extensions/nekolla.nekostick.ghost-lifecycle";

        using var enabled = await client.PostAsync($"{ghostPath}/enable", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, enabled.StatusCode);
        using var enableDocument = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync(cancellationToken));
        AssertErrorEnvelope(enableDocument.RootElement, "not_found");

        using var deleted = await client.DeleteAsync($"{ghostPath}/record", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    [Fact]
    public async Task ExtensionLifecycle_WithWrongMethod_ReturnsMethodNotAllowed()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var enable = await client.GetAsync($"/v1/extensions/{ControllerApiFixture.TestExtensionId}/enable", cancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, enable.StatusCode);
        using var refresh = await client.GetAsync("/v1/extensions/refresh", cancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, refresh.StatusCode);
    }

    [Fact]
    public async Task ExtensionsRefresh_ReturnsSummary()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.PostAsync("/v1/extensions/refresh", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = document.RootElement;
        AssertSuccessfulEnvelope(envelope);
        var data = envelope.GetProperty("data");
        Assert.Empty(data.GetProperty("added").EnumerateArray());
        Assert.Empty(data.GetProperty("versionUpdated").EnumerateArray());
        Assert.Empty(data.GetProperty("missing").EnumerateArray());
    }

    [Fact]
    public async Task ExtensionLifecycle_WhenManagementFails_MapsErrors()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        fixture.Host.FailNextManagement(ConfigurationErrorCode.StorageUnavailable);
        using var listed = await client.GetAsync("/v1/extensions", cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, listed.StatusCode);

        fixture.Host.FailNextManagement(ConfigurationErrorCode.StorageUnavailable);
        using var enabled = await client.PostAsync($"/v1/extensions/{ControllerApiFixture.TestExtensionId}/disable", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, enabled.StatusCode);

        fixture.Host.FailNextManagement(ConfigurationErrorCode.StorageUnavailable);
        using var refreshed = await client.PostAsync("/v1/extensions/refresh", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refreshed.StatusCode);
    }

    private static void AssertReloadOutcome(JsonElement envelope, string outcome)
    {
        AssertSuccessfulEnvelope(envelope);
        Assert.Equal(outcome, envelope.GetProperty("data").GetProperty("outcome").GetString());
    }

    private static void AssertSettings(JsonElement envelope, string theme)
    {
        AssertSuccessfulEnvelope(envelope);
        var data = envelope.GetProperty("data");
        Assert.Equal(ControllerApiFixture.TestExtensionId, data.GetProperty("extensionId").GetString());
        Assert.Equal(1, data.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(theme, data.GetProperty("settings").GetProperty("theme").GetString());
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, string body, string? etag = null, string mediaType = "application/json")
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation(ControllerManagementApiContract.IfMatchHeaderName, etag);
        }

        return request;
    }

    private static async Task<string> ReadEtagAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(ControllerManagementApiContract.ETagHeaderName, out var values));
        var etag = Assert.Single(values);
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);
        return etag;
    }

    private static void AssertSuccessfulEnvelope(JsonElement envelope)
    {
        Assert.Equal(1, envelope.GetProperty("apiVersion").GetInt32());
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", envelope.GetProperty("code").GetString());
    }

    private static void AssertErrorEnvelope(JsonElement envelope, string code)
    {
        Assert.Equal(1, envelope.GetProperty("apiVersion").GetInt32());
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(code, envelope.GetProperty("code").GetString());
    }
}

public sealed class ExtensionsApi133Tests(ControllerApi133Fixture fixture) : IClassFixture<ControllerApi133Fixture>
{
    [Fact]
    public async Task ExtensionRecords_ExposeContentHashOnApi133Host()
    {
        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var member = await client.GetAsync($"/v1/extensions/{ControllerApiFixture.TestExtensionId}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        using var memberDocument = JsonDocument.Parse(await member.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            memberDocument.RootElement.GetProperty("data").GetProperty("contentHash").GetString());

        using var root = await client.GetAsync("/v1", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        using var rootDocument = JsonDocument.Parse(await root.Content.ReadAsStringAsync(cancellationToken));
        var extension = rootDocument.RootElement.GetProperty("data").GetProperty("extensions").EnumerateArray()
            .Single(item => item.GetProperty("extensionId").GetString() == ControllerApiFixture.TestExtensionId);
        Assert.Equal(
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            extension.GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task RefreshExtensions_OmitsSkippedBeforeApi134()
    {
        using var client = fixture.CreateHttpClient();
        using var response = await client.PostAsync("/v1/extensions/refresh", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("skipped").ValueKind);
    }
}

public sealed class ExtensionsApi134Tests(ControllerApi134Fixture fixture) : IClassFixture<ControllerApi134Fixture>
{
    [Fact]
    public async Task RefreshExtensions_ExposesSkippedDirectories()
    {
        fixture.Host.SetRefreshSkips(
        [
            new ExtensionScanSkip("broken-ext", "ManifestMissing"),
            new ExtensionScanSkip("bad-json", "JsonInvalid")
        ]);

        using var client = fixture.CreateHttpClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.PostAsync("/v1/extensions/refresh", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var skipped = document.RootElement.GetProperty("data").GetProperty("skipped");
        Assert.Equal(JsonValueKind.Array, skipped.ValueKind);
        var entries = skipped.EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("broken-ext", entries[0].GetProperty("directoryName").GetString());
        Assert.Equal("ManifestMissing", entries[0].GetProperty("failureCode").GetString());
        Assert.Equal("bad-json", entries[1].GetProperty("directoryName").GetString());
        Assert.Equal("JsonInvalid", entries[1].GetProperty("failureCode").GetString());
    }
}

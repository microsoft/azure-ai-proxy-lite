using System.Net;
using System.Text.Json;
using AzureAIProxy.Shared.Database;
using AzureAIProxy.Tests.Fixtures;

namespace AzureAIProxy.Tests.Routes;

public class EventRouteTests : IClassFixture<ProxyAppFixture>
{
    private readonly ProxyAppFixture _fixture;

    public EventRouteTests(ProxyAppFixture fixture)
    {
        _fixture = fixture;
    }

    // --- POST /api/v1/eventinfo (requires ApiKey auth) ---

    [SkippableFact]
    public async Task EventInfo_NoApiKey_Returns401()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var response = await _fixture.Client.PostAsync("/api/v1/eventinfo", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task EventInfo_ValidKey_ReturnsEventData()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-info", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-4o", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-info", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/eventinfo");
        request.Headers.Add("api-key", apiKey);

        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("is_authorized").GetBoolean());
        Assert.Equal(500, root.GetProperty("max_token_cap").GetInt32());
        Assert.True(root.TryGetProperty("capabilities", out var caps));
        Assert.True(caps.TryGetProperty("foundry-model", out var foundryModels));
        Assert.Contains("gpt-4o", foundryModels.EnumerateArray().Select(e => e.GetString()));
    }

    [SkippableFact]
    public async Task EventInfo_ReturnsCorrectOrganizerDetails()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        await _fixture.SeedEventAsync(eventId, "alice");
        var apiKey = await _fixture.SeedAttendeeAsync("user-org", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/eventinfo");
        request.Headers.Add("api-key", apiKey);

        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Org-alice", doc.RootElement.GetProperty("organizer_name").GetString());
        Assert.Equal("alice@example.com", doc.RootElement.GetProperty("organizer_email").GetString());
    }

    // --- GET /api/v1/event/{eventId} (AllowAnonymous) ---

    [SkippableFact]
    public async Task EventRegistration_NonExistentEvent_Returns404()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var response = await _fixture.Client.GetAsync($"/api/v1/event/nonexistent-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task EventRegistration_ExistingEvent_ReturnsEventData_NoAuthRequired()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-reg", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-4o", ModelType.Foundry_Model.ToStorageString());

        // No api-key header — this endpoint is [AllowAnonymous]
        var response = await _fixture.Client.GetAsync($"/api/v1/event/{eventId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(eventId, root.GetProperty("event_id").GetString());
        Assert.Equal("# Test Event", root.GetProperty("event_markdown").GetString());
        Assert.True(root.TryGetProperty("proxy_url", out _));
        Assert.True(root.TryGetProperty("capabilities", out var caps));
        Assert.True(caps.TryGetProperty("foundry-model", out _));
    }

    [SkippableFact]
    public async Task EventRegistration_ReturnsProxyUrl()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        await _fixture.SeedEventAsync(eventId, "owner-url");

        var response = await _fixture.Client.GetAsync($"/api/v1/event/{eventId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var proxyUrl = doc.RootElement.GetProperty("proxy_url").GetString();

        // Fixture configures ProxyUrl = "http://localhost/api/v1"
        Assert.Equal("http://localhost/api/v1", proxyUrl);
    }

    [SkippableFact]
    public async Task EventRegistration_ReturnsDefaultSamplesPreference()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        await _fixture.SeedEventAsync(eventId, "owner-samples", showDefaultSamples: false);

        var response = await _fixture.Client.GetAsync($"/api/v1/event/{eventId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("show_default_samples").GetBoolean());
    }

    [SkippableFact]
    public async Task EventRegistration_ReturnsMcpServerEndpoints_WhenConfigured()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var mcpCatalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-mcp", catalogIds: mcpCatalogId);
        await _fixture.SeedCatalogAsync(mcpCatalogId, "docs-server", ModelType.MCP_Server.ToStorageString());

        var response = await _fixture.Client.GetAsync($"/api/v1/event/{eventId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var mcpEndpoints = doc.RootElement.GetProperty("mcp_server_endpoints");
        Assert.Equal(JsonValueKind.Array, mcpEndpoints.ValueKind);

        var endpoint = mcpEndpoints.EnumerateArray().Single();
        Assert.Equal("docs-server", endpoint.GetProperty("deployment_name").GetString());
        Assert.Equal("http://localhost/api/v1/mcp/docs-server", endpoint.GetProperty("endpoint_url").GetString());
    }
}

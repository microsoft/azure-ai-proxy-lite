using System.Net;
using System.Text;
using System.Text.Json;
using AzureAIProxy.Shared.Database;
using AzureAIProxy.Tests.Fixtures;

namespace AzureAIProxy.Tests.Routes;

public class AzureOpenAIRouteTests : IClassFixture<ProxyAppFixture>
{
    private readonly ProxyAppFixture _fixture;

    public AzureOpenAIRouteTests(ProxyAppFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task ChatCompletions_NoApiKey_Returns401()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var response = await _fixture.Client.PostAsync(
            "/api/v1/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21",
            JsonContent("{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ChatCompletions_InvalidApiKey_Returns401()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21");
        request.Headers.Add("api-key", "totally-invalid-key");
        request.Content = JsonContent("{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ChatCompletions_ValidKey_DeploymentNotFound_Returns404()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        await _fixture.SeedEventAsync(eventId, "owner-test");
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/nonexistent-model/chat/completions?api-version=2024-10-21");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent("{\"model\":\"nonexistent-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("nonexistent-model", body);
    }

    [SkippableFact]
    public async Task ChatCompletions_ValidKey_ValidDeployment_Returns200WithMockResponse()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-4o", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent("{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Diagnostic: surface the actual error
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 but got {(int)response.StatusCode}. Body: {body}");
        // Mock mode returns canned response (either from file or fallback JSON)
        Assert.True(body.Contains("choices") || body.Contains("Upstream proxy"),
            $"Unexpected mock response body: {body}");
    }

    [SkippableFact]
    public async Task Responses_RootOpenAIPath_ValidKeyAndFoundryModel_Returns200()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-5-mini", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/responses");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent("{\"model\":\"gpt-5-mini\",\"input\":\"hi\"}");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Responses_RootOpenAIPath_BearerTokenAndFoundryModel_Returns200()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-5-mini", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/responses");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent("{\"model\":\"gpt-5-mini\",\"input\":\"hi\"}");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Responses_CompleteUpstreamEndpoint_DoesNotDuplicateRequestPath()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        const string upstreamEndpoint = "https://fake-endpoint.example.com/openai/v1/responses";
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(
            catalogId,
            "foundry-agent",
            ModelType.Foundry_Agent.ToStorageString(),
            endpointUrl: upstreamEndpoint);
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/responses");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent("{\"model\":\"foundry-agent\",\"input\":\"hi\"}");

        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(upstreamEndpoint, body);
        Assert.DoesNotContain("/openai/v1/responses/openai/v1/responses", body);
    }

    [SkippableFact]
    public async Task Embeddings_RootOpenAIPath_BearerTokenAndFoundryModel_Returns200()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "text-embedding-3-small", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/embeddings");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent("{\"model\":\"text-embedding-3-small\",\"input\":\"hello\"}");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Responses_CanonicalApiPath_IgnoresRequestModelAndUsesFoundryAgent()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var agentCatalogId = Guid.NewGuid().ToString();
        var modelCatalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: $"{agentCatalogId},{modelCatalogId}");
        await _fixture.SeedCatalogAsync(
            agentCatalogId,
            "foundry-agent",
            ModelType.Foundry_Agent.ToStorageString(),
            endpointUrl: "https://foundry-agent.example.com");
        await _fixture.SeedCatalogAsync(
            modelCatalogId,
            "gpt-5-mini",
            ModelType.Foundry_Model.ToStorageString(),
            endpointUrl: "https://gpt-5-mini.example.com");
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/openai/v1/responses");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent("{\"model\":\"gpt-5-mini\",\"input\":\"hi\"}");

        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("foundry-agent.example.com", body);
        Assert.DoesNotContain("gpt-5-mini.example.com", body);
    }

    [SkippableFact]
    public async Task ChatCompletions_BearerToken_Returns200()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-4o", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent("{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task ChatCompletions_MaxTokensExceedsCap_Returns400()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-4o", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        // Event has MaxTokenCap=500; send max_tokens=501
        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent("{\"model\":\"gpt-4o\",\"max_tokens\":501,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("max_tokens", body);
    }

    [SkippableFact]
    public async Task Embeddings_ValidKey_ValidDeployment_Returns200()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "text-embedding", ModelType.Foundry_Model.ToStorageString());
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/text-embedding/embeddings?api-version=2024-10-21");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent("{\"input\":\"hello world\"}");

        var response = await _fixture.Client.SendAsync(request);

        // Mock proxy returns OK (foundry-model mock response)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task ChatCompletions_CrossEvent_Key_Cannot_Access_Other_Event_Deployment()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventA = $"evt-a-{Guid.NewGuid():N}";
        var eventB = $"evt-b-{Guid.NewGuid():N}";
        var catalogA = Guid.NewGuid().ToString();
        var catalogB = Guid.NewGuid().ToString();

        await _fixture.SeedEventAsync(eventA, "owner-alice", catalogIds: catalogA);
        await _fixture.SeedEventAsync(eventB, "owner-bob", catalogIds: catalogB);
        await _fixture.SeedCatalogAsync(catalogA, "model-a", ModelType.Foundry_Model.ToStorageString());
        await _fixture.SeedCatalogAsync(catalogB, "model-b", ModelType.Foundry_Model.ToStorageString());

        var keyA = await _fixture.SeedAttendeeAsync("user-alice", eventA);

        // Key A tries to use model-b (which belongs to event B)
        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/model-b/chat/completions?api-version=2024-10-21");
        request.Headers.Add("api-key", keyA);
        request.Content = JsonContent("{\"model\":\"model-b\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("model-b", body);
    }

    [SkippableFact]
    public async Task ChatCompletions_ManagedIdentityCatalog_Returns200()
    {
        Skip.IfNot(_fixture.Available, "Azurite not available");

        var eventId = $"evt-{Guid.NewGuid():N}";
        var catalogId = Guid.NewGuid().ToString();
        await _fixture.SeedEventAsync(eventId, "owner-test", catalogIds: catalogId);
        await _fixture.SeedCatalogAsync(catalogId, "gpt-4o-mi", ModelType.Foundry_Model.ToStorageString(),
            useManagedIdentity: true);
        var apiKey = await _fixture.SeedAttendeeAsync("user-1", eventId);

        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/openai/deployments/gpt-4o-mi/chat/completions?api-version=2024-10-21");
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent("{\"model\":\"gpt-4o-mi\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");

        var response = await _fixture.Client.SendAsync(request);

        // MockProxyService handles managed-identity branch and returns OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");
}

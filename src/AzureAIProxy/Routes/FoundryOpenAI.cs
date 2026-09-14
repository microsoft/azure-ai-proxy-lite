using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AzureAIProxy.Middleware;
using AzureAIProxy.Shared.Database;
using AzureAIProxy.Routes.CustomResults;
using AzureAIProxy.Services;
using AzureAIProxy.Models;

namespace AzureAIProxy.Routes;

/// <summary>
/// Routes for the OpenAI-compatible endpoints used by Foundry Agent Service.
/// Handles conversations and responses API calls made via get_openai_client().
/// The AIProjectClient's get_openai_client() sets base_url to {endpoint}/openai/v1,
/// so these routes are under /openai/v1/.
/// </summary>
public static class FoundryOpenAI
{
    public static RouteGroupBuilder MapFoundryOpenAIRoutes(this RouteGroupBuilder builder)
    {
        var openAIGroup = builder.MapGroup("/openai/v1");

        // Conversations: create, retrieve, update, delete
        openAIGroup.MapPost("/conversations", HandleRequestAsync);
        openAIGroup.MapGet("/conversations/{conversationId}", HandleRequestAsync);
        openAIGroup.MapPost("/conversations/{conversationId}", HandleRequestAsync);
        openAIGroup.MapDelete("/conversations/{conversationId}", HandleRequestAsync);

        // Responses: create, retrieve, delete, cancel, compact
        openAIGroup.MapPost("/responses", HandleRequestAsync);
        openAIGroup.MapGet("/responses/{responseId}", HandleRequestAsync);
        openAIGroup.MapDelete("/responses/{responseId}", HandleRequestAsync);
        openAIGroup.MapPost("/responses/{responseId}/cancel", HandleRequestAsync);
        openAIGroup.MapPost("/responses/compact", HandleRequestAsync);

        // OpenAI-compatible embeddings route used by OpenAIClient.GetEmbeddingClient.
        openAIGroup.MapPost("/embeddings", HandleRequestAsync);

        return builder;
    }

    [Authorize(AuthenticationSchemes = $"{ProxyAuthenticationOptions.ApiKeyScheme},{ProxyAuthenticationOptions.BearerTokenScheme}")]
    private static async Task<IResult> HandleRequestAsync(
        [FromServices] ICatalogService catalogService,
        [FromServices] IProxyService proxyService,
        [FromServices] IFoundryAgentService foundryAgentService,
        [FromServices] ILoggerFactory loggerFactory,
        HttpContext context,
        string? conversationId = null,
        string? responseId = null
    )
    {
        var logger = loggerFactory.CreateLogger("FoundryOpenAI");
        logger.LogInformation("Foundry OpenAI handler invoked: {Method} {Path}", context.Request.Method, context.Request.Path);

        string requestPath = (string)context.Items["requestPath"]!;
        RequestContext requestContext = (RequestContext)context.Items["RequestContext"]!;
        JsonDocument requestJsonDoc = (JsonDocument)context.Items["jsonDoc"]!;
        bool isStreaming = (bool?)context.Items["IsStreaming"] ?? false;

        logger.LogInformation("Foundry OpenAI: eventId={EventId}, requestPath={RequestPath}, conversationId={ConvId}, responseId={RespId}, streaming={Streaming}",
            requestContext.EventId, requestPath, conversationId, responseId, isStreaming);

        var modelName = context.Items["ModelName"] as string;
        var isOpenAIClientCompatibilityPath = context.Request.Path.StartsWithSegments("/openai/v1");
        Deployment? deployment;
        if (!isOpenAIClientCompatibilityPath || string.IsNullOrWhiteSpace(modelName))
        {
            deployment = await catalogService.GetEventFoundryAgentAsync(requestContext.EventId);
        }
        else
        {
            (deployment, _) = await catalogService.GetCatalogItemAsync(requestContext.EventId, modelName);
        }

        if (deployment is null)
        {
            logger.LogWarning("Foundry OpenAI: No deployment found for eventId={EventId}, model={ModelName}", requestContext.EventId, modelName);
            return OpenAIResult.NotFound(!isOpenAIClientCompatibilityPath || string.IsNullOrWhiteSpace(modelName)
                ? "No Foundry Agent deployment found for the event."
                : $"Model '{modelName}' not found for the event.");
        }
        logger.LogInformation("Foundry OpenAI: Found deployment={DeploymentName}, endpoint={Endpoint}, useMI={UseMI}, modelType={ModelType}",
            deployment.DeploymentName, deployment.EndpointUrl, deployment.UseManagedIdentity, deployment.ModelType);

        // requestPath will be "openai/v1/conversations/..." or "openai/v1/responses/...".
        // Foundry project endpoints need the request path appended, while direct model
        // endpoints can already include the complete OpenAI route.
        var url = new UriBuilder(deployment.EndpointUrl.TrimEnd('/'));
        var normalizedRequestPath = requestPath.TrimStart('/');
        if (!url.Path.TrimEnd('/').EndsWith($"/{normalizedRequestPath}", StringComparison.OrdinalIgnoreCase))
            url.Path = url.Path.TrimEnd('/') + "/" + normalizedRequestPath;
        logger.LogInformation("Foundry OpenAI: Forwarding to upstream URL={Url}", url.Uri);

        var authHeader = await proxyService.GetAuthenticationHeaderAsync(deployment);
        logger.LogInformation("Foundry OpenAI: Auth header type={AuthType}", authHeader.Key);
        List<RequestHeader> requestHeaders = [authHeader];

        // Validate ownership of conversation/response objects
        var validationResult = await ValidateObjectAccess(
            foundryAgentService, context.Request.Method, conversationId, responseId, requestContext);
        if (validationResult is not null) return validationResult;

        var methodHandlers = new Dictionary<string, Func<Task<(string, int)>>>
        {
            [HttpMethod.Get.Method] = () => proxyService.HttpGetAsync(url, requestHeaders, context, requestContext, deployment),
            [HttpMethod.Post.Method] = () => proxyService.HttpPostAsync(url, requestHeaders, context, requestJsonDoc!, requestContext, deployment),
            [HttpMethod.Delete.Method] = () => proxyService.HttpDeleteAsync(url, requestHeaders, context, requestContext, deployment),
        };

        if (isStreaming && context.Request.Method == HttpMethod.Post.Method)
        {
            await proxyService.HttpPostStreamAsync(url, requestHeaders, context, requestJsonDoc!, requestContext, deployment);
            return new ProxyResult(null!, (int)HttpStatusCode.OK);
        }
        else if (methodHandlers.TryGetValue(context.Request.Method, out var handler))
        {
            try
            {
                var (responseContent, statusCode) = await handler();

                await TrackObjects(foundryAgentService, context, requestPath, requestContext, responseContent ?? string.Empty, statusCode);

                return new ProxyResult(responseContent ?? string.Empty, statusCode);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
            {
                return OpenAIResult.ServiceUnavailable("The request was canceled due to timeout. Inner exception: " + ex.InnerException.Message);
            }
            catch (TaskCanceledException ex)
            {
                return OpenAIResult.ServiceUnavailable("The request was canceled: " + ex.Message);
            }
            catch (HttpRequestException ex)
            {
                return OpenAIResult.ServiceUnavailable("The request failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                return OpenAIResult.InternalServerError($"An error occurred processing the request: {ex.Message}");
            }
        }
        return OpenAIResult.MethodNotAllowed("Unsupported HTTP method: " + context.Request.Method);
    }

    /// <summary>
    /// Validate that the caller owns the conversation/response they're trying to access.
    /// Only validates on operations targeting specific IDs (not list/create).
    /// </summary>
    private static async Task<IResult?> ValidateObjectAccess(
        IFoundryAgentService foundryAgentService,
        string method,
        string? conversationId,
        string? responseId,
        RequestContext requestContext)
    {
        if (conversationId is not null)
        {
            if (!await foundryAgentService.ValidateObjectAsync(requestContext.ApiKey, $"conversation:{conversationId}"))
                return OpenAIResult.Unauthorized("Unauthorized conversation access.");
        }

        if (responseId is not null && responseId != "compact")
        {
            if (!await foundryAgentService.ValidateObjectAsync(requestContext.ApiKey, $"response:{responseId}"))
                return OpenAIResult.Unauthorized("Unauthorized response access.");
        }

        return null;
    }

    /// <summary>
    /// Track conversation/response creation and deletion for ownership.
    /// </summary>
    private static async Task TrackObjects(
        IFoundryAgentService foundryAgentService,
        HttpContext context,
        string requestPath,
        RequestContext requestContext,
        string responseContent,
        int statusCode)
    {
        if (statusCode < 200 || statusCode >= 300 || string.IsNullOrEmpty(responseContent))
            return;

        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (id is null) return;

            if (context.Request.Method == HttpMethod.Post.Method)
            {
                // Determine object type from the request path
                if (requestPath.Contains("/conversations") && !requestPath.Contains("/conversations/"))
                {
                    // POST /openai/v1/conversations — creating a new conversation
                    await foundryAgentService.AddObjectAsync(requestContext.ApiKey, $"conversation:{id}", "conversation");
                }
                else if (requestPath.Contains("/responses") && !requestPath.Contains("/responses/"))
                {
                    // POST /openai/v1/responses — creating a response
                    await foundryAgentService.AddObjectAsync(requestContext.ApiKey, $"response:{id}", "response");
                }
            }
            else if (context.Request.Method == HttpMethod.Delete.Method)
            {
                if (requestPath.Contains("/conversations/"))
                {
                    await foundryAgentService.DeleteObjectAsync(requestContext.ApiKey, $"conversation:{id}");
                }
                else if (requestPath.Contains("/responses/"))
                {
                    await foundryAgentService.DeleteObjectAsync(requestContext.ApiKey, $"response:{id}");
                }
            }
        }
        catch (JsonException)
        {
            // Response wasn't valid JSON - skip tracking
        }
    }
}

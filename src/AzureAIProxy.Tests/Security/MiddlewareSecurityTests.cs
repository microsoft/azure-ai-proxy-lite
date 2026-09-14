using System.Text;
using AzureAIProxy.Middleware;
using AzureAIProxy.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace AzureAIProxy.Tests.Security;

public class MiddlewareSecurityTests
{
    [Fact]
    public async Task LoadProperties_InvalidJson_ReturnsBadRequest_AndSkipsNext()
    {
        var nextCalled = false;
        var middleware = new LoadProperties(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/openai/deployments/test/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{ invalid-json"));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task LoadProperties_ValidJson_SetsRequestItems()
    {
        var nextCalled = false;
        var middleware = new LoadProperties(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/openai/v1/responses";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"gpt-4.1\",\"stream\":true}"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal("openai/v1/responses", context.Items["requestPath"]);
        Assert.Equal("gpt-4.1", context.Items["ModelName"]);
        Assert.Equal(true, context.Items["IsStreaming"]);
        Assert.NotNull(context.Items["jsonDoc"]);
    }

    [Fact]
    public async Task LoadProperties_RootOpenAIPath_SetsRequestPathWithoutLeadingSlash()
    {
        var middleware = new LoadProperties(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Path = "/openai/v1/responses";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        await middleware.InvokeAsync(context);

        Assert.Equal("openai/v1/responses", context.Items["requestPath"]);
    }

    [Fact]
    public async Task MaxTokensHandler_MaxTokensAboveCap_ReturnsBadRequest()
    {
        var nextCalled = false;
        var middleware = new MaxTokensHandler(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        using var jsonDoc = TestData.ParseJson("{\"max_tokens\":201}");
        var context = new DefaultHttpContext();
        context.Items["RequestContext"] = TestData.CreateRequestContext(maxTokenCap: 200);
        context.Items["jsonDoc"] = jsonDoc;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task MaxTokensHandler_MaxTokensWithinCap_CallsNext()
    {
        var nextCalled = false;
        var middleware = new MaxTokensHandler(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        using var jsonDoc = TestData.ParseJson("{\"max_tokens\":200}");
        var context = new DefaultHttpContext();
        context.Items["RequestContext"] = TestData.CreateRequestContext(maxTokenCap: 200);
        context.Items["jsonDoc"] = jsonDoc;

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task RateLimiterHandler_ExceededDailyCap_ReturnsTooManyRequests()
    {
        var nextCalled = false;
        var rateLimit = new StubRateLimitService { RequestCountToReturn = 101 };
        var middleware = new RateLimiterHandler(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, rateLimit);

        var context = new DefaultHttpContext();
        context.Items["RequestContext"] = TestData.CreateRequestContext(apiKey: "key-1", dailyRequestCap: 100);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task RateLimiterHandler_AtDailyCap_ReturnsTooManyRequests()
    {
        var nextCalled = false;
        var rateLimit = new StubRateLimitService { RequestCountToReturn = 100 };
        var middleware = new RateLimiterHandler(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, rateLimit);

        var context = new DefaultHttpContext();
        context.Items["RequestContext"] = TestData.CreateRequestContext(apiKey: "key-1", dailyRequestCap: 100);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.False(nextCalled);
    }
}

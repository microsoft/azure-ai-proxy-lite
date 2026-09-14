using Azure.Data.Tables;
using Azure.Identity;
using System.Security.Cryptography;
using System.Text;
using AzureAIProxy.Middleware;
using AzureAIProxy.Routes;
using AzureAIProxy.Services;
using AzureAIProxy.Shared.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
var useMockProxy = builder.Configuration.GetValue<bool>("UseMockProxy", false);

// --- Table Storage ---
var storageConnectionString = builder.Configuration.GetConnectionString("StorageAccount")
    ?? builder.Configuration["StorageAccountConnectionString"];

if (!string.IsNullOrEmpty(storageConnectionString))
{
    builder.Services.AddSingleton(new TableServiceClient(storageConnectionString));
}
else
{
    var storageAccountName = builder.Configuration["StorageAccountName"]
        ?? throw new InvalidOperationException("StorageAccountName or StorageAccount connection string must be configured");
    var serviceUri = new Uri($"https://{storageAccountName}.table.core.windows.net");
    builder.Services.AddSingleton(new TableServiceClient(serviceUri, new DefaultAzureCredential()));
}

builder.Services.AddSingleton<ITableStorageService, TableStorageService>();

// --- Encryption ---
var encryptionKey = builder.Configuration["EncryptionKey"]
    ?? builder.Configuration["PostgresEncryptionKey"]
    ?? throw new InvalidOperationException("EncryptionKey must be configured");
builder.Services.AddSingleton<IEncryptionService>(new EncryptionService(encryptionKey));

// --- Authentication (API key / JWT / Bearer only) ---
builder
    .Services.AddAuthentication()
    .AddScheme<ProxyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ProxyAuthenticationOptions.ApiKeyScheme,
        _ => { }
    )
    .AddScheme<ProxyAuthenticationOptions, JwtAuthenticationHandler>(
        ProxyAuthenticationOptions.JwtScheme,
        _ => { }
    )
    .AddScheme<ProxyAuthenticationOptions, BearerTokenAuthenticationHandler>(
        ProxyAuthenticationOptions.BearerTokenScheme,
        _ => { }
    );

builder.Services.AddAuthorization();

// --- Proxy Services ---
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICatalogCacheService, CatalogCacheService>();
builder.Services.AddSingleton<IEventCacheService, EventCacheService>();
builder.Services.AddHttpClient<IProxyService, ProxyService>();
builder.Services.AddProxyServices(useMockProxy);

if (!string.IsNullOrEmpty(builder.Configuration["ApplicationInsights:ConnectionString"] ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// Proxy middleware for API routes and the root OpenAI Responses compatibility alias.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path.StartsWithSegments("/openai/v1"),
    appBuilder =>
    {
        appBuilder.UseMiddleware<RateLimiterHandler>();
        appBuilder.UseMiddleware<LoadProperties>();
        appBuilder.UseMiddleware<MaxTokensHandler>();
    }
);

// Map Proxy API routes
app.MapProxyRoutes();

// The default .NET OpenAI client appends /openai/v1 to its configured endpoint.
// Keep /api/v1 as the canonical proxy base while accepting the root path as an alias.
app.MapGroup("").MapFoundryOpenAIRoutes();

// Cache invalidation endpoint (called by admin container via internal FQDN).
// Protected by shared secret. The endpoint only invalidates in-memory caches,
// so the worst case for an attacker with the key is a cache reset (not data exposure).
app.MapPost("/internal/cache/invalidate", (
    ICatalogCacheService catalogCache,
    IEventCacheService eventCache,
    IConfiguration config,
    HttpContext context) =>
{
    var expectedKey = config["EncryptionKey"] ?? config["PostgresEncryptionKey"] ?? "";
    if (string.IsNullOrEmpty(expectedKey))
        return Results.StatusCode(503);

    if (!context.Request.Headers.TryGetValue("X-Cache-Key", out var keyValues)
        || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(keyValues.ToString()),
            Encoding.UTF8.GetBytes(expectedKey)))
    {
        return Results.Unauthorized();
    }

    catalogCache.InvalidateAll();
    eventCache.InvalidateAll();
    return Results.Ok();
});

app.Run();

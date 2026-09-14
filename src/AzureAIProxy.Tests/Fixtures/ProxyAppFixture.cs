using Azure.Data.Tables;
using AzureAIProxy.Shared.Services;
using AzureAIProxy.Shared.TableStorage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureAIProxy.Tests.Fixtures;

/// <summary>
/// Boots the real AzureAIProxy app with UseMockProxy=true and Azurite table storage.
/// Exercises the full pipeline: auth → middleware → route handler → mock proxy → response.
/// </summary>
public class ProxyAppFixture : IAsyncLifetime
{
    private const string EncryptionKey = "dev-encryption-key-change-in-production";

    private WebApplicationFactory<Program>? _factory;
    private TableServiceClient? _tableServiceClient;
    private ITableStorageService? _tableStorage;

    public HttpClient Client { get; private set; } = null!;
    public ITableStorageService TableStorage => _tableStorage!;
    public IEncryptionService Encryption { get; } = new EncryptionService(EncryptionKey);
    public bool Available { get; private set; }

    public async Task InitializeAsync()
    {
        var result = await AzuriteHelper.TryCreateLocalAzuriteClientWithConnectionStringAsync();
        if (result is null)
        {
            Available = false;
            return;
        }

        Available = true;
        _tableServiceClient = result.Value.client;
        _connectionString = result.Value.connectionString;

        var connectionString = _connectionString!;

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:StorageAccount", connectionString);
                builder.UseSetting("EncryptionKey", EncryptionKey);
                builder.UseSetting("UseMockProxy", "true");
                builder.UseSetting("ProxyUrl", "http://localhost/api/v1");
            });

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        _tableStorage = new TableStorageService(_tableServiceClient);
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Calls the /internal/cache/invalidate endpoint to flush the in-memory
    /// event and catalog caches. Call this after mutating seeded data when a
    /// previous request may have populated the cache with stale values.
    /// </summary>
    public async Task InvalidateCacheAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/cache/invalidate");
        request.Headers.Add("X-Cache-Key", EncryptionKey);
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    #region Seed helpers

    public async Task SeedEventAsync(
        string eventId,
        string ownerId,
        string catalogIds = "",
        int dailyRequestCap = 1000,
        bool active = true,
        DateTime? startTimestamp = null,
        DateTime? endTimestamp = null,
        string? eventSharedCode = null,
        bool showDefaultSamples = true)
    {
        var eventsTable = TableStorage.GetTableClient(TableNames.Events);
        await eventsTable.UpsertEntityAsync(new EventEntity
        {
            PartitionKey = eventId,
            RowKey = eventId,
            OwnerId = ownerId,
            EventCode = $"CODE-{eventId[..8]}",
            EventMarkdown = "# Test Event",
            StartTimestamp = startTimestamp ?? DateTime.UtcNow.AddHours(-1),
            EndTimestamp = endTimestamp ?? DateTime.UtcNow.AddHours(1),
            TimeZoneOffset = 0,
            TimeZoneLabel = "UTC",
            OrganizerName = $"Org-{ownerId}",
            OrganizerEmail = $"{ownerId}@example.com",
            MaxTokenCap = 500,
            DailyRequestCap = dailyRequestCap,
            Active = active,
            ShowDefaultSamples = showDefaultSamples,
            CatalogIds = catalogIds,
            EventSharedCode = eventSharedCode
        }, TableUpdateMode.Replace);
    }

    public async Task SeedCatalogAsync(
        string catalogId,
        string deploymentName,
        string modelType,
        bool active = true,
        bool useManagedIdentity = false,
        string endpointUrl = "https://fake-endpoint.example.com",
        string endpointKey = "fake-api-key"
    )
    {
        var catalogTable = TableStorage.GetTableClient(TableNames.Catalogs);
        await catalogTable.UpsertEntityAsync(new CatalogEntity
        {
            PartitionKey = catalogId,
            RowKey = catalogId,
            OwnerId = "owner",
            DeploymentName = deploymentName,
            Active = active,
            ModelType = modelType,
            Location = "eastus",
            FriendlyName = deploymentName,
            EncryptedEndpointUrl = Encryption.Encrypt(endpointUrl),
            EncryptedEndpointKey = Encryption.Encrypt(endpointKey),
            UseManagedIdentity = useManagedIdentity,
            UseMaxCompletionTokens = false
        }, TableUpdateMode.Replace);
    }

    public async Task<string> SeedAttendeeAsync(string userId, string eventId)
    {
        var attendeeTable = TableStorage.GetTableClient(TableNames.Attendees);
        var lookupTable = TableStorage.GetTableClient(TableNames.AttendeeLookup);

        var apiKey = Guid.NewGuid().ToString();

        await attendeeTable.UpsertEntityAsync(new AttendeeEntity
        {
            PartitionKey = eventId,
            RowKey = userId,
            ApiKey = apiKey,
            Active = true
        }, TableUpdateMode.Replace);

        await lookupTable.UpsertEntityAsync(new AttendeeLookupEntity
        {
            PartitionKey = AttendeeLookupEntity.GetPartitionKey(apiKey),
            RowKey = apiKey,
            EventId = eventId,
            UserId = userId,
            Active = true
        }, TableUpdateMode.Replace);

        return apiKey;
    }

    #endregion

    #region Azurite connection

    private string? _connectionString;

    #endregion
}

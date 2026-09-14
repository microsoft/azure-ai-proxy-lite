using Azure.Data.Tables;
using AzureAIProxy.Management.Components.EventManagement;
using AzureAIProxy.Management.Services;
using AzureAIProxy.Shared.Database;
using AzureAIProxy.Shared.Services;
using AzureAIProxy.Shared.TableStorage;
using AzureAIProxy.Tests.Fixtures;
using AzureAIProxy.Tests.TestDoubles;

namespace AzureAIProxy.Tests.Admin;

public class EventServiceTests : IAsyncLifetime
{
    private (TableServiceClient client, string connectionString)? _azurite;
    private ITableStorageService _tableStorage = null!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];
    private string OwnerId => $"admin-owner-{_runId}";

    public async Task InitializeAsync()
    {
        _azurite = await AzuriteHelper.TryCreateLocalAzuriteClientWithConnectionStringAsync();
        if (_azurite is not null)
            _tableStorage = new TableStorageService(_azurite.Value.client);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private EventService CreateService(string? userId = null) =>
        new(new StubManagementAuthService(userId ?? OwnerId), _tableStorage, new NoopCacheInvalidationService());

    private EventEditorModel CreateModel(string name = "Test Event") => new()
    {
        Name = name,
        Description = "Test event description",
        Start = DateTime.UtcNow.AddHours(-1),
        End = DateTime.UtcNow.AddHours(24),
        OrganizerName = "Test Organizer",
        OrganizerEmail = "test@example.com",
        MaxTokenCap = 4096,
        DailyRequestCap = 1024,
        Active = true,
        SelectedTimeZone = TimeZoneInfo.Utc
    };

    [SkippableFact]
    public async Task CreateEvent_ReturnsEventWithId()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var service = CreateService();

        var result = await service.CreateEventAsync(CreateModel("My Workshop"));

        Assert.NotNull(result);
        Assert.NotEmpty(result.EventId);
        Assert.Equal("My Workshop", result.EventCode);
        Assert.Equal(OwnerId, result.OwnerId);
        Assert.True(result.ShowDefaultSamples);
    }

    [SkippableFact]
    public async Task GetEvent_ReturnsCreatedEvent()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var service = CreateService();

        var created = await service.CreateEventAsync(CreateModel());

        var fetched = await service.GetEventAsync(created!.EventId);

        Assert.NotNull(fetched);
        Assert.Equal(created.EventId, fetched.EventId);
    }

    [SkippableFact]
    public async Task GetEvent_WrongOwner_ReturnsNull()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var ownerA = CreateService($"owner-A-{_runId}");
        var ownerB = CreateService($"owner-B-{_runId}");

        var created = await ownerA.CreateEventAsync(CreateModel());

        var result = await ownerB.GetEventAsync(created!.EventId);

        Assert.Null(result);
    }

    [SkippableFact]
    public async Task GetOwnerEvents_ReturnsOnlyOwnedEvents()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var ownerA = CreateService($"owner-list-A-{_runId}");
        var ownerB = CreateService($"owner-list-B-{_runId}");

        await ownerA.CreateEventAsync(CreateModel("Event A"));
        await ownerB.CreateEventAsync(CreateModel("Event B"));

        var eventsA = (await ownerA.GetOwnerEventsAsync()).ToList();
        var eventsB = (await ownerB.GetOwnerEventsAsync()).ToList();

        Assert.Contains(eventsA, e => e.EventCode == "Event A");
        Assert.DoesNotContain(eventsA, e => e.EventCode == "Event B");
        Assert.Contains(eventsB, e => e.EventCode == "Event B");
    }

    [SkippableFact]
    public async Task UpdateEvent_ChangesFields()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var service = CreateService();

        var created = await service.CreateEventAsync(CreateModel("Original"));
        var model = CreateModel("Updated");
        model.Active = false;
        model.ShowDefaultSamples = false;

        var updated = await service.UpdateEventAsync(created!.EventId, model);

        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.EventCode);
        Assert.False(updated.Active);
        Assert.False(updated.ShowDefaultSamples);
    }

    [SkippableFact]
    public async Task UpdateEvent_WrongOwner_ReturnsNull()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var ownerA = CreateService($"owner-upd-A-{_runId}");
        var ownerB = CreateService($"owner-upd-B-{_runId}");

        var created = await ownerA.CreateEventAsync(CreateModel());

        var result = await ownerB.UpdateEventAsync(created!.EventId, CreateModel("Hacked"));

        Assert.Null(result);
    }

    [SkippableFact]
    public async Task DeleteEvent_RemovesEvent()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var service = CreateService($"owner-del-{_runId}");

        var created = await service.CreateEventAsync(CreateModel());
        await service.DeleteEventAsync(created!.EventId);

        var fetched = await service.GetEventAsync(created.EventId);

        Assert.Null(fetched);
    }

    [SkippableFact]
    public async Task DeleteEvent_BlockedWhenAttendeesExist()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var service = CreateService($"owner-del-block-{_runId}");

        var created = await service.CreateEventAsync(CreateModel());

        // Seed an attendee directly
        var attendeeTable = _tableStorage.GetTableClient(TableNames.Attendees);
        await attendeeTable.UpsertEntityAsync(new AttendeeEntity
        {
            PartitionKey = created!.EventId,
            RowKey = "some-user",
            ApiKey = Guid.NewGuid().ToString(),
            Active = true
        });

        await service.DeleteEventAsync(created.EventId);

        // Event should still exist
        var fetched = await service.GetEventAsync(created.EventId);
        Assert.NotNull(fetched);
    }

    [SkippableFact]
    public async Task UpdateModelsForEvent_LinksCatalogs()
    {
        Skip.If(_azurite is null, "Azurite not available");
        var userId = $"owner-models-{_runId}";
        var service = CreateService(userId);
        var encryption = new EncryptionService("dev-encryption-key-change-in-production");

        var created = await service.CreateEventAsync(CreateModel());

        // Create a catalog owned by the same user
        var catalogId = Guid.NewGuid();
        var catalogTable = _tableStorage.GetTableClient(TableNames.Catalogs);
        await catalogTable.UpsertEntityAsync(new CatalogEntity
        {
            PartitionKey = catalogId.ToString(),
            RowKey = catalogId.ToString(),
            OwnerId = userId,
            DeploymentName = "gpt-4o",
            Active = true,
            ModelType = ModelType.Foundry_Model.ToStorageString(),
            Location = "eastus",
            FriendlyName = "GPT-4o",
            EncryptedEndpointUrl = encryption.Encrypt("https://fake.example.com"),
            EncryptedEndpointKey = encryption.Encrypt("fake-key"),
            UseManagedIdentity = false,
            UseMaxCompletionTokens = false
        });

        await service.UpdateModelsForEventAsync(created!.EventId, [catalogId]);

        var fetched = await service.GetEventAsync(created.EventId);
        Assert.Single(fetched!.Catalogs);
        Assert.Equal("gpt-4o", fetched.Catalogs[0].DeploymentName);
    }
}

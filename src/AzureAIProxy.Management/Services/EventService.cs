using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using AzureAIProxy.Management.Components.EventManagement;
using AzureAIProxy.Shared.Database;
using AzureAIProxy.Shared.Services;
using AzureAIProxy.Shared.TableStorage;

namespace AzureAIProxy.Management.Services;

public class EventService(IAuthService authService, ITableStorageService tableStorage, ICacheInvalidationService cacheInvalidation) : IEventService
{
    public async Task<Event?> CreateEventAsync(EventEditorModel model)
    {
        if (string.IsNullOrEmpty(model.EventSharedCode)) model.EventSharedCode = null;

        string userId = await authService.GetCurrentUserIdAsync();

        var guidString = $"{Guid.NewGuid()}{Guid.NewGuid()}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(guidString));
        var hashString = Convert.ToHexStringLower(hashBytes);
        var eventId = $"{hashString[..4]}-{hashString[4..8]}";

        var eventEntity = new EventEntity
        {
            PartitionKey = eventId,
            RowKey = eventId,
            OwnerId = userId,
            EventCode = model.Name!,
            EventSharedCode = model.EventSharedCode,
            EventMarkdown = model.Description!,
            StartTimestamp = DateTime.SpecifyKind(model.Start!.Value, DateTimeKind.Utc),
            EndTimestamp = DateTime.SpecifyKind(model.End!.Value, DateTimeKind.Utc),
            TimeZoneOffset = (int)model.SelectedTimeZone!.BaseUtcOffset.TotalMinutes,
            TimeZoneLabel = model.SelectedTimeZone!.Id,
            OrganizerName = model.OrganizerName!,
            OrganizerEmail = model.OrganizerEmail!,
            MaxTokenCap = model.MaxTokenCap,
            DailyRequestCap = model.DailyRequestCap,
            Active = model.Active,
            ShowDefaultSamples = model.ShowDefaultSamples,
            CatalogIds = ""
        };

        var eventsTable = tableStorage.GetTableClient(TableNames.Events);
        await eventsTable.AddEntityAsync(eventEntity);

        var ownerEventsTable = tableStorage.GetTableClient(TableNames.OwnerEvents);
        await ownerEventsTable.AddEntityAsync(new OwnerEventEntity
        {
            PartitionKey = userId,
            RowKey = eventId,
            Creator = true
        });

        return MapToEvent(eventEntity);
    }

    public async Task<Event?> GetEventAsync(string id)
    {
        string userId = await authService.GetCurrentUserIdAsync();
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);

        EventEntity? evt;
        try
        {
            var response = await eventsTable.GetEntityAsync<EventEntity>(id, id);
            evt = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        if (evt.OwnerId != userId)
            return null;

        var result = MapToEvent(evt);

        // Load catalogs from inlined IDs
        var catalogTable = tableStorage.GetTableClient(TableNames.Catalogs);
        var catalogIds = string.IsNullOrEmpty(evt.CatalogIds) ? [] : evt.CatalogIds.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var catalogId in catalogIds)
        {
            try
            {
                var catResponse = await catalogTable.GetEntityAsync<CatalogEntity>(catalogId, catalogId);
                var catalog = catResponse.Value;
                result.Catalogs.Add(new OwnerCatalog
                {
                    CatalogId = Guid.Parse(catalogId),
                    OwnerId = catalog.OwnerId,
                    DeploymentName = catalog.DeploymentName,
                    Active = catalog.Active,
                    ModelType = ModelTypeExtensions.FromStorageString(catalog.ModelType),
                    Location = catalog.Location,
                    FriendlyName = catalog.FriendlyName
                });
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        return result;
    }

    public async Task<IEnumerable<Event>> GetOwnerEventsAsync()
    {
        string userId = await authService.GetCurrentUserIdAsync();

        var ownerEventsTable = tableStorage.GetTableClient(TableNames.OwnerEvents);
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);
        var catalogTable = tableStorage.GetTableClient(TableNames.Catalogs);
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);

        var eventIds = new List<string>();
        await foreach (var oe in ownerEventsTable.QueryAsync<OwnerEventEntity>(e => e.PartitionKey == userId))
        {
            eventIds.Add(oe.RowKey);
        }

        var results = new List<Event>();
        foreach (var eventId in eventIds)
        {
            try
            {
                var evtResponse = await eventsTable.GetEntityAsync<EventEntity>(eventId, eventId);
                var evt = evtResponse.Value;
                var eventObj = MapToEvent(evt);

                // Load catalogs from inlined IDs
                var catalogIds = string.IsNullOrEmpty(evt.CatalogIds) ? [] : evt.CatalogIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var catalogId in catalogIds)
                {
                    try
                    {
                        var catResponse = await catalogTable.GetEntityAsync<CatalogEntity>(catalogId, catalogId);
                        var catalog = catResponse.Value;
                        eventObj.Catalogs.Add(new OwnerCatalog
                        {
                            CatalogId = Guid.Parse(catalogId),
                            OwnerId = catalog.OwnerId,
                            DeploymentName = catalog.DeploymentName,
                            Active = catalog.Active,
                            ModelType = ModelTypeExtensions.FromStorageString(catalog.ModelType),
                            Location = catalog.Location,
                            FriendlyName = catalog.FriendlyName
                        });
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }

                // Load attendees
                await foreach (var att in attendeeTable.QueryAsync<AttendeeEntity>(e => e.PartitionKey == eventId))
                {
                    eventObj.EventAttendees.Add(new EventAttendee
                    {
                        EventId = eventId,
                        UserId = att.RowKey,
                        ApiKey = att.ApiKey,
                        Active = att.Active
                    });
                }

                results.Add(eventObj);
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        return results
            .OrderByDescending(e => e.Active)
            .ThenByDescending(e => e.EndTimestamp);
    }

    public async Task<Event?> UpdateEventAsync(string id, EventEditorModel model)
    {
        string userId = await authService.GetCurrentUserIdAsync();
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);

        EventEntity? evt;
        try
        {
            var response = await eventsTable.GetEntityAsync<EventEntity>(id, id);
            evt = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        if (evt.OwnerId != userId)
            return null;

        if (string.IsNullOrEmpty(model.EventSharedCode)) model.EventSharedCode = null;

        evt.EventCode = model.Name!;
        evt.EventSharedCode = model.EventSharedCode;
        evt.EventMarkdown = model.Description!;
        evt.StartTimestamp = DateTime.SpecifyKind(model.Start!.Value, DateTimeKind.Utc);
        evt.EndTimestamp = DateTime.SpecifyKind(model.End!.Value, DateTimeKind.Utc);
        evt.OrganizerEmail = model.OrganizerEmail!;
        evt.OrganizerName = model.OrganizerName!;
        evt.Active = model.Active;
        evt.ShowDefaultSamples = model.ShowDefaultSamples;
        evt.MaxTokenCap = model.MaxTokenCap;
        evt.DailyRequestCap = model.DailyRequestCap;
        evt.TimeZoneLabel = model.SelectedTimeZone!.Id;
        evt.TimeZoneOffset = (int)model.SelectedTimeZone.BaseUtcOffset.TotalMinutes;

        await eventsTable.UpdateEntityAsync(evt, evt.ETag, TableUpdateMode.Replace);
        await cacheInvalidation.InvalidateAllCachesAsync();

        return MapToEvent(evt);
    }

    public async Task UpdateModelsForEventAsync(string id, IEnumerable<Guid> modelIds)
    {
        string userId = await authService.GetCurrentUserIdAsync();
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);

        try
        {
            var response = await eventsTable.GetEntityAsync<EventEntity>(id, id);
            var evt = response.Value;

            if (evt.OwnerId != userId)
                return;

            // Verify all catalogs belong to the current user
            var catalogTable = tableStorage.GetTableClient(TableNames.Catalogs);
            var validatedIds = new List<string>();
            foreach (var modelId in modelIds)
            {
                var catId = modelId.ToString();
                try
                {
                    var catResponse = await catalogTable.GetEntityAsync<CatalogEntity>(catId, catId);
                    if (catResponse.Value.OwnerId == userId)
                        validatedIds.Add(catId);
                }
                catch (RequestFailedException catEx) when (catEx.Status == 404) { }
            }

            evt.CatalogIds = string.Join(",", validatedIds);
            await eventsTable.UpdateEntityAsync(evt, evt.ETag, TableUpdateMode.Replace);
            await cacheInvalidation.InvalidateAllCachesAsync();
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    public async Task DeleteEventAsync(string id)
    {
        string userId = await authService.GetCurrentUserIdAsync();
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);

        // Check for attendees
        await foreach (var _ in attendeeTable.QueryAsync<AttendeeEntity>(e => e.PartitionKey == id))
        {
            return; // Block deletion if attendees exist
        }

        try
        {
            var response = await eventsTable.GetEntityAsync<EventEntity>(id, id);
            var evt = response.Value;

            if (evt.OwnerId != userId)
                return;

            await eventsTable.DeleteEntityAsync(id, id);

            // Clean up owner-event mapping
            var oeTable = tableStorage.GetTableClient(TableNames.OwnerEvents);
            try { await oeTable.DeleteEntityAsync(evt.OwnerId, id); }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            await cacheInvalidation.InvalidateAllCachesAsync();
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }

    private static Event MapToEvent(EventEntity entity) => new()
    {
        EventId = entity.PartitionKey,
        OwnerId = entity.OwnerId,
        EventCode = entity.EventCode,
        EventSharedCode = entity.EventSharedCode,
        EventMarkdown = entity.EventMarkdown,
        StartTimestamp = entity.StartTimestamp,
        EndTimestamp = entity.EndTimestamp,
        TimeZoneOffset = entity.TimeZoneOffset,
        TimeZoneLabel = entity.TimeZoneLabel,
        OrganizerName = entity.OrganizerName,
        OrganizerEmail = entity.OrganizerEmail,
        MaxTokenCap = entity.MaxTokenCap,
        DailyRequestCap = entity.DailyRequestCap,
        Active = entity.Active,
        ShowDefaultSamples = entity.ShowDefaultSamples
    };
}

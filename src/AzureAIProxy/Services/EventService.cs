using Azure;
using AzureAIProxy.Models;
using AzureAIProxy.Shared.Services;
using AzureAIProxy.Shared.TableStorage;
using Microsoft.Extensions.Caching.Memory;

namespace AzureAIProxy.Services;

public class EventService(ITableStorageService tableStorage, IMemoryCache memoryCache, IEventCacheService eventCache) : IEventService
{
    const string EventRegistrationCacheKey = "event+registration+info";

    public async Task<EventRegistration?> GetEventRegistrationInfoAsync(string eventId)
    {
        var cacheKey = $"{EventRegistrationCacheKey}+{eventId}";
        if (memoryCache.TryGetValue(cacheKey, out EventRegistration? cachedContext))
            return cachedContext;

        var table = tableStorage.GetTableClient(TableNames.Events);

        EventEntity? evt;
        try
        {
            var response = await table.GetEntityAsync<EventEntity>(eventId, eventId);
            evt = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var result = new EventRegistration
        {
            EventId = evt.PartitionKey,
            EventCode = evt.EventCode,
            OrganizerName = evt.OrganizerName,
            EventMarkdown = evt.EventMarkdown,
            StartTimestamp = evt.StartTimestamp,
            EndTimestamp = evt.EndTimestamp,
            TimeZoneOffset = evt.TimeZoneOffset,
            TimeZoneLabel = evt.TimeZoneLabel,
            ShowDefaultSamples = evt.ShowDefaultSamples
        };

        memoryCache.Set(cacheKey, result, eventCache.GetCacheEntryOptions());
        return result;
    }
}

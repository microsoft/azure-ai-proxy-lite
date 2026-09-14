using AzureAIProxy.Shared.Services;
using AzureAIProxy.Shared.TableStorage;
using AzureAIProxy.Shared.Database;

namespace AzureAIProxy.Management.Services;

public class EventMetricsData
{
    public string EventId { get; set; } = null!;
    public DateTime DateStamp { get; set; }
    public string Resource { get; set; } = null!;
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public long Requests { get; set; }
}

public class EventChartData
{
    public DateTime DateStamp { get; set; }
    public long Count { get; set; }
}


public class MetricService(ITableStorageService tableStorage) : IMetricService
{
    public async Task<List<EventMetricsData>> GetEventMetricsAsync(string eventId)
    {
        var table = tableStorage.GetTableClient(TableNames.Metrics);
        var results = new List<EventMetricsData>();

        await foreach (var entity in table.QueryAsync<MetricEntity>(e => e.PartitionKey == eventId))
        {
            results.Add(new EventMetricsData
            {
                EventId = entity.PartitionKey,
                DateStamp = DateTime.Parse(entity.DateStamp),
                Resource = entity.Resource,
                PromptTokens = entity.PromptTokens,
                CompletionTokens = entity.CompletionTokens,
                TotalTokens = entity.TotalTokens,
                Requests = entity.RequestCount
            });
        }

        return results.OrderByDescending(m => m.Requests).ToList();
    }

    public async Task<(int attendeeCount, int requestCount)> GetAttendeeMetricsAsync(string eventId)
    {
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);
        var metricTable = tableStorage.GetTableClient(TableNames.Metrics);

        int userCount = 0;
        await foreach (var _ in attendeeTable.QueryAsync<AttendeeEntity>(e => e.PartitionKey == eventId))
        {
            userCount++;
        }

        long totalRequests = 0;
        await foreach (var entity in metricTable.QueryAsync<MetricEntity>(e => e.PartitionKey == eventId))
        {
            totalRequests += entity.RequestCount;
        }

        return (userCount, (int)totalRequests);
    }

    public async Task<Event?> GetEventForReportAsync(string eventId)
    {
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);
        var catalogTable = tableStorage.GetTableClient(TableNames.Catalogs);

        EventEntity evt;
        try
        {
            var response = await eventsTable.GetEntityAsync<EventEntity>(eventId, eventId);
            evt = response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var result = new Event
        {
            EventId = evt.PartitionKey,
            OwnerId = evt.OwnerId,
            EventCode = evt.EventCode,
            EventSharedCode = evt.EventSharedCode,
            EventMarkdown = evt.EventMarkdown,
            StartTimestamp = evt.StartTimestamp,
            EndTimestamp = evt.EndTimestamp,
            TimeZoneOffset = evt.TimeZoneOffset,
            TimeZoneLabel = evt.TimeZoneLabel,
            OrganizerName = evt.OrganizerName,
            OrganizerEmail = evt.OrganizerEmail,
            MaxTokenCap = evt.MaxTokenCap,
            DailyRequestCap = evt.DailyRequestCap,
            Active = evt.Active,
            ShowDefaultSamples = evt.ShowDefaultSamples
        };

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
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }
        }

        return result;
    }

    public async Task<List<EventRegistrations>> GetAllEventsAsync()
    {
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);

        var results = new List<EventRegistrations>();

        await foreach (var evt in eventsTable.QueryAsync<EventEntity>())
        {
            int attendeeCount = 0;
            await foreach (var _ in attendeeTable.QueryAsync<AttendeeEntity>(e => e.PartitionKey == evt.PartitionKey))
            {
                attendeeCount++;
            }

            results.Add(new EventRegistrations
            {
                EventId = evt.PartitionKey,
                EventName = evt.EventCode,
                OrganizerName = evt.OrganizerName,
                StartDate = evt.StartTimestamp,
                EndDate = evt.EndTimestamp,
                Registered = attendeeCount
            });
        }

        return results;
    }

    public async Task<List<EventChartData>> GetActiveRegistrationsAsync(string eventId)
    {
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);
        var requestTable = tableStorage.GetTableClient(TableNames.AttendeeRequests);

        // Get all attendees for this event
        var attendeeKeys = new List<string>();
        await foreach (var attendee in attendeeTable.QueryAsync<AttendeeEntity>(e => e.PartitionKey == eventId))
        {
            attendeeKeys.Add(attendee.ApiKey);
        }

        // For each attendee, find their earliest request date
        var firstSeenDates = new Dictionary<string, DateTime>();
        foreach (var apiKey in attendeeKeys)
        {
            DateTime? earliest = null;
            await foreach (var req in requestTable.QueryAsync<AttendeeRequestEntity>(e => e.PartitionKey == apiKey))
            {
                var date = DateTime.Parse(req.RowKey);
                if (earliest is null || date < earliest) earliest = date;
            }
            if (earliest.HasValue)
                firstSeenDates[apiKey] = earliest.Value;
        }

        // Build cumulative growth
        var growth = firstSeenDates.Values
            .GroupBy(d => d.Date)
            .OrderBy(g => g.Key)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToList();

        long cumulative = 0;
        return growth.Select(g =>
        {
            cumulative += g.Count;
            return new EventChartData { DateStamp = g.Date, Count = cumulative };
        }).ToList();
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using AzureAIProxy.Shared.Services;
using AzureAIProxy.Shared.TableStorage;

namespace AzureAIProxy.Management.Services;

public class BackupService(ITableStorageService tableStorage, IEncryptionService encryption, ICacheInvalidationService cacheInvalidation, IAuthService authService) : IBackupService
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 600_000;
    private const int KeySize = 32;
    private const byte BackupVersion = 0x01;
    public const int MinPassphraseLength = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    public async Task<BackupData> CreateBackupAsync()
    {
        var currentUserId = await authService.GetCurrentUserIdAsync();
        var backup = new BackupData
        {
            BackupTimestamp = DateTime.UtcNow
        };

        // Backup only events owned by the current user
        var ownerEventsTable = tableStorage.GetTableClient(TableNames.OwnerEvents);
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);

        var eventIds = new List<string>();
        await foreach (var oe in ownerEventsTable.QueryAsync<OwnerEventEntity>(e => e.PartitionKey == currentUserId))
        {
            eventIds.Add(oe.RowKey);
        }

        foreach (var eventId in eventIds)
        {
            try
            {
                var response = await eventsTable.GetEntityAsync<EventEntity>(eventId, eventId);
                var entity = response.Value;
                backup.Events.Add(new BackupEvent
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
                    ShowDefaultSamples = entity.ShowDefaultSamples,
                    CatalogIds = entity.CatalogIds
                });
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        // Backup only resources owned by the current user
        var catalogTable = tableStorage.GetTableClient(TableNames.Catalogs);
        await foreach (var entity in catalogTable.QueryAsync<CatalogEntity>(c => c.OwnerId == currentUserId))
        {
            string decryptedUrl;
            string decryptedKey;

            decryptedUrl = encryption.Decrypt(entity.EncryptedEndpointUrl);
            decryptedKey = string.IsNullOrWhiteSpace(entity.EncryptedEndpointKey)
                ? string.Empty
                : encryption.Decrypt(entity.EncryptedEndpointKey);

            backup.Resources.Add(new BackupResource
            {
                CatalogId = entity.PartitionKey,
                OwnerId = entity.OwnerId,
                DeploymentName = entity.DeploymentName,
                EndpointUrl = decryptedUrl,
                EndpointKey = decryptedKey,
                Active = entity.Active,
                ModelType = entity.ModelType,
                Location = entity.Location,
                FriendlyName = entity.FriendlyName,
                UseManagedIdentity = entity.UseManagedIdentity,
                UseMaxCompletionTokens = entity.UseMaxCompletionTokens
            });
        }

        // Backup metrics for each owned event
        var metricTable = tableStorage.GetTableClient(TableNames.Metrics);
        foreach (var eventId in eventIds)
        {
            await foreach (var metric in metricTable.QueryAsync<MetricEntity>(e => e.PartitionKey == eventId))
            {
                backup.Metrics.Add(new BackupMetric
                {
                    EventId = metric.PartitionKey,
                    Resource = metric.Resource,
                    DateStamp = metric.DateStamp,
                    PromptTokens = metric.PromptTokens,
                    CompletionTokens = metric.CompletionTokens,
                    TotalTokens = metric.TotalTokens,
                    RequestCount = metric.RequestCount
                });
            }
        }

        // Backup attendees for each owned event
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);
        foreach (var eventId in eventIds)
        {
            await foreach (var attendee in attendeeTable.QueryAsync<AttendeeEntity>(e => e.PartitionKey == eventId))
            {
                backup.Attendees.Add(new BackupAttendee
                {
                    EventId = attendee.PartitionKey,
                    UserId = attendee.RowKey,
                    ApiKey = attendee.ApiKey,
                    Active = attendee.Active
                });
            }
        }

        return backup;
    }

    public async Task<byte[]> CreateEncryptedBackupAsync(string passphrase)
    {
        var data = await CreateBackupAsync();
        var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        return EncryptWithPassphrase(passphrase, json);
    }

    public async Task RestoreEncryptedBackupAsync(string passphrase, Stream encryptedStream)
    {
        using var ms = new MemoryStream();
        await encryptedStream.CopyToAsync(ms);
        var plainBytes = DecryptWithPassphrase(passphrase, ms.ToArray());
        var data = JsonSerializer.Deserialize<BackupData>(plainBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Invalid backup data.");
        await RestoreBackupAsync(data);
    }

    public async Task RestoreBackupAsync(BackupData data)
    {
        // Clear all existing data first to avoid clashes
        await ClearAllDataAsync();

        var currentUserId = await authService.GetCurrentUserIdAsync();

        // Build a mapping from old catalog IDs to new catalog IDs so event CatalogIds references stay consistent
        var catalogIdMap = new Dictionary<string, string>();
        var catalogTable = tableStorage.GetTableClient(TableNames.Catalogs);

        // Restore resources first (events reference catalog IDs)
        foreach (var resource in data.Resources)
        {
            var newCatalogId = Guid.NewGuid().ToString();
            catalogIdMap[resource.CatalogId] = newCatalogId;

            await catalogTable.UpsertEntityAsync(new CatalogEntity
            {
                PartitionKey = newCatalogId,
                RowKey = newCatalogId,
                OwnerId = currentUserId,
                DeploymentName = resource.DeploymentName,
                EncryptedEndpointUrl = encryption.Encrypt(resource.EndpointUrl),
                EncryptedEndpointKey = string.IsNullOrWhiteSpace(resource.EndpointKey) ? string.Empty : encryption.Encrypt(resource.EndpointKey),
                Active = resource.Active,
                ModelType = resource.ModelType,
                Location = resource.Location,
                FriendlyName = resource.FriendlyName,
                UseManagedIdentity = resource.UseManagedIdentity,
                UseMaxCompletionTokens = resource.UseMaxCompletionTokens
            });
        }

        // Restore events with new IDs and remapped catalog references
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);
        var ownerEventsTable = tableStorage.GetTableClient(TableNames.OwnerEvents);
        var eventIdMap = new Dictionary<string, string>();

        foreach (var evt in data.Events)
        {
            var guidString = $"{Guid.NewGuid()}{Guid.NewGuid()}";
            var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(guidString));
            var hashString = Convert.ToHexStringLower(hashBytes);
            var newEventId = $"{hashString[..4]}-{hashString[4..8]}";
            eventIdMap[evt.EventId] = newEventId;

            // Remap catalog IDs in the event's CatalogIds field
            var remappedCatalogIds = string.Empty;
            if (!string.IsNullOrEmpty(evt.CatalogIds))
            {
                var oldIds = evt.CatalogIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var newIds = oldIds
                    .Where(id => catalogIdMap.ContainsKey(id))
                    .Select(id => catalogIdMap[id]);
                remappedCatalogIds = string.Join(",", newIds);
            }

            await eventsTable.UpsertEntityAsync(new EventEntity
            {
                PartitionKey = newEventId,
                RowKey = newEventId,
                OwnerId = currentUserId,
                EventCode = evt.EventCode,
                EventSharedCode = evt.EventSharedCode,
                EventMarkdown = evt.EventMarkdown,
                StartTimestamp = DateTime.SpecifyKind(evt.StartTimestamp, DateTimeKind.Utc),
                EndTimestamp = DateTime.SpecifyKind(evt.EndTimestamp, DateTimeKind.Utc),
                TimeZoneOffset = evt.TimeZoneOffset,
                TimeZoneLabel = evt.TimeZoneLabel,
                OrganizerName = evt.OrganizerName,
                OrganizerEmail = evt.OrganizerEmail,
                MaxTokenCap = evt.MaxTokenCap,
                DailyRequestCap = evt.DailyRequestCap,
                Active = evt.Active,
                ShowDefaultSamples = evt.ShowDefaultSamples,
                CatalogIds = remappedCatalogIds
            });

            await ownerEventsTable.UpsertEntityAsync(new OwnerEventEntity
            {
                PartitionKey = currentUserId,
                RowKey = newEventId,
                Creator = true
            });
        }

        // Restore metrics with remapped event IDs
        var metricTable = tableStorage.GetTableClient(TableNames.Metrics);
        foreach (var metric in data.Metrics)
        {
            var newEventId = eventIdMap.TryGetValue(metric.EventId, out var mapped) ? mapped : metric.EventId;

            await metricTable.UpsertEntityAsync(new MetricEntity
            {
                PartitionKey = newEventId,
                RowKey = $"{metric.Resource}|{metric.DateStamp}",
                Resource = metric.Resource,
                DateStamp = metric.DateStamp,
                PromptTokens = metric.PromptTokens,
                CompletionTokens = metric.CompletionTokens,
                TotalTokens = metric.TotalTokens,
                RequestCount = metric.RequestCount
            });
        }

        // Restore attendees with remapped event IDs and recreate lookup entries
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);
        var lookupTable = tableStorage.GetTableClient(TableNames.AttendeeLookup);
        foreach (var attendee in data.Attendees)
        {
            var newEventId = eventIdMap.TryGetValue(attendee.EventId, out var mapped) ? mapped : attendee.EventId;

            await attendeeTable.UpsertEntityAsync(new AttendeeEntity
            {
                PartitionKey = newEventId,
                RowKey = attendee.UserId,
                ApiKey = attendee.ApiKey,
                Active = attendee.Active
            });

            await lookupTable.UpsertEntityAsync(new AttendeeLookupEntity
            {
                PartitionKey = AttendeeLookupEntity.GetPartitionKey(attendee.ApiKey),
                RowKey = attendee.ApiKey,
                EventId = newEventId,
                UserId = attendee.UserId,
                Active = attendee.Active
            });
        }

        await cacheInvalidation.InvalidateAllCachesAsync();
    }

    public async Task ClearAllDataAsync()
    {
        var currentUserId = await authService.GetCurrentUserIdAsync();

        // Get the current user's event IDs
        var ownerEventsTable = tableStorage.GetTableClient(TableNames.OwnerEvents);
        var eventIds = new List<string>();
        await foreach (var oe in ownerEventsTable.QueryAsync<OwnerEventEntity>(e => e.PartitionKey == currentUserId))
        {
            eventIds.Add(oe.RowKey);
        }

        // Delete attendees, attendee lookups, attendee requests, metrics, and foundry agents for each owned event
        var attendeeTable = tableStorage.GetTableClient(TableNames.Attendees);
        var lookupTable = tableStorage.GetTableClient(TableNames.AttendeeLookup);
        var requestTable = tableStorage.GetTableClient(TableNames.AttendeeRequests);
        var metricTable = tableStorage.GetTableClient(TableNames.Metrics);
        var foundryTable = tableStorage.GetTableClient(TableNames.FoundryAgents);
        var eventsTable = tableStorage.GetTableClient(TableNames.Events);

        foreach (var eventId in eventIds)
        {
            // Collect attendee API keys before deleting attendees (needed for lookup + request cleanup)
            var apiKeys = new List<string>();
            await foreach (var att in attendeeTable.QueryAsync<AttendeeEntity>(e => e.PartitionKey == eventId))
            {
                apiKeys.Add(att.ApiKey);
                try { await attendeeTable.DeleteEntityAsync(att.PartitionKey, att.RowKey, att.ETag); }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }

            // Delete attendee lookups and requests by API key
            foreach (var apiKey in apiKeys)
            {
                var lookupPk = AttendeeLookupEntity.GetPartitionKey(apiKey);
                try { await lookupTable.DeleteEntityAsync(lookupPk, apiKey); }
                catch (RequestFailedException ex) when (ex.Status == 404) { }

                await foreach (var req in requestTable.QueryAsync<AttendeeRequestEntity>(e => e.PartitionKey == apiKey))
                {
                    try { await requestTable.DeleteEntityAsync(req.PartitionKey, req.RowKey, req.ETag); }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }
            }

            // Delete metrics for this event
            await foreach (var metric in metricTable.QueryAsync<MetricEntity>(e => e.PartitionKey == eventId))
            {
                try { await metricTable.DeleteEntityAsync(metric.PartitionKey, metric.RowKey, metric.ETag); }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
            }

            // Delete foundry agents for attendees of this event
            foreach (var apiKey in apiKeys)
            {
                await foreach (var fa in foundryTable.QueryAsync<TableEntity>(e => e.PartitionKey == apiKey, select: new[] { "PartitionKey", "RowKey" }))
                {
                    try { await foundryTable.DeleteEntityAsync(fa.PartitionKey, fa.RowKey, fa.ETag); }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }
            }

            // Delete the event itself
            try { await eventsTable.DeleteEntityAsync(eventId, eventId); }
            catch (RequestFailedException ex) when (ex.Status == 404) { }

            // Delete the owner-event mapping
            try { await ownerEventsTable.DeleteEntityAsync(currentUserId, eventId); }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        // Delete catalogs owned by current user
        var catalogTable = tableStorage.GetTableClient(TableNames.Catalogs);
        await foreach (var catalog in catalogTable.QueryAsync<CatalogEntity>(c => c.OwnerId == currentUserId))
        {
            try { await catalogTable.DeleteEntityAsync(catalog.PartitionKey, catalog.RowKey, catalog.ETag); }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
        }

        await cacheInvalidation.InvalidateAllCachesAsync();
    }

    // Format: [version(1)] [salt(16)] [nonce(12)] [tag(16)] [ciphertext(N)]
    private static byte[] EncryptWithPassphrase(string passphrase, byte[] plainBytes)
    {
        ValidatePassphrase(passphrase);

        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[1 + SaltSize + NonceSize + TagSize + cipherBytes.Length];
        result[0] = BackupVersion;
        var span = result.AsSpan();
        salt.CopyTo(span.Slice(1, SaltSize));
        nonce.CopyTo(span.Slice(1 + SaltSize, NonceSize));
        tag.CopyTo(span.Slice(1 + SaltSize + NonceSize, TagSize));
        cipherBytes.CopyTo(span[(1 + SaltSize + NonceSize + TagSize)..]);

        return result;
    }

    private static byte[] DecryptWithPassphrase(string passphrase, byte[] fullBytes)
    {
        ValidatePassphrase(passphrase);

        const int headerSize = 1 + SaltSize + NonceSize + TagSize;
        if (fullBytes.Length < headerSize || fullBytes[0] != BackupVersion)
            throw new CryptographicException("Invalid or unsupported encrypted backup payload.");

        ReadOnlySpan<byte> fullSpan = fullBytes;
        var salt = fullSpan.Slice(1, SaltSize);
        var nonce = fullSpan.Slice(1 + SaltSize, NonceSize);
        var tag = fullSpan.Slice(1 + SaltSize + NonceSize, TagSize);
        var cipherBytes = fullSpan[headerSize..];
        var plainBytes = new byte[cipherBytes.Length];

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return plainBytes;
    }

    private static void ValidatePassphrase(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        if (passphrase.Length < MinPassphraseLength)
            throw new ArgumentException($"Passphrase must be at least {MinPassphraseLength} characters.", nameof(passphrase));
    }
}

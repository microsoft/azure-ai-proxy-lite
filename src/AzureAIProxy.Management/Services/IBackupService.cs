namespace AzureAIProxy.Management.Services;

public interface IBackupService
{
    Task<BackupData> CreateBackupAsync();
    Task<byte[]> CreateEncryptedBackupAsync(string passphrase);
    Task RestoreBackupAsync(BackupData data);
    Task RestoreEncryptedBackupAsync(string passphrase, Stream encryptedStream);
    Task ClearAllDataAsync();
}

public class BackupData
{
    public DateTime BackupTimestamp { get; set; }
    public List<BackupEvent> Events { get; set; } = [];
    public List<BackupResource> Resources { get; set; } = [];
    public List<BackupMetric> Metrics { get; set; } = [];
    public List<BackupAttendee> Attendees { get; set; } = [];
}

public class BackupEvent
{
    public string EventId { get; set; } = null!;
    public string OwnerId { get; set; } = null!;
    public string EventCode { get; set; } = null!;
    public string? EventSharedCode { get; set; }
    public string EventMarkdown { get; set; } = null!;
    public DateTime StartTimestamp { get; set; }
    public DateTime EndTimestamp { get; set; }
    public int TimeZoneOffset { get; set; }
    public string TimeZoneLabel { get; set; } = null!;
    public string OrganizerName { get; set; } = null!;
    public string OrganizerEmail { get; set; } = null!;
    public int MaxTokenCap { get; set; }
    public int DailyRequestCap { get; set; }
    public bool Active { get; set; }
    public bool ShowDefaultSamples { get; set; } = true;
    public string CatalogIds { get; set; } = "";
}

public class BackupResource
{
    public string CatalogId { get; set; } = null!;
    public string OwnerId { get; set; } = null!;
    public string DeploymentName { get; set; } = null!;
    public string EndpointUrl { get; set; } = null!;
    public string EndpointKey { get; set; } = null!;
    public bool Active { get; set; }
    public string ModelType { get; set; } = null!;
    public string Location { get; set; } = null!;
    public string FriendlyName { get; set; } = null!;
    public bool UseManagedIdentity { get; set; }
    public bool UseMaxCompletionTokens { get; set; }
}

public class BackupMetric
{
    public string EventId { get; set; } = null!;
    public string Resource { get; set; } = null!;
    public string DateStamp { get; set; } = null!;
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public long RequestCount { get; set; }
}

public class BackupAttendee
{
    public string EventId { get; set; } = null!;
    public string UserId { get; set; } = null!;
    public string ApiKey { get; set; } = null!;
    public bool Active { get; set; }
}

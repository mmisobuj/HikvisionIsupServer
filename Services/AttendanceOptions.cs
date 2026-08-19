namespace HikvisionIsupServer.Services;

public sealed class AttendanceOptions
{
    /// <summary>Directory for the append-only JSONL event journal.</summary>
    public string DataDirectory { get; set; } = "data/attendance";

    /// <summary>Directory for event photos delivered inline with ISAPI alarms.</summary>
    public string PhotoDirectory { get; set; } = "data/attendance/photos";

    public bool SavePhotos { get; set; } = true;

    /// <summary>Most recent events kept in memory for the query API.</summary>
    public int MaxInMemoryEvents { get; set; } = 20000;

    /// <summary>Journal days reloaded into memory at startup.</summary>
    public int LoadDays { get; set; } = 7;

    /// <summary>Keep the verbatim device payload on each record. Useful for auditing.</summary>
    public bool RetainRawPayload { get; set; } = true;

    /// <summary>
    /// Time zone used to bucket events into local dates and to interpret device timestamps
    /// that arrive without a UTC offset. Empty uses the host time zone.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>Path to the ISAPI major/minor event catalog. Relative paths resolve against the content root.</summary>
    public string EventCatalogPath { get; set; } = "config/acs-event-types.json";

    /// <summary>Bounded ingest queue. Overflow is dropped and logged rather than blocking the SDK callback.</summary>
    public int QueueCapacity { get; set; } = 10000;

    public HrmsOptions Hrms { get; set; } = new();

    public BackfillOptions Backfill { get; set; } = new();
}

public sealed class HrmsOptions
{
    /// <summary>POST target for each accepted event. Null disables forwarding.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Value for the Authorization header. Treat as a credential: supply through
    /// <c>Attendance__Hrms__AuthorizationHeader</c> rather than a committed file.
    /// </summary>
    public string? AuthorizationHeader { get; set; }

    public int TimeoutSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 5;
    public int QueueCapacity { get; set; } = 10000;
}

public sealed class BackfillOptions
{
    /// <summary>Pull events the device buffered while the server was unreachable, on every device registration.</summary>
    public bool OnDeviceOnline { get; set; }

    /// <summary>How far back an automatic backfill reaches.</summary>
    public int LookbackHours { get; set; } = 24;

    /// <summary>Results requested per ISAPI AcsEvent page.</summary>
    public int PageSize { get; set; } = 30;

    /// <summary>Safety stop so a misconfigured range cannot pull forever.</summary>
    public int MaxPages { get; set; } = 200;
}

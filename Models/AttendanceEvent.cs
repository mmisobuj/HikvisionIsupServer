namespace HikvisionIsupServer.Models;

/// <summary>
/// Where a normalised attendance record came from.
/// </summary>
public enum AttendanceSource
{
    /// <summary>ISUP alarm channel, ISAPI payload (alarm type 13).</summary>
    IsupIsapiAlarm,

    /// <summary>ISUP alarm channel, access-control payload (alarm type 11).</summary>
    IsupAcsAlarm,

    /// <summary>Pulled from the device with an ISAPI AcsEvent search.</summary>
    IsapiBackfill
}

public enum AttendanceDirection
{
    Unknown,
    In,
    Out
}

/// <summary>
/// Outcome of the authentication attempt behind the event, as classified by
/// the ISAPI major/minor event catalog.
/// </summary>
public enum AccessOutcome
{
    /// <summary>Not an authentication event (door held open, tamper, relay, ...).</summary>
    Info,
    Granted,
    Denied
}

/// <summary>
/// One normalised access-control punch. This is the record the HRMS consumes;
/// <see cref="RawPayload"/> keeps the original device message for auditing.
/// </summary>
public sealed record AttendanceEvent
{
    /// <summary>Deterministic id derived from the device id and the device event serial.</summary>
    public required string Id { get; init; }

    public required string DeviceId { get; init; }
    public string? DeviceSerial { get; init; }

    public string? EmployeeId { get; init; }
    public string? PersonName { get; init; }
    public string? CardNo { get; init; }

    /// <summary>Event time as reported by the device, normalised to an offset.</summary>
    public required DateTimeOffset EventTime { get; init; }

    /// <summary>Verbatim time string from the device, before parsing.</summary>
    public string? DeviceReportedTime { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public int MajorEventType { get; init; }
    public int SubEventType { get; init; }

    /// <summary>ISAPI event name, e.g. <c>faceVerifyPass</c>. Falls back to <c>major/minor</c>.</summary>
    public required string EventName { get; init; }

    public AccessOutcome Outcome { get; init; }

    /// <summary>Attendance state the terminal assigned, e.g. checkIn / checkOut / breakOut.</summary>
    public string? AttendanceStatus { get; init; }

    public AttendanceDirection Direction { get; init; }

    /// <summary>Verification method used, e.g. <c>face</c>, <c>card</c>, <c>fingerprint</c>.</summary>
    public string? VerificationMode { get; init; }

    public int? DoorNo { get; init; }
    public int? ReaderNo { get; init; }

    /// <summary>Per-device monotonic event serial. Primary de-duplication key.</summary>
    public long? DeviceEventSerial { get; init; }

    /// <summary>Picture URL advertised by the device (requires a picture storage server).</summary>
    public string? PictureUrl { get; init; }

    /// <summary>File names of pictures delivered inline with the event.</summary>
    public IReadOnlyList<string> Photos { get; init; } = Array.Empty<string>();

    public AttendanceSource Source { get; init; }

    public string? RawPayload { get; init; }
}

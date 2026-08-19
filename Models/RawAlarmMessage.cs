namespace HikvisionIsupServer.Models;

public enum RawAlarmFormat
{
    Unknown,
    Json,
    Xml
}

public sealed record RawAlarmPicture(string FileName, byte[] Data);

/// <summary>
/// One message off the ISUP alarm channel, copied out of native memory before the SDK
/// callback returns. Everything here is owned by the managed side.
/// </summary>
public sealed record RawAlarmMessage
{
    public required int AlarmType { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Device serial from the alarm header, used to attribute the event when the payload omits an id.</summary>
    public string? DeviceSerial { get; init; }

    public string? Payload { get; init; }
    public RawAlarmFormat Format { get; init; }

    public IReadOnlyList<RawAlarmPicture> Pictures { get; init; } = Array.Empty<RawAlarmPicture>();
}

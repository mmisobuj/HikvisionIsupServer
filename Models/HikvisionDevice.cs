namespace HikvisionIsupServer.Models;

public sealed class HikvisionDevice
{
    public string DeviceId { get; init; } = "";
    public string SerialNumber { get; init; } = "";

    /// <summary>
    /// SDK login handle for the current registration. Changes every time the device
    /// re-registers, and every ISAPI pass-through call needs the current value.
    /// </summary>
    public int LoginId { get; set; }

    public int ProtocolVersion { get; set; }
    public DateTimeOffset ConnectedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Online { get; set; }
}

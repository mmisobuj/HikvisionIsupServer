using System.Collections.Concurrent;
using HikvisionIsupServer.Models;

namespace HikvisionIsupServer.Services;

public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, HikvisionDevice> _devices = new();

    public IReadOnlyCollection<HikvisionDevice> GetAll() =>
        _devices.Values.OrderBy(x => x.DeviceId).ToArray();

    public HikvisionDevice? Find(string deviceId) =>
        _devices.TryGetValue(deviceId, out var device) ? device : null;

    /// <summary>
    /// Looks a device up by the serial the alarm channel stamps on each message, which is how
    /// an event gets attributed when its payload omits the device id.
    /// </summary>
    public HikvisionDevice? FindBySerial(string serialNumber) =>
        _devices.Values.FirstOrDefault(
            x => string.Equals(x.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Looks a device up by its current SDK login handle. Offline notifications arrive with
    /// nothing but that handle.
    /// </summary>
    public HikvisionDevice? FindByLoginId(int loginId) =>
        _devices.Values.FirstOrDefault(x => x.LoginId == loginId);

    public void Online(HikvisionDevice device) =>
        _devices.AddOrUpdate(device.DeviceId, device, (_, existing) =>
        {
            // A re-registration issues a new login handle; keeping the old one would break
            // every subsequent ISAPI pass-through call.
            existing.LoginId = device.LoginId;
            existing.ProtocolVersion = device.ProtocolVersion;
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            existing.Online = true;
            return existing;
        });

    public void Offline(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var device))
        {
            device.Online = false;
            device.LastSeenAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Records activity from an inbound event.</summary>
    public void Touch(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var device))
            device.LastSeenAt = DateTimeOffset.UtcNow;
    }
}

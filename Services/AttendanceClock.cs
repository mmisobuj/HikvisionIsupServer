using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

/// <summary>
/// The single place that decides what "local" means for attendance: which zone device
/// timestamps without an offset belong to, and which zone days are bucketed by.
/// </summary>
public sealed class AttendanceClock
{
    public AttendanceClock(ILogger<AttendanceClock> logger, IOptions<AttendanceOptions> options)
    {
        var id = options.Value.TimeZone;

        if (string.IsNullOrWhiteSpace(id))
        {
            Zone = TimeZoneInfo.Local;
        }
        else
        {
            try
            {
                Zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                logger.LogWarning(
                    "Unknown time zone {TimeZone}; falling back to the host zone {Fallback}.",
                    id, TimeZoneInfo.Local.Id);

                Zone = TimeZoneInfo.Local;
            }
        }

        logger.LogInformation("Attendance time zone: {TimeZone}", Zone.Id);
    }

    public TimeZoneInfo Zone { get; }

    /// <summary>Attaches the configured zone offset to a timestamp the device reported without one.</summary>
    public DateTimeOffset FromDeviceLocal(DateTime naive)
    {
        var unspecified = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified));
    }

    public DateTimeOffset ToLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Zone);

    public DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(ToLocal(instant).DateTime);
}

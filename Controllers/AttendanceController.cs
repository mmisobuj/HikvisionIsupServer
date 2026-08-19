using HikvisionIsupServer.Models;
using HikvisionIsupServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace HikvisionIsupServer.Controllers;

[ApiController]
[Route("api/attendance")]
public sealed class AttendanceController : ControllerBase
{
    private readonly AttendanceStore _store;
    private readonly PhotoStore _photos;
    private readonly AttendanceIngestService _ingest;
    private readonly AttendanceBackfillService _backfill;
    private readonly IsupAlarmService _alarm;
    private readonly AttendanceClock _clock;

    public AttendanceController(
        AttendanceStore store,
        PhotoStore photos,
        AttendanceIngestService ingest,
        AttendanceBackfillService backfill,
        IsupAlarmService alarm,
        AttendanceClock clock)
    {
        _store = store;
        _photos = photos;
        _ingest = ingest;
        _backfill = backfill;
        _alarm = alarm;
        _clock = clock;
    }

    /// <summary>Recent attendance events, newest first.</summary>
    [HttpGet("events")]
    public ActionResult<AttendancePage> GetEvents(
        [FromQuery] string? deviceId,
        [FromQuery] string? employeeId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] AccessOutcome? outcome,
        [FromQuery] string? attendanceStatus,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        return Ok(_store.Query(new AttendanceEventQuery
        {
            DeviceId = deviceId,
            EmployeeId = employeeId,
            From = from,
            To = to,
            Outcome = outcome,
            AttendanceStatus = attendanceStatus,
            Skip = skip,
            Take = take
        }));
    }

    [HttpGet("events/{id}")]
    public ActionResult<AttendanceEvent> GetEvent(string id)
    {
        var evt = _store.GetById(id);
        return evt is null ? NotFound() : Ok(evt);
    }

    /// <summary>Photo captured with the event, when the terminal uploaded one inline.</summary>
    [HttpGet("events/{id}/photo")]
    public IActionResult GetEventPhoto(string id, [FromQuery] int index = 0)
    {
        var evt = _store.GetById(id);

        if (evt is null)
            return NotFound();

        if (index < 0 || index >= evt.Photos.Count)
            return NotFound(new { message = "No photo was stored for this event." });

        var path = _photos.Resolve(evt.Photos[index]);

        return path is null
            ? NotFound(new { message = "The photo file is missing from the photo store." })
            : PhysicalFile(path, "image/jpeg");
    }

    /// <summary>
    /// One row per person per local day: first punch, last punch and the check-in/check-out
    /// pair when the terminal reports attendance status.
    /// </summary>
    [HttpGet("summary/daily")]
    public ActionResult<IReadOnlyList<AttendanceDaySummary>> GetDailySummary(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? employeeId)
    {
        var today = _clock.LocalDate(DateTimeOffset.UtcNow);
        var start = from ?? today;
        var end = to ?? today;

        if (end < start)
            return BadRequest(new { message = "'to' must not be earlier than 'from'." });

        return Ok(_store.Summarise(start, end, employeeId));
    }

    [HttpGet("stats")]
    public ActionResult<AttendanceStats> GetStats()
    {
        var (eventsToday, employeesToday) = _store.Today();

        return Ok(new AttendanceStats
        {
            EventsInMemory = _store.Count,
            EventsToday = eventsToday,
            DistinctEmployeesToday = employeesToday,
            TotalIngested = _store.AcceptedCount,
            DuplicatesDropped = _store.DuplicateCount,
            PayloadsIgnored = _ingest.IgnoredPayloadCount,
            QueueDrops = _ingest.QueueDropCount,
            QueueDepth = _ingest.QueueDepth,
            LastEventAt = _store.LastEventAt,
            AlarmListenerRunning = _alarm.IsRunning,
            AlarmListenerStatus = _alarm.Status
        });
    }

    /// <summary>
    /// Replays events the terminal buffered while this server was unreachable. Safe to run
    /// repeatedly: events already recorded are de-duplicated by device event serial.
    /// </summary>
    [HttpPost("backfill")]
    public ActionResult<BackfillResult> Backfill([FromBody] BackfillRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { message = "deviceId is required." });

        var to = request.To ?? DateTimeOffset.Now;
        var from = request.From ?? to.AddHours(-_backfill.Options.LookbackHours);

        if (from >= to)
            return BadRequest(new { message = "'from' must be earlier than 'to'." });

        return Ok(_backfill.Run(request.DeviceId, from, to));
    }

    public sealed record BackfillRequest(string DeviceId, DateTimeOffset? From, DateTimeOffset? To);
}

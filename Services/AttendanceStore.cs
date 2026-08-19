using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

/// <summary>
/// Keeps recent attendance events queryable in memory and appends every accepted event to a
/// per-day JSONL journal.
/// </summary>
/// <remarks>
/// The journal is the local record of what the device sent, not the system of record: the
/// HRMS database is. It exists so events survive a restart, so a failed webhook delivery can
/// be replayed, and so a disputed punch can be traced back to the raw device payload.
/// </remarks>
public sealed class AttendanceStore
{
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<string, AttendanceEvent> _byId = new();
    private readonly LinkedList<AttendanceEvent> _recent = new();
    private readonly Lock _sync = new();

    private readonly AttendanceOptions _options;
    private readonly AttendanceClock _clock;
    private readonly ILogger<AttendanceStore> _logger;
    private readonly string _root;

    private long _accepted;
    private long _duplicates;

    public AttendanceStore(
        IOptions<AttendanceOptions> options,
        AttendanceClock clock,
        IHostEnvironment environment,
        ILogger<AttendanceStore> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;

        _root = Path.IsPathRooted(_options.DataDirectory)
            ? _options.DataDirectory
            : Path.Combine(environment.ContentRootPath, _options.DataDirectory);

        Directory.CreateDirectory(_root);
        LoadRecentJournals();
    }

    public long AcceptedCount => Interlocked.Read(ref _accepted);

    public long DuplicateCount => Interlocked.Read(ref _duplicates);

    public DateTimeOffset? LastEventAt { get; private set; }

    /// <summary>
    /// Adds an event unless its id was already recorded. Returns false for a duplicate, which
    /// is expected whenever a backfill overlaps events that already arrived live.
    /// </summary>
    public bool Add(AttendanceEvent evt)
    {
        if (!_byId.TryAdd(evt.Id, evt))
        {
            Interlocked.Increment(ref _duplicates);
            return false;
        }

        lock (_sync)
        {
            _recent.AddLast(evt);

            while (_recent.Count > _options.MaxInMemoryEvents && _recent.First is { } oldest)
            {
                _recent.RemoveFirst();
                _byId.TryRemove(oldest.Value.Id, out _);
            }
        }

        Interlocked.Increment(ref _accepted);
        LastEventAt = evt.ReceivedAt;

        Append(evt);
        return true;
    }

    public AttendanceEvent? GetById(string id) =>
        _byId.TryGetValue(id, out var evt) ? evt : null;

    public AttendancePage Query(AttendanceEventQuery query)
    {
        AttendanceEvent[] snapshot;

        lock (_sync)
        {
            snapshot = _recent.ToArray();
        }

        var filtered = snapshot
            .Where(e => query.DeviceId is null ||
                        string.Equals(e.DeviceId, query.DeviceId, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.EmployeeId is null ||
                        string.Equals(e.EmployeeId, query.EmployeeId, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.From is null || e.EventTime >= query.From)
            .Where(e => query.To is null || e.EventTime <= query.To)
            .Where(e => query.Outcome is null || e.Outcome == query.Outcome)
            .Where(e => query.AttendanceStatus is null ||
                        string.Equals(e.AttendanceStatus, query.AttendanceStatus, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.EventTime)
            .ToArray();

        var take = Math.Clamp(query.Take, 1, 1000);
        var skip = Math.Max(query.Skip, 0);

        return new AttendancePage(
            filtered.Length,
            skip,
            take,
            filtered.Skip(skip).Take(take).ToArray());
    }

    /// <summary>
    /// Collapses granted punches into one row per person per local day. Terminals configured
    /// for attendance report a check-in/check-out status; where they do not, the first and
    /// last punch of the day are all there is to go on.
    /// </summary>
    public IReadOnlyList<AttendanceDaySummary> Summarise(
        DateOnly from,
        DateOnly to,
        string? employeeId)
    {
        AttendanceEvent[] snapshot;

        lock (_sync)
        {
            snapshot = _recent.ToArray();
        }

        return snapshot
            .Where(e => e.Outcome == AccessOutcome.Granted && e.EmployeeId is not null)
            .Where(e => employeeId is null ||
                        string.Equals(e.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
            .Select(e => (Event: e, Date: _clock.LocalDate(e.EventTime)))
            .Where(x => x.Date >= from && x.Date <= to)
            .GroupBy(x => (x.Event.EmployeeId!, x.Date))
            .Select(group =>
            {
                var ordered = group.OrderBy(x => x.Event.EventTime).Select(x => x.Event).ToArray();

                return new AttendanceDaySummary
                {
                    Date = group.Key.Item2,
                    EmployeeId = group.Key.Item1,
                    PersonName = ordered.Select(e => e.PersonName).FirstOrDefault(n => n is not null),
                    FirstPunch = ordered[0].EventTime,
                    LastPunch = ordered[^1].EventTime,
                    CheckIn = ordered
                        .FirstOrDefault(e => e.Direction == AttendanceDirection.In)?.EventTime,
                    CheckOut = ordered
                        .LastOrDefault(e => e.Direction == AttendanceDirection.Out)?.EventTime,
                    PunchCount = ordered.Length,
                    DeviceIds = ordered.Select(e => e.DeviceId).Distinct().ToArray()
                };
            })
            .OrderBy(s => s.Date)
            .ThenBy(s => s.EmployeeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public (int EventsToday, int EmployeesToday) Today()
    {
        var today = _clock.LocalDate(DateTimeOffset.UtcNow);

        AttendanceEvent[] snapshot;

        lock (_sync)
        {
            snapshot = _recent.ToArray();
        }

        var events = snapshot.Where(e => _clock.LocalDate(e.EventTime) == today).ToArray();

        return (events.Length,
            events.Where(e => e.EmployeeId is not null)
                .Select(e => e.EmployeeId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    public int Count => _byId.Count;

    // ---- Journal ------------------------------------------------------------------------

    private string JournalPath(DateTimeOffset eventTime) =>
        Path.Combine(_root, $"events-{_clock.LocalDate(eventTime):yyyy-MM-dd}.jsonl");

    private void Append(AttendanceEvent evt)
    {
        var path = JournalPath(evt.EventTime);

        try
        {
            var line = JsonSerializer.Serialize(evt, JournalJson);

            lock (_sync)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not append event {EventId} to the journal {Path}.", evt.Id, path);
        }
    }

    private void LoadRecentJournals()
    {
        if (_options.LoadDays <= 0)
            return;

        var today = _clock.LocalDate(DateTimeOffset.UtcNow);
        var loaded = 0;

        for (var offset = _options.LoadDays - 1; offset >= 0; offset--)
        {
            var path = Path.Combine(_root, $"events-{today.AddDays(-offset):yyyy-MM-dd}.jsonl");

            if (!File.Exists(path))
                continue;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var evt = JsonSerializer.Deserialize<AttendanceEvent>(line, JournalJson);

                    if (evt is null || !_byId.TryAdd(evt.Id, evt))
                        continue;

                    _recent.AddLast(evt);
                    loaded++;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Skipped a malformed journal line in {Path}.", path);
                }
            }
        }

        while (_recent.Count > _options.MaxInMemoryEvents && _recent.First is { } oldest)
        {
            _recent.RemoveFirst();
            _byId.TryRemove(oldest.Value.Id, out _);
        }

        if (loaded > 0)
        {
            LastEventAt = _recent.Last?.Value.ReceivedAt;
            _logger.LogInformation(
                "Reloaded {Count} attendance events from the last {Days} day(s) of journals.",
                loaded, _options.LoadDays);
        }
    }
}

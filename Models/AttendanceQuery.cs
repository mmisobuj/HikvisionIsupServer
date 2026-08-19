namespace HikvisionIsupServer.Models;

/// <summary>Filters for <c>GET /api/attendance/events</c>.</summary>
public sealed class AttendanceEventQuery
{
    public string? DeviceId { get; set; }
    public string? EmployeeId { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }

    /// <summary>Restrict to a single outcome; null returns every event.</summary>
    public AccessOutcome? Outcome { get; set; }

    public string? AttendanceStatus { get; set; }

    public int Skip { get; set; }
    public int Take { get; set; } = 100;
}

public sealed record AttendancePage(int Total, int Skip, int Take, IReadOnlyList<AttendanceEvent> Items);

/// <summary>First punch, last punch and span for one person on one local date.</summary>
public sealed record AttendanceDaySummary
{
    public required DateOnly Date { get; init; }
    public required string EmployeeId { get; init; }
    public string? PersonName { get; init; }

    public required DateTimeOffset FirstPunch { get; init; }
    public required DateTimeOffset LastPunch { get; init; }

    /// <summary>First punch whose attendance status is a check-in, when the terminal reports one.</summary>
    public DateTimeOffset? CheckIn { get; init; }

    /// <summary>Last punch whose attendance status is a check-out, when the terminal reports one.</summary>
    public DateTimeOffset? CheckOut { get; init; }

    public required int PunchCount { get; init; }

    /// <summary>Span between the first and last punch. Not a payroll figure on its own.</summary>
    public TimeSpan Span => LastPunch - FirstPunch;

    public required IReadOnlyList<string> DeviceIds { get; init; }
}

public sealed record AttendanceStats
{
    public required int EventsInMemory { get; init; }
    public required int EventsToday { get; init; }
    public required int DistinctEmployeesToday { get; init; }
    public required long TotalIngested { get; init; }
    public required long DuplicatesDropped { get; init; }
    public required long PayloadsIgnored { get; init; }
    public required long QueueDrops { get; init; }
    public required int QueueDepth { get; init; }
    public DateTimeOffset? LastEventAt { get; init; }
    public required bool AlarmListenerRunning { get; init; }
    public required string AlarmListenerStatus { get; init; }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

/// <summary>
/// Turns a device payload into an <see cref="AttendanceEvent"/>.
/// </summary>
/// <remarks>
/// Handles the three shapes the same access-control event arrives in:
/// the ISAPI <c>AccessControllerEvent</c> notification (JSON or XML) pushed over the ISUP
/// alarm channel, and the flatter <c>AcsEvent</c> record returned by an ISAPI event search.
/// Rather than binding each shape to its own DTO, scalar leaves are flattened into one
/// case-insensitive bag and read through alias lists, so a firmware that renames or moves a
/// field degrades to a missing value instead of a parse failure.
/// </remarks>
public sealed partial class AccessEventParser
{
    private readonly AcsEventCatalog _catalog;
    private readonly AttendanceClock _clock;
    private readonly AttendanceOptions _options;
    private readonly ILogger<AccessEventParser> _logger;

    public AccessEventParser(
        AcsEventCatalog catalog,
        AttendanceClock clock,
        IOptions<AttendanceOptions> options,
        ILogger<AccessEventParser> logger)
    {
        _catalog = catalog;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcOffsetSuffix();

    /// <summary>
    /// Parses a message off the alarm channel. Returns null when the payload is not an
    /// access-control event, which is normal: the channel also carries tamper, door and
    /// health messages.
    /// </summary>
    public AttendanceEvent? Parse(RawAlarmMessage raw, string? fallbackDeviceId)
    {
        if (string.IsNullOrWhiteSpace(raw.Payload))
            return null;

        var fields = raw.Format switch
        {
            RawAlarmFormat.Json => FlattenJson(raw.Payload),
            RawAlarmFormat.Xml => FlattenXml(raw.Payload),
            _ => Sniff(raw.Payload)
        };

        if (fields is null)
            return null;

        var source = raw.AlarmType == Interop.HikvisionAlarmNative.EHOME_ALARM_ACS
            ? AttendanceSource.IsupAcsAlarm
            : AttendanceSource.IsupIsapiAlarm;

        return Build(fields, fallbackDeviceId, raw.DeviceSerial, source, raw.ReceivedAt, raw.Payload);
    }

    /// <summary>Parses one item from an ISAPI <c>AcsEvent</c> search response.</summary>
    public AttendanceEvent? ParseSearchResult(JsonElement item, string deviceId, string? deviceSerial)
    {
        var fields = new FieldBag();
        CollectJson(item, fields);

        return Build(
            fields,
            deviceId,
            deviceSerial,
            AttendanceSource.IsapiBackfill,
            DateTimeOffset.UtcNow,
            item.GetRawText());
    }

    private AttendanceEvent? Build(
        FieldBag fields,
        string? fallbackDeviceId,
        string? deviceSerial,
        AttendanceSource source,
        DateTimeOffset receivedAt,
        string rawPayload)
    {
        var major = fields.GetInt("majorEventType", "major");
        var minor = fields.GetInt("subEventType", "minor", "minorEventType");

        if (major is null || minor is null)
        {
            _logger.LogDebug("Payload carries no major/minor event code; not an access event.");
            return null;
        }

        var deviceId = fields.GetString("deviceID", "deviceId")
                       ?? fallbackDeviceId
                       ?? deviceSerial
                       ?? "unknown";

        var reportedTime = fields.GetString("dateTime", "time", "eventTime");
        var eventTime = ParseTime(reportedTime) ?? receivedAt;

        var employeeId = Normalise(fields.GetString("employeeNoString", "employeeNo"));
        var cardNo = Normalise(fields.GetString("cardNo"));
        var attendanceStatus = Normalise(fields.GetString("attendanceStatus"));
        var serial = fields.GetLong("serialNo");

        var evt = new AttendanceEvent
        {
            Id = ComputeId(deviceId, serial, major.Value, minor.Value, eventTime, employeeId, cardNo),
            DeviceId = deviceId,
            DeviceSerial = deviceSerial,
            EmployeeId = employeeId,
            PersonName = Normalise(fields.GetString("name", "personName", "employeeName")),
            CardNo = cardNo,
            EventTime = eventTime,
            DeviceReportedTime = reportedTime,
            ReceivedAt = receivedAt,
            MajorEventType = major.Value,
            SubEventType = minor.Value,
            EventName = _catalog.GetName(major.Value, minor.Value),
            Outcome = _catalog.GetOutcome(major.Value, minor.Value),
            AttendanceStatus = attendanceStatus,
            Direction = ToDirection(attendanceStatus),
            VerificationMode = Normalise(fields.GetString("currentVerifyMode", "verifyMode")),
            DoorNo = fields.GetInt("doorNo"),
            ReaderNo = fields.GetInt("cardReaderNo", "readerNo"),
            DeviceEventSerial = serial,
            PictureUrl = Normalise(fields.GetString("pictureURL", "pictureUrl")),
            Source = source,
            RawPayload = _options.RetainRawPayload ? rawPayload : null
        };

        return evt;
    }

    /// <summary>
    /// Stable across restarts and across ingest paths, so an event that arrives live and is
    /// later re-read by a backfill collapses onto one record. The device event serial is the
    /// key whenever the device supplies one.
    /// </summary>
    private static string ComputeId(
        string deviceId,
        long? serial,
        int major,
        int minor,
        DateTimeOffset eventTime,
        string? employeeId,
        string? cardNo)
    {
        var key = serial is not null
            ? $"{deviceId}|{serial}"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{deviceId}|{major}|{minor}|{eventTime.UtcDateTime:O}|{employeeId}|{cardNo}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();

        if (UtcOffsetSuffix().IsMatch(text) &&
            DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var withOffset))
        {
            return withOffset;
        }

        // No offset: the terminal reported its own wall clock.
        if (DateTime.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            return _clock.FromDeviceLocal(naive);
        }

        _logger.LogWarning("Could not parse device timestamp {Timestamp}.", text);
        return null;
    }

    private static AttendanceDirection ToDirection(string? attendanceStatus) =>
        attendanceStatus?.ToLowerInvariant() switch
        {
            "checkin" or "breakin" or "overtimein" => AttendanceDirection.In,
            "checkout" or "breakout" or "overtimeout" => AttendanceDirection.Out,
            _ => AttendanceDirection.Unknown
        };

    private static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 || trimmed == "0" ? null : trimmed;
    }

    // ---- Payload flattening --------------------------------------------------------------

    private FieldBag? Sniff(string payload)
    {
        var trimmed = payload.TrimStart();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return FlattenJson(payload);

        if (trimmed.StartsWith('<'))
            return FlattenXml(payload);

        _logger.LogDebug("Alarm payload is neither JSON nor XML; ignoring.");
        return null;
    }

    private FieldBag? FlattenJson(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var fields = new FieldBag();
            CollectJson(document.RootElement, fields);
            return fields;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Alarm payload is not valid JSON.");
            return null;
        }
    }

    private FieldBag? FlattenXml(string payload)
    {
        try
        {
            var root = XDocument.Parse(payload).Root;
            if (root is null)
                return null;

            var fields = new FieldBag();
            CollectXml(root, fields);
            return fields;
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogWarning(ex, "Alarm payload is not valid XML.");
            return null;
        }
    }

    /// <summary>
    /// Depth-first so nested values (the AccessControllerEvent body) override same-named
    /// values from the envelope.
    /// </summary>
    private static void CollectJson(JsonElement element, FieldBag fields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        CollectJson(property.Value, fields);
                    else
                        fields.Set(property.Name, ScalarText(property.Value));
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectJson(item, fields);

                break;
        }
    }

    private static string? ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText()
    };

    private static void CollectXml(XElement element, FieldBag fields)
    {
        foreach (var child in element.Elements())
        {
            if (child.HasElements)
                CollectXml(child, fields);
            else
                fields.Set(child.Name.LocalName, child.Value);
        }
    }

    /// <summary>Case-insensitive flat view of a payload, read through field aliases.</summary>
    private sealed class FieldBag
    {
        private readonly Dictionary<string, string> _values =
            new(StringComparer.OrdinalIgnoreCase);

        public void Set(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                _values[name] = value;
        }

        public string? GetString(params string[] names)
        {
            foreach (var name in names)
            {
                if (_values.TryGetValue(name, out var value))
                    return value;
            }

            return null;
        }

        public int? GetInt(params string[] names) =>
            int.TryParse(GetString(names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        public long? GetLong(params string[] names) =>
            long.TryParse(GetString(names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }
}

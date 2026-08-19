using System.Globalization;
using System.Text.Json;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

public sealed record BackfillResult
{
    public required string DeviceId { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required int Pages { get; init; }
    public required int Fetched { get; init; }
    public required int Added { get; init; }
    public required int Duplicates { get; init; }
    public required int Unparsed { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// Pulls access-control events the terminal buffered while this server was unreachable.
/// </summary>
/// <remarks>
/// The alarm channel is push-only, so anything the device recorded during an outage is
/// invisible to it. Terminals keep an on-board event log, and an ISAPI AcsEvent search
/// replays it. De-duplication is by device event serial, so re-reading a range that already
/// arrived live costs nothing but a few requests.
/// </remarks>
public sealed class AttendanceBackfillService
{
    private const string SearchUrl = "/ISAPI/AccessControl/AcsEvent?format=json";

    private readonly IsapiPassthroughService _isapi;
    private readonly DeviceRegistry _registry;
    private readonly AccessEventParser _parser;
    private readonly AttendanceIngestService _ingest;
    private readonly BackfillOptions _options;
    private readonly ILogger<AttendanceBackfillService> _logger;

    public AttendanceBackfillService(
        IsapiPassthroughService isapi,
        DeviceRegistry registry,
        AccessEventParser parser,
        AttendanceIngestService ingest,
        IOptions<AttendanceOptions> options,
        ILogger<AttendanceBackfillService> logger)
    {
        _isapi = isapi;
        _registry = registry;
        _parser = parser;
        _ingest = ingest;
        _options = options.Value.Backfill;
        _logger = logger;
    }

    public BackfillOptions Options => _options;

    public BackfillResult Run(string deviceId, DateTimeOffset from, DateTimeOffset to)
    {
        var device = _registry.Find(deviceId);

        if (device is null)
            return Empty(deviceId, from, to, "device not registered");

        if (!device.Online)
            return Empty(deviceId, from, to, "device offline");

        var searchId = Guid.NewGuid().ToString("N");
        var pageSize = Math.Clamp(_options.PageSize, 1, 100);

        var position = 0;
        var pages = 0;
        var fetched = 0;
        var added = 0;
        var duplicates = 0;
        var unparsed = 0;
        var status = "complete";

        while (pages < Math.Max(_options.MaxPages, 1))
        {
            var body = BuildRequest(searchId, position, pageSize, from, to);
            var response = _isapi.Send(device.LoginId, IsapiVerb.Post, SearchUrl, body);

            pages++;

            if (string.IsNullOrWhiteSpace(response))
            {
                status = "ISAPI call failed";
                break;
            }

            JsonElement acsEvent;

            try
            {
                using var document = JsonDocument.Parse(response);

                if (!document.RootElement.TryGetProperty("AcsEvent", out var element))
                {
                    status = "unexpected response shape";
                    _logger.LogWarning("AcsEvent search returned an unexpected body: {Body}", Truncate(response));
                    break;
                }

                acsEvent = element.Clone();
            }
            catch (JsonException ex)
            {
                status = "invalid JSON response";
                _logger.LogWarning(ex, "AcsEvent search returned invalid JSON.");
                break;
            }

            var pageCount = 0;

            if (acsEvent.TryGetProperty("InfoList", out var list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    pageCount++;
                    fetched++;

                    var evt = _parser.ParseSearchResult(item, deviceId, device.SerialNumber);

                    if (evt is null)
                    {
                        unparsed++;
                        continue;
                    }

                    if (_ingest.Accept(evt with { Source = AttendanceSource.IsapiBackfill }))
                        added++;
                    else
                        duplicates++;
                }
            }

            var responseStatus = acsEvent.TryGetProperty("responseStatusStrg", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (pageCount == 0 || !string.Equals(responseStatus, "MORE", StringComparison.OrdinalIgnoreCase))
                break;

            position += pageCount;
        }

        if (pages >= Math.Max(_options.MaxPages, 1))
            status = "stopped at the page limit";

        _logger.LogInformation(
            "Backfill {DeviceId} {From:O}..{To:O}: {Added} added, {Duplicates} duplicate, {Unparsed} unparsed over {Pages} page(s) ({Status}).",
            deviceId, from, to, added, duplicates, unparsed, pages, status);

        return new BackfillResult
        {
            DeviceId = deviceId,
            From = from,
            To = to,
            Pages = pages,
            Fetched = fetched,
            Added = added,
            Duplicates = duplicates,
            Unparsed = unparsed,
            Status = status
        };
    }

    /// <summary>Fires the configured lookback window after a device registers.</summary>
    public void RunOnDeviceOnline(string deviceId)
    {
        if (!_options.OnDeviceOnline)
            return;

        var to = DateTimeOffset.Now;
        var from = to.AddHours(-Math.Max(_options.LookbackHours, 1));

        // Deliberately off the registration callback thread: the SDK is waiting on it.
        _ = Task.Run(() =>
        {
            try
            {
                Run(deviceId, from, to);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic backfill for {DeviceId} failed.", deviceId);
            }
        });
    }

    private static string BuildRequest(
        string searchId,
        int position,
        int pageSize,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var condition = new
        {
            AcsEventCond = new
            {
                searchID = searchId,
                searchResultPosition = position,
                maxResults = pageSize,
                major = 0,
                minor = 0,
                startTime = from.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
                endTime = to.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)
            }
        };

        return JsonSerializer.Serialize(condition);
    }

    private static BackfillResult Empty(
        string deviceId, DateTimeOffset from, DateTimeOffset to, string status) =>
        new()
        {
            DeviceId = deviceId,
            From = from,
            To = to,
            Pages = 0,
            Fetched = 0,
            Added = 0,
            Duplicates = 0,
            Unparsed = 0,
            Status = status
        };

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512] + "...";
}

using System.Threading.Channels;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

/// <summary>
/// The single funnel every attendance event passes through, whichever channel it arrived on.
/// </summary>
/// <remarks>
/// The SDK invokes the alarm callback on its own thread and expects it back promptly, so the
/// callback only copies the payload and hands it here. Parsing, disk writes and webhook
/// delivery all happen on this background loop instead.
/// </remarks>
public sealed class AttendanceIngestService : BackgroundService
{
    private readonly Channel<RawAlarmMessage> _queue;
    private readonly AccessEventParser _parser;
    private readonly AttendanceStore _store;
    private readonly PhotoStore _photos;
    private readonly HrmsForwarder _forwarder;
    private readonly DeviceRegistry _registry;
    private readonly ILogger<AttendanceIngestService> _logger;

    private long _received;
    private long _ignoredPayloads;
    private long _queueDrops;

    public AttendanceIngestService(
        AccessEventParser parser,
        AttendanceStore store,
        PhotoStore photos,
        HrmsForwarder forwarder,
        DeviceRegistry registry,
        IOptions<AttendanceOptions> options,
        ILogger<AttendanceIngestService> logger)
    {
        _parser = parser;
        _store = store;
        _photos = photos;
        _forwarder = forwarder;
        _registry = registry;
        _logger = logger;

        _queue = Channel.CreateBounded<RawAlarmMessage>(
            new BoundedChannelOptions(Math.Max(options.Value.QueueCapacity, 16))
            {
                SingleReader = true
            });
    }

    public long ReceivedCount => Interlocked.Read(ref _received);

    public long IgnoredPayloadCount => Interlocked.Read(ref _ignoredPayloads);

    public long QueueDropCount => Interlocked.Read(ref _queueDrops);

    public int QueueDepth => _queue.Reader.Count;

    /// <summary>Called from the native alarm callback. Never blocks.</summary>
    public void Submit(RawAlarmMessage message)
    {
        Interlocked.Increment(ref _received);

        if (_queue.Writer.TryWrite(message))
            return;

        Interlocked.Increment(ref _queueDrops);
        _logger.LogError(
            "Attendance ingest queue is full; dropped an alarm of type {AlarmType}.",
            message.AlarmType);
    }

    /// <summary>Stores an already-parsed event, e.g. one pulled by a backfill.</summary>
    public bool Accept(AttendanceEvent evt)
    {
        if (!_store.Add(evt))
            return false;

        _forwarder.Enqueue(evt);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                Process(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process an alarm of type {AlarmType}.", message.AlarmType);
            }
        }
    }

    private void Process(RawAlarmMessage message)
    {
        var fallbackDeviceId = message.DeviceSerial is null
            ? null
            : _registry.FindBySerial(message.DeviceSerial)?.DeviceId;

        var evt = _parser.Parse(message, fallbackDeviceId);

        if (evt is null)
        {
            Interlocked.Increment(ref _ignoredPayloads);
            _logger.LogDebug(
                "Alarm type {AlarmType} produced no attendance event.", message.AlarmType);
            return;
        }

        if (message.Pictures.Count > 0)
        {
            var photos = _photos.Save(evt.Id, evt.EventTime, message.Pictures);

            if (photos.Count > 0)
                evt = evt with { Photos = photos };
        }

        if (!Accept(evt))
        {
            _logger.LogDebug("Ignored duplicate event {EventId}.", evt.Id);
            return;
        }

        _registry.Touch(evt.DeviceId);

        _logger.LogInformation(
            "Attendance {EventName} ({Outcome}) device={DeviceId} employee={EmployeeId} at {EventTime:O}",
            evt.EventName, evt.Outcome, evt.DeviceId, evt.EmployeeId ?? "-", evt.EventTime);
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

/// <summary>
/// Posts accepted events to the HRMS webhook, off the ingest path so a slow or unreachable
/// HRMS never stalls event capture.
/// </summary>
public sealed class HrmsForwarder : BackgroundService
{
    public const string HttpClientName = "hrms";

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Channel<AttendanceEvent> _queue;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HrmsOptions _options;
    private readonly ILogger<HrmsForwarder> _logger;

    private long _delivered;
    private long _dropped;

    public HrmsForwarder(
        IHttpClientFactory httpClientFactory,
        IOptions<AttendanceOptions> options,
        ILogger<HrmsForwarder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value.Hrms;
        _logger = logger;

        _queue = Channel.CreateBounded<AttendanceEvent>(
            new BoundedChannelOptions(Math.Max(_options.QueueCapacity, 16))
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.WebhookUrl);

    public long DeliveredCount => Interlocked.Read(ref _delivered);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Enqueue(AttendanceEvent evt)
    {
        if (!Enabled)
            return;

        if (!_queue.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _dropped);
            _logger.LogError(
                "HRMS forward queue is full; dropped event {EventId}. It remains in the journal.",
                evt.Id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation(
                "HRMS forwarding is disabled (no Attendance:Hrms:WebhookUrl configured).");
            return;
        }

        _logger.LogInformation("Forwarding attendance events to {Url}.", _options.WebhookUrl);

        await foreach (var evt in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await DeliverAsync(evt, stoppingToken);
        }
    }

    private async Task DeliverAsync(AttendanceEvent evt, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(_options.MaxRetries, 1);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var client = CreateClient();
                using var content = new StringContent(
                    JsonSerializer.Serialize(evt, PayloadJson), Encoding.UTF8, "application/json");

                using var response = await client.PostAsync(_options.WebhookUrl, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _delivered);
                    return;
                }

                _logger.LogWarning(
                    "HRMS webhook returned {StatusCode} for event {EventId} (attempt {Attempt}/{Attempts}).",
                    (int)response.StatusCode, evt.Id, attempt, attempts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "HRMS webhook call failed for event {EventId} (attempt {Attempt}/{Attempts}).",
                    evt.Id, attempt, attempts);
            }

            if (attempt < attempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        Interlocked.Increment(ref _dropped);
        _logger.LogError(
            "Gave up forwarding event {EventId} after {Attempts} attempts. It remains in the journal for replay.",
            evt.Id, attempts);
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(_options.TimeoutSeconds, 1));

        if (!string.IsNullOrWhiteSpace(_options.AuthorizationHeader) &&
            AuthenticationHeaderValue.TryParse(_options.AuthorizationHeader, out var header))
        {
            client.DefaultRequestHeaders.Authorization = header;
        }

        return client;
    }
}

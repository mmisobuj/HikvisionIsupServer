namespace HikvisionIsupServer.Services;

public sealed class IsupHostedService : IHostedService
{
    private readonly IsupAlarmService _alarm;
    private readonly IsupCmsService _cms;
    private readonly ILogger<IsupHostedService> _logger;

    public IsupHostedService(
        IsupAlarmService alarm,
        IsupCmsService cms,
        ILogger<IsupHostedService> logger)
    {
        _alarm = alarm;
        _cms = cms;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Hikvision ISUP server...");

        // Alarm first: the CMS registration callback hands it the ISUP 5.0 session key, and a
        // device can register within milliseconds of the listener opening.
        _alarm.Start();
        _cms.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Hikvision ISUP server...");

        _cms.Dispose();
        _alarm.Dispose();

        return Task.CompletedTask;
    }
}

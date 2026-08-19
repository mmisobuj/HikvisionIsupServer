using System.Text.Json.Serialization;
using HikvisionIsupServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

builder.Services.Configure<IsupOptions>(
    builder.Configuration.GetSection("HikvisionIsup"));

builder.Services.Configure<AttendanceOptions>(
    builder.Configuration.GetSection("Attendance"));

// Attendance pipeline: alarm callback -> ingest queue -> parser -> store -> HRMS webhook.
builder.Services.AddSingleton<AttendanceClock>();
builder.Services.AddSingleton<AcsEventCatalog>();
builder.Services.AddSingleton<AccessEventParser>();
builder.Services.AddSingleton<PhotoStore>();
builder.Services.AddSingleton<AttendanceStore>();

builder.Services.AddSingleton<HrmsForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HrmsForwarder>());

builder.Services.AddSingleton<AttendanceIngestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AttendanceIngestService>());

// ISUP channels.
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<IsapiPassthroughService>();
builder.Services.AddSingleton<AttendanceBackfillService>();
builder.Services.AddSingleton<IsupAlarmService>();
builder.Services.AddSingleton<IsupCmsService>();
builder.Services.AddHostedService<IsupHostedService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.MapGet("/", (
    Microsoft.Extensions.Options.IOptions<IsupOptions> options,
    IsupAlarmService alarm) =>
{
    var isup = options.Value;

    return Results.Ok(new
    {
        service = "Hikvision ISUP 5.0 attendance server",
        status = "running",
        cms = $"{isup.ListenIp}:{isup.ListenPort}",
        alarm = alarm.Status,
        api = $"http://0.0.0.0:{isup.ApiPort}"
    });
});

app.Run();

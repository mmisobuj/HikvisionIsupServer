using System.Text.Json;
using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

/// <summary>
/// Maps Hikvision ISAPI major/minor event codes onto names and an access outcome.
/// </summary>
/// <remarks>
/// Loaded from <c>config/acs-event-types.json</c> so codes can be added or corrected against
/// the ISAPI documentation without a rebuild. Unknown codes are not an error: they surface as
/// <c>major/minor</c> with <see cref="AccessOutcome.Info"/> and the raw payload is retained.
/// </remarks>
public sealed class AcsEventCatalog
{
    private readonly Dictionary<(int Major, int Minor), Entry> _entries = new();
    private readonly ILogger<AcsEventCatalog> _logger;

    public AcsEventCatalog(
        ILogger<AcsEventCatalog> logger,
        IOptions<AttendanceOptions> options,
        IHostEnvironment environment)
    {
        _logger = logger;

        var path = options.Value.EventCatalogPath;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(environment.ContentRootPath, path);

        Load(path);
    }

    private sealed record Entry(string Name, AccessOutcome Outcome);

    private void Load(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "ISAPI event catalog not found at {Path}. Events will be reported as major/minor codes.",
                path);
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            foreach (var major in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(major.Name, out var majorCode))
                    continue;

                foreach (var minor in major.Value.EnumerateObject())
                {
                    if (!int.TryParse(minor.Name, out var minorCode))
                        continue;

                    var name = minor.Value.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString()
                        : null;

                    var outcome = minor.Value.TryGetProperty("outcome", out var outcomeElement)
                        ? ParseOutcome(outcomeElement.GetString())
                        : AccessOutcome.Info;

                    if (!string.IsNullOrWhiteSpace(name))
                        _entries[(majorCode, minorCode)] = new Entry(name, outcome);
                }
            }

            _logger.LogInformation(
                "Loaded {Count} ISAPI event codes from {Path}.", _entries.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the ISAPI event catalog at {Path}.", path);
        }
    }

    private static AccessOutcome ParseOutcome(string? value) => value switch
    {
        "granted" => AccessOutcome.Granted,
        "denied" => AccessOutcome.Denied,
        _ => AccessOutcome.Info
    };

    public string GetName(int major, int minor) =>
        _entries.TryGetValue((major, minor), out var entry)
            ? entry.Name
            : $"{major}/{minor}";

    public AccessOutcome GetOutcome(int major, int minor) =>
        _entries.TryGetValue((major, minor), out var entry)
            ? entry.Outcome
            : AccessOutcome.Info;

    public int Count => _entries.Count;
}

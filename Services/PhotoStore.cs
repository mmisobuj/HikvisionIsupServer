using HikvisionIsupServer.Models;
using Microsoft.Extensions.Options;

namespace HikvisionIsupServer.Services;

/// <summary>
/// Writes event photos that arrive inline on the ISAPI alarm channel to disk, one folder
/// per local date, and serves them back to the API by name.
/// </summary>
public sealed class PhotoStore
{
    private readonly AttendanceOptions _options;
    private readonly AttendanceClock _clock;
    private readonly ILogger<PhotoStore> _logger;
    private readonly string _root;

    public PhotoStore(
        IOptions<AttendanceOptions> options,
        AttendanceClock clock,
        IHostEnvironment environment,
        ILogger<PhotoStore> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;

        _root = Path.IsPathRooted(_options.PhotoDirectory)
            ? _options.PhotoDirectory
            : Path.Combine(environment.ContentRootPath, _options.PhotoDirectory);
    }

    /// <summary>Saves the pictures for an event and returns the relative names to record on it.</summary>
    public IReadOnlyList<string> Save(
        string eventId,
        DateTimeOffset eventTime,
        IReadOnlyList<RawAlarmPicture> pictures)
    {
        if (!_options.SavePhotos || pictures.Count == 0)
            return Array.Empty<string>();

        var folder = _clock.LocalDate(eventTime).ToString("yyyy-MM-dd");
        var directory = Path.Combine(_root, folder);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the photo directory {Directory}.", directory);
            return Array.Empty<string>();
        }

        var saved = new List<string>(pictures.Count);

        for (var i = 0; i < pictures.Count; i++)
        {
            var extension = Path.GetExtension(pictures[i].FileName);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
                extension = ".jpg";

            var name = $"{folder}/{eventId}_{i}{extension}";

            try
            {
                File.WriteAllBytes(Path.Combine(_root, folder, $"{eventId}_{i}{extension}"), pictures[i].Data);
                saved.Add(name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write the photo {Name}.", name);
            }
        }

        return saved;
    }

    /// <summary>
    /// Resolves a stored photo name to a readable path, refusing anything that escapes the
    /// photo root.
    /// </summary>
    public string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var candidate = Path.GetFullPath(Path.Combine(_root, name));
        var root = Path.GetFullPath(_root);

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected a photo path outside the photo root: {Name}", name);
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }
}

using HikvisionIsupServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace HikvisionIsupServer.Controllers;

public sealed class IsapiProxyRequest
{
    public string Verb { get; set; } = "GET";
    public string Url { get; set; } = "";
    public string? Body { get; set; }
}

[ApiController]
[Route("api/hikvision/devices/{deviceId}/isapi")]
public sealed class IsapiController : ControllerBase
{
    private readonly DeviceRegistry _registry;
    private readonly IsapiPassthroughService _isapi;
    private readonly ILogger<IsapiController> _logger;

    public IsapiController(
        DeviceRegistry registry,
        IsapiPassthroughService isapi,
        ILogger<IsapiController> logger)
    {
        _registry = registry;
        _isapi = isapi;
        _logger = logger;
    }

    [HttpPost]
    public IActionResult Proxy(string deviceId, [FromBody] IsapiProxyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { ok = false, message = "Url is required" });

        var device = _registry.Find(deviceId)
            ?? _registry.GetAll().FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        if (device is null || !device.Online)
            return NotFound(new { ok = false, message = $"Device {deviceId} is not Online on ISUP bridge" });

        if (device.LoginId < 0)
            return Conflict(new { ok = false, message = $"Device {deviceId} has no valid ISUP login handle yet" });

        var verb = (request.Verb ?? "GET").Trim().ToUpperInvariant() switch
        {
            "POST" => IsapiVerb.Post,
            "PUT" => IsapiVerb.Put,
            "DELETE" => IsapiVerb.Delete,
            _ => IsapiVerb.Get
        };

        var url = request.Url.Trim();
        if (!url.StartsWith('/'))
            url = "/" + url;

        _logger.LogInformation(
            "ISAPI proxy {Verb} {Url} for device {DeviceId} login {LoginId}",
            verb, url, device.DeviceId, device.LoginId);

        var responseBody = _isapi.Send(device.LoginId, verb, url, request.Body);
        if (responseBody is null)
        {
            return StatusCode(502, new
            {
                ok = false,
                message = "ISAPI passthrough failed (device offline or SDK error)",
                deviceId = device.DeviceId,
                loginId = device.LoginId
            });
        }

        return Ok(new
        {
            ok = true,
            deviceId = device.DeviceId,
            loginId = device.LoginId,
            verb = verb.ToString(),
            url,
            body = responseBody
        });
    }
}

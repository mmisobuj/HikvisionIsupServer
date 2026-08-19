using HikvisionIsupServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace HikvisionIsupServer.Controllers;

[ApiController]
[Route("api/hikvision/devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly DeviceRegistry _registry;

    public DevicesController(DeviceRegistry registry)
    {
        _registry = registry;
    }

    [HttpGet]
    public IActionResult GetDevices() =>
        Ok(_registry.GetAll());

    [HttpGet("{deviceId}")]
    public IActionResult GetDevice(string deviceId)
    {
        var device = _registry.GetAll()
            .FirstOrDefault(x => x.DeviceId == deviceId);

        return device is null ? NotFound() : Ok(device);
    }
}

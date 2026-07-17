using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Harbor.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        return Ok(new HealthResponse("ok", "harbor-net", version, DateTimeOffset.UtcNow));
    }
}

public record HealthResponse(string Status, string Name, string Version, DateTimeOffset UtcNow);

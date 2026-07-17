using System.Diagnostics;
using System.Reflection;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

/// <summary>
/// Liveness and readiness. The database probe is what makes this useful to a
/// load balancer: a process that is running but cannot reach its database
/// should be taken out of rotation, so an unreachable database answers 503
/// rather than a cheerful 200.
/// </summary>
[ApiController]
[Route("health")]
[AllowAnonymous]
public class HealthController(HarborDbContext db, ILogger<HealthController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<HealthResponse>> Get(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        var database = await ProbeDatabaseAsync(cancellationToken);
        var response = new HealthResponse(
            database.Healthy ? "ok" : "unhealthy",
            "harbor-net",
            version,
            DateTimeOffset.UtcNow,
            database);

        return database.Healthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    /// <summary>
    /// Runs a trivial query rather than only opening a connection: a pooled
    /// connection can look alive while the database behind it is not.
    /// </summary>
    private async Task<CheckResult> ProbeDatabaseAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            stopwatch.Stop();
            return new CheckResult(true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Health check database probe failed.");

            // The detail is for the operator's logs, not the internet: the
            // caller learns the database is unreachable, not our connection
            // string or schema.
            return new CheckResult(false, stopwatch.ElapsedMilliseconds, "unreachable");
        }
    }
}

/// <summary>The outcome of one dependency probe.</summary>
public record CheckResult(bool Healthy, long DurationMs, string? Error);

public record HealthResponse(
    string Status, string Name, string Version, DateTimeOffset UtcNow, CheckResult Database);

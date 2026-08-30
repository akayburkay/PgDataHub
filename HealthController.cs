using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PgDataHub.Config;

namespace PgDataHub;

[ApiController, AllowAnonymous]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "ok" });

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        try
        {
            await using var connection = await Db.OpenConnectionAsync(ct);
            return Ok(new { status = "ready", database = "connected" });
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = "not-ready", database = "unavailable" });
        }
    }
}

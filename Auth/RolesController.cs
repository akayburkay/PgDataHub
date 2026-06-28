using Microsoft.AspNetCore.Mvc;
using PgDataHub.Config;

namespace PgDataHub.Auth;

[ApiController]
[Route("auth/roles")]
public sealed class RolesController : AdminControllerBase
{
    private readonly AuthService _auth;

    public RolesController(AuthService auth, PgDataHubConfig config) : base(config)
    {
        _auth = auth;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;
        return Ok(await _auth.ListRolesAsync(ct));
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> Upsert(
        string name, [FromBody] RoleUpsertRequest request, CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;

        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "role name is required." });

        await _auth.UpsertRoleAsync(name, request, ct);
        return NoContent();
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;

        return await _auth.DeleteRoleAsync(name, ct) switch
        {
            DeleteRoleResult.Deleted => NoContent(),
            DeleteRoleResult.NotFound => NotFound(new { error = "Role not found.", name }),
            DeleteRoleResult.Referenced => Conflict(
                new { error = "Role is still referenced by one or more API keys.", name }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}

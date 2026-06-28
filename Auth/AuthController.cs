using Microsoft.AspNetCore.Mvc;
using PgDataHub.Config;

namespace PgDataHub.Auth;

[ApiController]
[Route("auth/apikeys")]
public sealed class AuthController : AdminControllerBase
{
    private readonly AuthService _auth;

    public AuthController(AuthService auth, PgDataHubConfig config) : base(config)
    {
        _auth = auth;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;
        return Ok(await _auth.ListKeysAsync(ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name is required." });

        if (string.IsNullOrWhiteSpace(request.RoleName))
            return BadRequest(new { error = "roleName is required." });

        try
        {
            var created = await _auth.CreateKeyAsync(
                request.Name, request.Owner, request.Description,
                request.RoleName, request.ExpiresAt, ct);

            return CreatedAtAction(nameof(List), created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;

        return await _auth.RevokeKeyAsync(id, ct)
            ? NoContent()
            : NotFound(new { error = "API key not found.", id });
    }
}

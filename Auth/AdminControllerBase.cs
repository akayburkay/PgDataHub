using Microsoft.AspNetCore.Mvc;
using PgDataHub.Config;

namespace PgDataHub.Auth;

/// <summary>Base for admin-only management controllers (API keys, roles).</summary>
public abstract class AdminControllerBase : ControllerBase
{
    private readonly PgDataHubConfig _config;

    protected AdminControllerBase(PgDataHubConfig config) => _config = config;

    /// <summary>
    /// 404 when auth is off, 403 unless the caller has the admin role. Returns null
    /// to allow the action to proceed.
    /// </summary>
    protected IActionResult? AdminGuard()
    {
        if (!_config.Auth.Enabled)
            return NotFound();

        var adminRole = _config.Auth.AdminRole;

        if (!string.IsNullOrWhiteSpace(adminRole)
            && User.RoleNames().Contains(adminRole, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return StatusCode(
            StatusCodes.Status403Forbidden,
            new { error = "Admin role required." });
    }
}

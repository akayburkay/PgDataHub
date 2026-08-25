using Microsoft.AspNetCore.Mvc;
using PgDataHub.Config;

namespace PgDataHub.Auth;

/// <summary>Base for admin-only management controllers (API keys, roles).</summary>
public abstract class AdminControllerBase : ControllerBase
{
    private readonly PgDataHubConfig _config;
    private readonly AdminSessionService _sessions;

    protected AdminControllerBase(PgDataHubConfig config, AdminSessionService sessions)
    { _config = config; _sessions = sessions; }

    /// <summary>
    /// 404 when auth is off, 403 unless the caller has the admin role. Returns null
    /// to allow the action to proceed.
    /// </summary>
    protected IActionResult? AdminGuard()
    {
        var mutation = !HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method);
        return _sessions.Validate(HttpContext, mutation)
            ? null
            : Unauthorized(new { error = mutation ? "Admin session or CSRF token is invalid." : "Admin login required." });
    }
}

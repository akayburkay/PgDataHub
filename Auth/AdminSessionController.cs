using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace PgDataHub.Auth;

[ApiController, AllowAnonymous]
[Route("admin/session")]
public sealed class AdminSessionController : ControllerBase
{
    private readonly AdminSessionService _sessions;
    private readonly IWebHostEnvironment _environment;
    public AdminSessionController(AdminSessionService sessions, IWebHostEnvironment environment)
    { _sessions = sessions; _environment = environment; }

    [HttpPost]
    [EnableRateLimiting("admin-login")]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey)) return Unauthorized(new { error = "Invalid administrator credential." });
        var login = await _sessions.LoginAsync(request.ApiKey, ct);
        if (login is null) return Unauthorized(new { error = "Invalid administrator credential." });
        Response.Cookies.Append(AdminSessionService.CookieName, login.Token, new CookieOptions
        {
            HttpOnly = true, Secure = !_environment.IsDevelopment() || Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Expires = login.Expires, Path = "/"
        });
        return Ok(new { csrfToken = login.Csrf, expiresAt = login.Expires });
    }

    [HttpGet]
    public IActionResult Status() => _sessions.GetCsrf(HttpContext) is { } csrf
        ? Ok(new { authenticated = true, csrfToken = csrf }) : Unauthorized();

    [HttpDelete]
    public IActionResult Logout()
    {
        if (!_sessions.Validate(HttpContext, true)) return Unauthorized();
        _sessions.Logout(HttpContext);
        Response.Cookies.Delete(AdminSessionService.CookieName, new CookieOptions { Path = "/" });
        return NoContent();
    }
}

public sealed record AdminLoginRequest(string ApiKey);

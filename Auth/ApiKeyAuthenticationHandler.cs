using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PgDataHub.Config;

namespace PgDataHub.Auth;

public sealed class ApiKeyAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";

    private readonly AuthService _auth;
    private readonly PgDataHubConfig _config;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AuthService auth,
        PgDataHubConfig config)
        : base(options, logger, encoder)
    {
        _auth = auth;
        _config = config;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_config.Auth.Enabled)
            return AuthenticateResult.NoResult();

        var presented = Request.Headers[_config.Auth.ApiKey.Header].ToString();

        if (string.IsNullOrWhiteSpace(presented))
        {
            // No credentials: fall back to anonymous role if configured.
            var defaultRole = _config.Auth.DefaultRole;

            if (string.IsNullOrWhiteSpace(defaultRole))
                return AuthenticateResult.NoResult();

            return AuthenticateResult.Success(
                BuildTicket("anonymous", defaultRole, PrincipalSource.Anonymous));
        }

        if (!_config.Auth.ApiKey.Enabled)
            return AuthenticateResult.NoResult();

        var record = await _auth.ResolveApiKeyAsync(presented, Context.RequestAborted);

        if (record is null)
            return AuthenticateResult.Fail("Invalid or expired API key.");

        // Best-effort, throttled last-used update; must not block the request.
        _ = _auth.TouchKeyAsync(record.KeyHash);

        return AuthenticateResult.Success(
            BuildTicket(record.Name, record.RoleName, PrincipalSource.ApiKey));
    }

    private AuthenticationTicket BuildTicket(string name, string role, PrincipalSource source)
    {
        var identity = new ClaimsIdentity(
            authenticationType: SchemeName,
            nameType: ClaimTypes.Name,
            roleType: AuthClaims.Role);

        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        identity.AddClaim(new Claim(AuthClaims.Role, role));
        identity.AddClaim(new Claim("pgdatahub:source", source.ToString()));

        return new AuthenticationTicket(
            new ClaimsPrincipal(identity), Scheme.Name);
    }
}

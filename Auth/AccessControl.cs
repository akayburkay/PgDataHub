using System.Security.Claims;

namespace PgDataHub.Auth;

public static class AuthClaims
{
    /// <summary>Canonical role claim type used by the API key identity.</summary>
    public const string Role = "pgdatahub:role";
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Role names for the caller, read from the identity's own role claim type
    /// (API key identities use <see cref="AuthClaims.Role"/>; JWT identities use
    /// the configured role claim set as RoleClaimType).
    /// </summary>
    public static IReadOnlyList<string> RoleNames(this ClaimsPrincipal user)
    {
        var roleType = (user.Identity as ClaimsIdentity)?.RoleClaimType ?? ClaimTypes.Role;

        return user.FindAll(roleType)
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PgDataHub.Auth;

public sealed class AdminSessionService
{
    public const string CookieName = "pgdh_admin";
    public const string CsrfHeader = "X-PgDataHub-CSRF";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private const int MaxSessions = 10_000;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly AuthService _auth;
    private readonly Config.PgDataHubConfig _config;

    public AdminSessionService(AuthService auth, Config.PgDataHubConfig config)
    { _auth = auth; _config = config; }

    public async Task<LoginResult?> LoginAsync(string apiKey, CancellationToken ct)
    {
        var key = await _auth.ResolveApiKeyAsync(apiKey, ct);
        if (key is null || !string.Equals(key.RoleName, _config.Auth.AdminRole, StringComparison.OrdinalIgnoreCase))
            return null;
        RemoveExpiredSessions();
        if (_sessions.Count >= MaxSessions) return null;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var csrf = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        _sessions[token] = new Session(csrf, key.KeyHash, expires);
        return new(token, csrf, expires);
    }

    public bool Validate(HttpContext context, bool requireCsrf)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) ||
            !_sessions.TryGetValue(token, out var session)) return false;
        if (session.Expires <= DateTimeOffset.UtcNow || !_auth.IsAdminKeyHashUsable(session.KeyHash))
        { _sessions.TryRemove(token, out _); return false; }
        return !requireCsrf || CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(session.Csrf),
            System.Text.Encoding.UTF8.GetBytes(context.Request.Headers[CsrfHeader].ToString()));
    }

    public string? GetCsrf(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) ||
            !_sessions.TryGetValue(token, out var session) || session.Expires <= DateTimeOffset.UtcNow ||
            !_auth.IsAdminKeyHashUsable(session.KeyHash)) return null;
        return session.Csrf;
    }

    public void Logout(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token)) _sessions.TryRemove(token, out _);
    }

    private void RemoveExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _sessions)
            if (item.Value.Expires <= now || !_auth.IsAdminKeyHashUsable(item.Value.KeyHash))
                _sessions.TryRemove(item.Key, out _);
    }

    private sealed record Session(string Csrf, string KeyHash, DateTimeOffset Expires);
    public sealed record LoginResult(string Token, string Csrf, DateTimeOffset Expires);
}

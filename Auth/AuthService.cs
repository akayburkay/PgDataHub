using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PgDataHub.Config;
using PgDataHub.Metadata;

namespace PgDataHub.Auth;

/// <summary>
/// Auth runtime: builds an in-memory view of roles/permissions/keys from the INI
/// config and persists management changes back to the same INI file. No database.
/// </summary>
public sealed class AuthService
{
    private static readonly TimeSpan TouchThrottle = TimeSpan.FromSeconds(60);

    private readonly PgDataHubConfig _config;
    private readonly ILogger<AuthService> _logger;
    private readonly SemaphoreSlim _gate = ConfigMutationCoordinator.Gate;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFlushed = new();
    private volatile AuthCache _cache;

    public AuthService(PgDataHubConfig config, ILogger<AuthService> logger)
    {
        _config = config;
        _logger = logger;
        _cache = BuildCache();
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        _cache = BuildCache();
        return Task.CompletedTask;
    }

    /// <summary>SHA-256 hex hash of a raw API key.</summary>
    public static string HashKey(string rawKey)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    /// <summary>Generate a new high-entropy raw API key.</summary>
    public static string GenerateRawKey()
        => "pgdh_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    // ----- Authentication / authorization (hot path, lock-free reads) -----

    public Task<ApiKeyRecord?> ResolveApiKeyAsync(string presentedKey, CancellationToken ct = default)
    {
        _cache.KeysByHash.TryGetValue(HashKey(presentedKey), out var record);
        return Task.FromResult(record is { IsUsable: true } ? record : null);
    }

    public Task<bool> CanAccessCollectionAsync(
        IReadOnlyList<string> roles, string schema, string name, CancellationToken ct = default)
        => Task.FromResult(Evaluate(_cache, roles, ResourceType.Collection, Full(schema, name)));

    public Task<bool> CanAccessFunctionAsync(
        IReadOnlyList<string> roles, string schema, string name, CancellationToken ct = default)
        => Task.FromResult(Evaluate(_cache, roles, ResourceType.Function, Full(schema, name)));

    /// <summary>
    /// Effective column visibility for a principal on a collection, merged across
    /// all matching allow-permissions: union of allow-lists and hides. A hidden
    /// column stays hidden when any matching rule hides it.
    /// </summary>
    public Task<ColumnRules> ResolveColumnRulesAsync(
        IReadOnlyList<string> roles, string schema, string name, CancellationToken ct = default)
    {
        var full = Full(schema, name);

        var matching = _config.Roles
            .Where(role => roles.Contains(role.Name, StringComparer.OrdinalIgnoreCase) &&
                           role.CollectionsAllow.Any(pattern => Glob.Match(full, pattern)))
            .SelectMany(role => role.ColumnRules)
            .Where(rule => Glob.Match(full, rule.Key))
            .Select(rule => rule.Value)
            .ToList();

        if (matching.Count == 0)
            return Task.FromResult(new ColumnRules([], []));

        var allow = matching.Any(p => p.Allow.Count == 0)
            ? Array.Empty<string>()
            : matching.SelectMany(p => p.Allow)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();

        IReadOnlyList<string> hide = matching
            .SelectMany(p => p.Hide)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(new ColumnRules(allow, hide));
    }

    /// <summary>
    /// Return a copy of <paramref name="collection"/> narrowed to the columns the
    /// principal may see. Returns the original when there are no column restrictions.
    /// </summary>
    public async Task<CollectionMetadata> ApplyColumnRulesAsync(
        IReadOnlyList<string> roles, CollectionMetadata collection, CancellationToken ct = default)
    {
        var rules = await ResolveColumnRulesAsync(roles, collection.Schema, collection.Name, ct);

        if (rules.Allow.Count == 0 && rules.Hide.Count == 0)
            return collection;

        return CollectionOverrideApplier.Apply(
            collection, rules.Allow, rules.Hide, validateColumns: false, _logger);
    }

    /// <summary>Throttled, best-effort persistence of a key's last-used time.</summary>
    public async Task TouchKeyAsync(string keyHash, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (_lastFlushed.TryGetValue(keyHash, out var last) && now - last < TouchThrottle)
            return;

        _lastFlushed[keyHash] = now;

        await _gate.WaitAsync(ct);
        try
        {
            var k = _config.ApiKeys.FirstOrDefault(x => Eq(x.Hash, keyHash));
            if (k is null) return;

            k.LastUsed = now;
            try { IniAuthWriter.Save(_config); }
            catch (Exception ex) { _logger.LogDebug(ex, "[Auth] last_used persist failed (non-fatal)."); }
            _cache = BuildCache();
        }
        finally { _gate.Release(); }
    }

    // ----- Management (admin): mutate config + write INI + rebuild cache -----

    public Task<IReadOnlyList<ApiKeyInfo>> ListKeysAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ApiKeyInfo> list = _config.ApiKeys
            .Select(k => new ApiKeyInfo
            {
                Id = k.Id,
                Name = k.Name,
                Owner = k.Owner,
                Description = k.Description,
                RoleName = k.Role,
                Enabled = k.Enabled,
                ExpiresAt = k.Expires,
                LastUsedAt = k.LastUsed
            })
            .ToList();

        return Task.FromResult(list);
    }

    public async Task<CreatedApiKey> CreateKeyAsync(
        string name, string? owner, string? description,
        string roleName, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        ValidateIniValue(name, nameof(name));
        ValidateIniValue(owner, nameof(owner));
        ValidateIniValue(description, nameof(description));
        ValidateRoleName(roleName);
        var rawKey = GenerateRawKey();

        await _gate.WaitAsync(ct);
        try
        {
            if (!_config.Roles.Any(r => Eq(r.Name, roleName)))
                throw new ArgumentException($"Unknown role '{roleName}'.");

            var id = _config.ApiKeys.Count == 0 ? 1 : _config.ApiKeys.Max(k => k.Id) + 1;

            _config.ApiKeys.Add(new AuthApiKeyConfig
            {
                Id = id,
                Name = name,
                Owner = owner,
                Description = description,
                Hash = HashKey(rawKey),
                Role = roleName,
                Enabled = true,
                Expires = expiresAt
            });

            IniAuthWriter.Save(_config);
            _cache = BuildCache();

            return new CreatedApiKey { Id = id, ApiKey = rawKey, RoleName = roleName };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Delete a key by id. Returns false when no such key exists.</summary>
    public async Task<bool> RevokeKeyAsync(long id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var removed = _config.ApiKeys.RemoveAll(k => k.Id == id) > 0;
            if (removed)
            {
                IniAuthWriter.Save(_config);
                _cache = BuildCache();
            }
            return removed;
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RoleInfo> list = _config.Roles.Select(ToRoleInfo).ToList();
        return Task.FromResult(list);
    }

    public async Task UpsertRoleAsync(string name, RoleUpsertRequest req, CancellationToken ct = default)
    {
        ValidateRoleName(name);
        ValidateIniValue(req.Description, nameof(req.Description));
        foreach (var value in (req.CollectionsAllow ?? []).Concat(req.CollectionsDeny ?? [])
            .Concat(req.FunctionsAllow ?? []).Concat(req.FunctionsDeny ?? []))
            ValidateIniValue(value, "permission pattern");
        foreach (var rule in req.ColumnRules ?? [])
        {
            ValidateIniValue(rule.Pattern, "column rule pattern");
            foreach (var column in rule.Allow.Concat(rule.Hide)) ValidateIniValue(column, "column name");
        }
        await _gate.WaitAsync(ct);
        try
        {
            var role = _config.Roles.FirstOrDefault(r => Eq(r.Name, name));
            if (role is null)
            {
                role = new AuthRoleConfig { Name = name };
                _config.Roles.Add(role);
            }

            role.Description = req.Description;
            role.CollectionsAllow = req.CollectionsAllow ?? [];
            role.CollectionsDeny = req.CollectionsDeny ?? [];
            role.FunctionsAllow = req.FunctionsAllow ?? [];
            role.FunctionsDeny = req.FunctionsDeny ?? [];
            role.ColumnRules = (req.ColumnRules ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Pattern))
                .ToDictionary(
                    c => c.Pattern,
                    c => new AuthColumnRuleConfig { Allow = [.. c.Allow], Hide = [.. c.Hide] },
                    StringComparer.OrdinalIgnoreCase);

            IniAuthWriter.Save(_config);
            _cache = BuildCache();
        }
        finally { _gate.Release(); }
    }

    public async Task<DeleteRoleResult> DeleteRoleAsync(string name, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_config.ApiKeys.Any(k => Eq(k.Role, name)))
                return DeleteRoleResult.Referenced;

            if (_config.Roles.RemoveAll(r => Eq(r.Name, name)) == 0)
                return DeleteRoleResult.NotFound;

            IniAuthWriter.Save(_config);
            _cache = BuildCache();
            return DeleteRoleResult.Deleted;
        }
        finally { _gate.Release(); }
    }

    // ----- Internals -----

    private static RoleInfo ToRoleInfo(AuthRoleConfig role) => new()
    {
        Name = role.Name,
        Description = role.Description,
        CollectionsAllow = role.CollectionsAllow,
        CollectionsDeny = role.CollectionsDeny,
        FunctionsAllow = role.FunctionsAllow,
        FunctionsDeny = role.FunctionsDeny,
        ColumnRules = role.ColumnRules
            .Select(kv => new RoleColumnRule { Pattern = kv.Key, Allow = kv.Value.Allow, Hide = kv.Value.Hide })
            .ToList()
    };

    private static bool Evaluate(
        AuthCache cache, IReadOnlyList<string> roles, ResourceType type, string full)
    {
        var matching = cache.Permissions
            .Where(p => p.ResourceType == type
                        && roles.Contains(p.RoleName, StringComparer.OrdinalIgnoreCase)
                        && Glob.Match(full, p.Pattern))
            .ToList();

        if (matching.Any(p => p.Effect == PermissionEffect.Deny))
            return false;

        return matching.Any(p => p.Effect == PermissionEffect.Allow);
    }

    private AuthCache BuildCache()
    {
        var permissions = new List<RolePermission>();

        foreach (var role in _config.Roles)
        {
            foreach (var pattern in role.CollectionsAllow)
            {
                role.ColumnRules.TryGetValue(pattern, out var cr);
                permissions.Add(new RolePermission
                {
                    RoleName = role.Name,
                    ResourceType = ResourceType.Collection,
                    Pattern = pattern,
                    Effect = PermissionEffect.Allow,
                    ColumnsAllow = cr?.Allow ?? [],
                    ColumnsHide = cr?.Hide ?? []
                });
            }

            foreach (var pattern in role.CollectionsDeny)
                permissions.Add(new RolePermission
                {
                    RoleName = role.Name,
                    ResourceType = ResourceType.Collection,
                    Pattern = pattern,
                    Effect = PermissionEffect.Deny
                });

            foreach (var pattern in role.FunctionsAllow)
                permissions.Add(new RolePermission
                {
                    RoleName = role.Name,
                    ResourceType = ResourceType.Function,
                    Pattern = pattern,
                    Effect = PermissionEffect.Allow
                });

            foreach (var pattern in role.FunctionsDeny)
                permissions.Add(new RolePermission
                {
                    RoleName = role.Name,
                    ResourceType = ResourceType.Function,
                    Pattern = pattern,
                    Effect = PermissionEffect.Deny
                });
        }

        var keys = new Dictionary<string, ApiKeyRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _config.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(k.Hash))
                continue;

            keys[k.Hash] = new ApiKeyRecord
            {
                Name = k.Name,
                Owner = k.Owner,
                Description = k.Description,
                KeyHash = k.Hash,
                RoleName = k.Role,
                Enabled = k.Enabled,
                ExpiresAt = k.Expires,
                LastUsedAt = k.LastUsed
            };
        }

        _logger.LogInformation(
            "[Auth] Cache built — {Roles} roles, {Permissions} permissions, {Keys} API keys.",
            _config.Roles.Count, permissions.Count, keys.Count);

        return new AuthCache { Permissions = permissions, KeysByHash = keys };
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void ValidateRoleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new ArgumentException("Role names may contain only letters, numbers, '.', '_' and '-'.");
    }

    public bool IsAdminKeyHashUsable(string keyHash)
        => _cache.KeysByHash.TryGetValue(keyHash, out var key) && key.IsUsable &&
           string.Equals(key.RoleName, _config.Auth.AdminRole, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> UpdateKeyAsync(long id, bool enabled, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var key = _config.ApiKeys.FirstOrDefault(k => k.Id == id);
            if (key is null) return false;
            key.Enabled = enabled;
            key.Expires = expiresAt;
            IniAuthWriter.Save(_config);
            _cache = BuildCache();
            return true;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateIniValue(string? value, string name)
    {
        if (value?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException($"{name} cannot contain line breaks.");
    }

    private static string Full(string schema, string name) => $"{schema}.{name}";

    private sealed class AuthCache
    {
        public IReadOnlyList<RolePermission> Permissions { get; init; } = [];

        public IReadOnlyDictionary<string, ApiKeyRecord> KeysByHash { get; init; }
            = new Dictionary<string, ApiKeyRecord>(StringComparer.OrdinalIgnoreCase);
    }
}

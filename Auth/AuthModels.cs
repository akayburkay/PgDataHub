namespace PgDataHub.Auth;

public enum PrincipalSource { ApiKey, Jwt, Anonymous }

/// <summary>Resolved caller identity used for authorization decisions.</summary>
public sealed class Principal
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> RoleNames { get; init; }

    public PrincipalSource Source { get; init; }
}

public sealed class RoleDefinition
{
    public required string Name { get; init; }

    public string? Description { get; init; }
}

public enum PermissionEffect { Allow, Deny }

public enum ResourceType { Collection, Function, VolatileFunction }

public sealed class RolePermission
{
    public required string RoleName { get; init; }

    public ResourceType ResourceType { get; init; }

    public required string Pattern { get; init; }

    public PermissionEffect Effect { get; init; } = PermissionEffect.Allow;

    /// <summary>Empty = all columns allowed.</summary>
    public IReadOnlyList<string> ColumnsAllow { get; init; } = [];

    public IReadOnlyList<string> ColumnsHide { get; init; } = [];
}

public sealed class ApiKeyRecord
{
    public required string Name { get; init; }

    public string? Owner { get; init; }

    public string? Description { get; init; }

    public required string KeyHash { get; init; }

    public required string RoleName { get; init; }

    public bool Enabled { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public bool IsUsable =>
        Enabled && (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);
}

/// <summary>Effective column visibility for a principal on one collection.</summary>
public readonly record struct ColumnRules(
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Hide);

/// <summary>Safe (hash-free) view of an API key, for the management endpoints.</summary>
public sealed class ApiKeyInfo
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? Owner { get; init; }
    public string? Description { get; init; }
    public required string RoleName { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>Role + its permissions, for the management endpoints / UI.</summary>
public sealed class RoleInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> CollectionsAllow { get; init; } = [];
    public IReadOnlyList<string> CollectionsDeny { get; init; } = [];
    public IReadOnlyList<string> FunctionsAllow { get; init; } = [];
    public IReadOnlyList<string> FunctionsDeny { get; init; } = [];
    public IReadOnlyList<string> FunctionsVolatileAllow { get; init; } = [];
    public IReadOnlyList<string> FunctionsVolatileDeny { get; init; } = [];
    public IReadOnlyList<RoleColumnRule> ColumnRules { get; init; } = [];
    public IReadOnlyList<RowRule> RowRules { get; init; } = [];
}

public sealed class RoleColumnRule
{
    public required string Pattern { get; init; }
    public IReadOnlyList<string> Allow { get; init; } = [];
    public IReadOnlyList<string> Hide { get; init; } = [];
}

/// <summary>Body for PUT /auth/roles/{name} (full replace).</summary>
public sealed class RoleUpsertRequest
{
    public string? Description { get; init; }
    public List<string>? CollectionsAllow { get; init; }
    public List<string>? CollectionsDeny { get; init; }
    public List<string>? FunctionsAllow { get; init; }
    public List<string>? FunctionsDeny { get; init; }
    public List<string>? FunctionsVolatileAllow { get; init; }
    public List<string>? FunctionsVolatileDeny { get; init; }
    public List<RoleColumnRule>? ColumnRules { get; init; }
    public List<RowRule>? RowRules { get; init; }
}

public enum DeleteRoleResult { Deleted, NotFound, Referenced }

/// <summary>Request body to create an API key.</summary>
public sealed class CreateApiKeyRequest
{
    public string? Name { get; init; }
    public string? Owner { get; init; }
    public string? Description { get; init; }
    public string? RoleName { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Result of creating an API key. The raw key is shown only once.</summary>
public sealed class CreatedApiKey
{
    public required long Id { get; init; }
    public required string ApiKey { get; init; }
    public required string RoleName { get; init; }
}

public sealed class UpdateApiKeyRequest
{
    public bool Enabled { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

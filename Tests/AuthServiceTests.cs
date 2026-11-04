using Microsoft.Extensions.Logging.Abstractions;
using PgDataHub.Auth;
using PgDataHub.Config;

namespace PgDataHub.Tests;

public sealed class AuthServiceTests
{
    [Fact]
    public async Task Function_read_permission_does_not_grant_volatile_execution_and_deny_wins()
    {
        var service = Service(new() { Name = "reader", FunctionsAllow = ["*"] },
            new() { Name = "writer", FunctionsVolatileAllow = ["public.*"] },
            new() { Name = "blocked", FunctionsVolatileDeny = ["public.write"] });
        Assert.True(await service.CanAccessFunctionAsync(["reader"], "public", "write"));
        Assert.False(await service.CanExecuteVolatileFunctionAsync(["reader"], "public", "write"));
        Assert.True(await service.CanExecuteVolatileFunctionAsync(["reader", "writer"], "public", "write"));
        Assert.False(await service.CanExecuteVolatileFunctionAsync(["reader", "writer", "blocked"], "public", "write"));
    }

    [Fact]
    public async Task Access_denies_when_any_matching_rule_denies()
    {
        var service = Service(
            Role("reader", allow: ["public.*"]),
            Role("blocked", deny: ["public.roads"]));

        Assert.False(await service.CanAccessCollectionAsync(["reader", "blocked"], "public", "roads"));
    }

    [Fact]
    public async Task Access_requires_at_least_one_matching_allow()
    {
        var service = Service(Role("reader", allow: ["public.roads"]));

        Assert.True(await service.CanAccessCollectionAsync(["reader"], "public", "roads"));
        Assert.False(await service.CanAccessCollectionAsync(["reader"], "public", "buildings"));
        Assert.False(await service.CanAccessCollectionAsync(["unknown"], "public", "roads"));
    }

    [Fact]
    public async Task Column_rules_merge_every_matching_pattern()
    {
        var role = Role("reader", allow: ["public.*"]);
        role.ColumnRules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["public.*"] = new AuthColumnRuleConfig { Allow = ["id", "name"], Hide = ["secret"] },
            ["*.roads"] = new AuthColumnRuleConfig { Allow = ["id", "kind"], Hide = ["salary"] }
        };
        var rules = await Service(role).ResolveColumnRulesAsync(["reader"], "public", "roads");

        Assert.Equal(["id", "name", "kind"], rules.Allow);
        Assert.Equal(["secret", "salary"], rules.Hide);
    }

    [Fact]
    public async Task Empty_allow_in_any_matching_column_rule_means_all_columns()
    {
        var role = Role("reader", allow: ["public.*"]);
        role.ColumnRules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["public.*"] = new AuthColumnRuleConfig { Allow = [] },
            ["*.roads"] = new AuthColumnRuleConfig { Allow = ["id"] }
        };

        var rules = await Service(role).ResolveColumnRulesAsync(["reader"], "public", "roads");
        Assert.Empty(rules.Allow);
    }

    [Fact]
    public void HashKey_is_deterministic_and_does_not_return_raw_key()
    {
        var first = AuthService.HashKey("super-secret");
        var second = AuthService.HashKey("super-secret");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.DoesNotContain("super-secret", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveApiKey_rejects_disabled_and_expired_keys()
    {
        var rawEnabled = "enabled-key";
        var rawDisabled = "disabled-key";
        var rawExpired = "expired-key";
        var config = new PgDataHubConfig
        {
            ApiKeys =
            [
                Key(rawEnabled, enabled: true),
                Key(rawDisabled, enabled: false),
                Key(rawExpired, enabled: true, expires: DateTimeOffset.UtcNow.AddMinutes(-1))
            ]
        };
        var service = new AuthService(config, NullLogger<AuthService>.Instance);

        Assert.NotNull(await service.ResolveApiKeyAsync(rawEnabled));
        Assert.Null(await service.ResolveApiKeyAsync(rawDisabled));
        Assert.Null(await service.ResolveApiKeyAsync(rawExpired));
        Assert.Null(await service.ResolveApiKeyAsync("unknown"));
    }

    private static AuthService Service(params AuthRoleConfig[] roles)
        => new(new PgDataHubConfig { Roles = [.. roles] }, NullLogger<AuthService>.Instance);

    private static AuthRoleConfig Role(string name, List<string>? allow = null, List<string>? deny = null)
        => new() { Name = name, CollectionsAllow = allow ?? [], CollectionsDeny = deny ?? [] };

    private static AuthApiKeyConfig Key(string raw, bool enabled, DateTimeOffset? expires = null)
        => new()
        {
            Id = Random.Shared.NextInt64(1, long.MaxValue), Name = raw, Role = "reader",
            Hash = AuthService.HashKey(raw), Enabled = enabled, Expires = expires
        };
}

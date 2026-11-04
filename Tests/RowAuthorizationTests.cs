using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Features;

namespace PgDataHub.Tests;

public sealed class RowAuthorizationTests
{
    [Fact]
    public void Jwt_claim_values_are_parameters_and_missing_claims_deny_rows()
    {
        var collection = TestData.Collection("id", "tenant_id");
        collection.Columns[1].Type = "integer";
        var config = new PgDataHubConfig { Roles = [new() { Name = "reader", CollectionsAllow = ["public.*"],
            RowRules = [new() { Pattern = "public.roads", Conditions = [new() { Column = "tenant_id", Claim = "tenant" }] }] }] };
        ClaimsPrincipal User(params string[] tenants) => new(new ClaimsIdentity(
            new[] { new Claim(AuthClaims.Role, "reader") }.Concat(tenants.Select(v => new Claim("tenant", v))),
            "JWT", ClaimTypes.Name, AuthClaims.Role));
        collection.RowFilters = RowRule.Resolve(config, User("17", "21"), collection);
        var query = new FeatureQueryBuilder(config).Build(collection, new() { Count = true });
        Assert.Contains("t.\"tenant_id\" in (@", query.Sql);
        Assert.DoesNotContain("17", query.Sql);
        Assert.Equal(new object[] { 17L, 21L }, query.Parameters.Select(p => p.Value));
        collection.RowFilters = RowRule.Resolve(config, User(), collection);
        Assert.Contains("where false", new FeatureQueryBuilder(config).Build(collection).Sql);
        collection.RowFilters = RowRule.Resolve(config, User("invalid-integer"), collection);
        Assert.Contains(" and false", new FeatureQueryBuilder(config).BuildFeatureById(collection, "1").Sql);
    }

    [Fact]
    public void Restrictive_role_grants_are_union_and_only_a_granting_unrestricted_role_widens_access()
    {
        var collection = TestData.Collection("id", "tenant_id");
        var config = new PgDataHubConfig { Roles = [
            new() { Name = "first", CollectionsAllow = ["public.*"], RowRules = [Rule("1")] },
            new() { Name = "second", CollectionsAllow = ["public.*"], RowRules = [Rule("2")] },
            new() { Name = "unrelated", CollectionsAllow = ["private.*"] },
            new() { Name = "unrestricted", CollectionsAllow = ["public.*"] }] };
        ClaimsPrincipal User(params string[] roles) => new(new ClaimsIdentity(roles.Select(r => new Claim(AuthClaims.Role, r)), "test", ClaimTypes.Name, AuthClaims.Role));
        collection.RowFilters = RowRule.Resolve(config, User("first", "second", "unrelated"), collection);
        var query = new FeatureQueryBuilder(config).Build(collection);
        Assert.Contains(" or ", query.Sql);
        Assert.Equal(2, query.Parameters.Count);
        Assert.Null(RowRule.Resolve(config, User("first", "unrestricted"), collection));
    }

    [Fact]
    public void Sql_fragments_remain_literal_values_and_unknown_policy_columns_fail_closed()
    {
        const string value = "' OR true; --";
        var collection = TestData.Collection("id", "tenant_id");
        var config = new PgDataHubConfig { Roles = [new() { Name = "reader", CollectionsAllow = ["*"], RowRules = [Rule(value)] }] };
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaims.Role, "reader")], "test", ClaimTypes.Name, AuthClaims.Role));
        collection.RowFilters = RowRule.Resolve(config, user, collection);
        var query = new FeatureQueryBuilder(config).Build(collection);
        Assert.DoesNotContain(value, query.Sql);
        Assert.Equal(value, Assert.Single(query.Parameters).Value);
        config.Roles[0].RowRules = [new() { Pattern = "*", Conditions = [new() { Column = "missing", Values = ["1"] }] }];
        collection.RowFilters = RowRule.Resolve(config, user, collection);
        Assert.Contains("where false", new FeatureQueryBuilder(config).Build(collection).Sql);
    }

    [Fact]
    public async Task Managed_row_rules_survive_auth_save_and_ini_reload_and_invalid_rules_are_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "pgdh-rows-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            File.WriteAllText(path, "[auth]\nenabled=true\n");
            var config = PgDataHubConfig.Load(path);
            var auth = new AuthService(config, NullLogger<AuthService>.Instance);
            await auth.UpsertRoleAsync("reader", new() { CollectionsAllow = ["public.*"], RowRules = [Rule("17")] });
            var reloaded = PgDataHubConfig.Load(path);
            Assert.Equal("17", reloaded.Roles.Single().RowRules.Single().Conditions.Single().Values.Single());
            Assert.Single((await auth.ListRolesAsync()).Single().RowRules);
            await Assert.ThrowsAsync<ArgumentException>(() => auth.UpsertRoleAsync("bad", new() {
                RowRules = [new() { Pattern = "*", Conditions = [new() { Column = "id", Operator = "rawSQL", Values = ["true"] }] }] }));
            await Assert.ThrowsAsync<ArgumentException>(() => auth.UpsertRoleAsync("bad", new() {
                RowRules = [new() { Pattern = "*", Conditions = [new() { Column = "id", Claim = "tenant", Values = ["1"] }] }] }));
        }
        finally { File.Delete(path); }
    }

    private static RowRule Rule(string tenant) => new() { Pattern = "*", Conditions = [new() { Column = "tenant_id", Values = [tenant] }] };
}

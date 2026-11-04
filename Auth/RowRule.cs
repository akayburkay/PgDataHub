using System.Security.Claims;
using PgDataHub.Config;
using PgDataHub.Features;
using PgDataHub.Metadata;

namespace PgDataHub.Auth;

public sealed class RowRule
{
    public string Pattern { get; init; } = "";
    public List<RowCondition> Conditions { get; init; } = [];

    internal static void Validate(IReadOnlyList<RowRule> rules)
    {
        if (rules.Count > 64) throw new ArgumentException("At most 64 row rules are allowed per role.");
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.IndexOfAny(['\r', '\n']) >= 0 ||
                rule.Conditions is null || rule.Conditions.Count is < 1 or > 16)
                throw new ArgumentException("Every row rule needs a collection pattern and 1–16 conditions.");
            foreach (var condition in rule.Conditions)
            {
                if (condition is null || string.IsNullOrWhiteSpace(condition.Column) ||
                    !TryOperator(condition.Operator, out var op) || condition.Values is null || condition.Values.Count > 256 ||
                    condition.Values.Any(v => v is null))
                    throw new ArgumentException("Invalid row condition column, operator or values.");
                var count = condition.Values.Count;
                if (!string.IsNullOrWhiteSpace(condition.Claim))
                {
                    if (count != 0 || op is not (FilterOperator.Eq or FilterOperator.In))
                        throw new ArgumentException("Claim conditions use eq/in and cannot also have literal values.");
                }
                else if (op switch
                {
                    FilterOperator.IsNull or FilterOperator.IsNotNull => count != 0,
                    FilterOperator.In or FilterOperator.NotIn => count == 0,
                    FilterOperator.Between => count != 2,
                    _ => count != 1
                }) throw new ArgumentException("The row condition has the wrong number of values.");
            }
        }
    }

    internal static bool TryOperator(string value, out FilterOperator op)
        => Enum.TryParse(value, true, out op) && op is FilterOperator.Eq or FilterOperator.Neq
            or FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte
            or FilterOperator.In or FilterOperator.NotIn or FilterOperator.IsNull or FilterOperator.IsNotNull
            or FilterOperator.Between;

    internal static IReadOnlyList<IReadOnlyList<PropertyFilter>>? Resolve(
        PgDataHubConfig config, ClaimsPrincipal user, CollectionMetadata collection)
    {
        var roles = user.RoleNames();
        var name = $"{collection.Schema}.{collection.Name}";
        var groups = new List<IReadOnlyList<PropertyFilter>>();
        var columns = collection.PolicyColumns ?? collection.Columns;
        foreach (var role in config.Roles.Where(r => roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase) &&
            r.CollectionsAllow.Any(p => Glob.Match(name, p))))
        {
            var rules = role.RowRules.Where(r => Glob.Match(name, r.Pattern)).ToList();
            // Grants across roles are a union: an unrestricted granting role grants all rows.
            if (rules.Count == 0) return null;
            var conditions = new List<PropertyFilter>();
            var valid = true;
            foreach (var condition in rules.SelectMany(r => r.Conditions))
            {
                var column = columns.FirstOrDefault(c => c.Name.Equals(condition.Column, StringComparison.OrdinalIgnoreCase));
                if (column is null || column.Type.StartsWith("geometry", StringComparison.OrdinalIgnoreCase) ||
                    column.Type.StartsWith("geography", StringComparison.OrdinalIgnoreCase) || !TryOperator(condition.Operator, out var op))
                { valid = false; break; }
                IReadOnlyList<string> values = condition.Values;
                if (!string.IsNullOrWhiteSpace(condition.Claim))
                {
                    values = user.FindAll(condition.Claim).Select(c => c.Value).Distinct().ToArray();
                    if (values.Count == 0) { valid = false; break; }
                    op = FilterOperator.In;
                }
                conditions.Add(new() { PropertyName = column.Name, Operator = op, Values = values });
            }
            if (valid) groups.Add(conditions);
        }
        return groups;
    }
}

public sealed class RowCondition
{
    public string Column { get; init; } = "";
    public string Operator { get; init; } = "eq";
    public List<string> Values { get; init; } = [];
    public string? Claim { get; init; }
}

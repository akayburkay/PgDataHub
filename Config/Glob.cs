namespace PgDataHub.Config;

/// <summary>
/// Shared glob matcher used by both metadata include/exclude filtering and
/// auth role-permission patterns. Supports '*' (match all) and a single
/// trailing '*' prefix wildcard, matching the original MetadataService rules.
/// </summary>
internal static class Glob
{
    public static bool Match(string value, string pattern)
        => pattern == "*"
            ? true
            : pattern.EndsWith('*')
                ? value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
}

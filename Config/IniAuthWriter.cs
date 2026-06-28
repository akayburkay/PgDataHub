using System.Globalization;
using System.Text;

namespace PgDataHub.Config;

/// <summary>
/// Writes auth data ([role.*] and [apikey.*] sections) back into the existing INI,
/// preserving every other section, comment and blank line. All other auth sections
/// are stripped and re-emitted from the model at the end of the file. Writes are
/// serialized through a process-wide gate.
/// </summary>
internal static class IniAuthWriter
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private const string HeaderComment =
        "# === Auth data (managed by PgDataHub — manual edits here may be overwritten) ===";

    public static void Save(PgDataHubConfig config)
    {
        Gate.Wait();
        try
        {
            var kept = ReadNonAuthLines(config.SourcePath);

            var sb = new StringBuilder();
            foreach (var line in kept)
                sb.AppendLine(line);

            if (kept.Count > 0)
                sb.AppendLine();

            AppendAuth(sb, config);

            File.WriteAllText(config.SourcePath, sb.ToString());
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>All lines except those inside a [role.*] or [apikey.*] section
    /// (and our generated header), with trailing blank lines trimmed.</summary>
    private static List<string> ReadNonAuthLines(string path)
    {
        var kept = new List<string>();

        if (!File.Exists(path))
            return kept;

        var inAuthData = false;

        foreach (var raw in File.ReadAllLines(path))
        {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var sec = trimmed[1..^1].Trim().ToLowerInvariant();
                inAuthData = sec.StartsWith("role.") || sec.StartsWith("apikey.");
            }

            if (inAuthData)
                continue;

            if (trimmed == HeaderComment)
                continue;

            kept.Add(raw);
        }

        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
            kept.RemoveAt(kept.Count - 1);

        return kept;
    }

    private static void AppendAuth(StringBuilder sb, PgDataHubConfig config)
    {
        sb.AppendLine(HeaderComment);

        foreach (var role in config.Roles)
        {
            sb.AppendLine();
            sb.AppendLine($"[role.{role.Name}]");
            sb.AppendLine($"description={role.Description ?? ""}");
            sb.AppendLine($"collections.allow={Join(role.CollectionsAllow)}");
            sb.AppendLine($"collections.deny={Join(role.CollectionsDeny)}");
            sb.AppendLine($"functions.allow={Join(role.FunctionsAllow)}");
            sb.AppendLine($"functions.deny={Join(role.FunctionsDeny)}");

            foreach (var (pattern, rule) in role.ColumnRules)
            {
                if (rule.Allow.Count > 0)
                    sb.AppendLine($"columns.{pattern}.allow={Join(rule.Allow)}");
                if (rule.Hide.Count > 0)
                    sb.AppendLine($"columns.{pattern}.hide={Join(rule.Hide)}");
            }
        }

        foreach (var key in config.ApiKeys)
        {
            sb.AppendLine();
            sb.AppendLine($"[apikey.{key.Id}]");
            sb.AppendLine($"name={key.Name}");
            sb.AppendLine($"owner={key.Owner ?? ""}");
            sb.AppendLine($"description={key.Description ?? ""}");
            sb.AppendLine($"hash={key.Hash}");
            sb.AppendLine($"role={key.Role}");
            sb.AppendLine($"enabled={(key.Enabled ? "true" : "false")}");
            sb.AppendLine($"expires={Date(key.Expires)}");
            sb.AppendLine($"lastUsed={Date(key.LastUsed)}");
        }
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static string Date(DateTimeOffset? value)
        => value?.ToString("o", CultureInfo.InvariantCulture) ?? "";
}

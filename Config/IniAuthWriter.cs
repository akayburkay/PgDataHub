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

            AtomicWrite(config.SourcePath, sb.ToString());
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void SaveAll(PgDataHubConfig config)
    {
        Gate.Wait();
        try
        {
            var lines = File.Exists(config.SourcePath)
                ? File.ReadAllLines(config.SourcePath).ToList()
                : [];

            PatchSection(lines, "database", new Dictionary<string, string>
            {
                ["host"] = config.Database.Host,
                ["port"] = config.Database.Port.ToString(CultureInfo.InvariantCulture),
                ["database"] = config.Database.Database,
                ["user"] = config.Database.User,
                ["password"] = config.Database.Password,
                ["minPoolSize"] = config.Database.MinPoolSize.ToString(CultureInfo.InvariantCulture),
                ["maxPoolSize"] = config.Database.MaxPoolSize.ToString(CultureInfo.InvariantCulture),
                ["commandTimeout"] = config.Database.CommandTimeout.ToString(CultureInfo.InvariantCulture)
            });
            PatchSection(lines, "collections", new Dictionary<string, string>
            {
                ["include"] = Join(config.Collections.Include), ["exclude"] = Join(config.Collections.Exclude)
            });
            PatchSection(lines, "feature", new Dictionary<string, string>
            {
                ["defaultLimit"] = config.Feature.DefaultLimit.ToString(CultureInfo.InvariantCulture),
                ["maxLimit"] = config.Feature.MaxLimit.ToString(CultureInfo.InvariantCulture)
            });
            PatchSection(lines, "function", new Dictionary<string, string>
            {
                ["include"] = Join(config.Function.Include), ["exclude"] = Join(config.Function.Exclude),
                ["defaultTimeout"] = config.Function.DefaultTimeout.ToString(CultureInfo.InvariantCulture),
                ["maxRows"] = config.Function.MaxRows.ToString(CultureInfo.InvariantCulture)
            });
            PatchSection(lines, "auth", new Dictionary<string, string>
            {
                ["enabled"] = Bool(config.Auth.Enabled), ["defaultRole"] = config.Auth.DefaultRole,
                ["adminRole"] = config.Auth.AdminRole
            });
            PatchSection(lines, "auth.apikey", new Dictionary<string, string>
            {
                ["enabled"] = Bool(config.Auth.ApiKey.Enabled), ["header"] = config.Auth.ApiKey.Header
            });
            PatchSection(lines, "auth.jwt", new Dictionary<string, string>
            {
                ["enabled"] = Bool(config.Auth.Jwt.Enabled), ["authority"] = config.Auth.Jwt.Authority,
                ["audience"] = config.Auth.Jwt.Audience, ["roleClaim"] = config.Auth.Jwt.RoleClaim
            });

            var wantedTables = new HashSet<string>(config.Tables.Keys.Select(k => $"{k}.feature"),
                StringComparer.OrdinalIgnoreCase);
            RemoveSections(lines, section => section.EndsWith(".feature", StringComparison.OrdinalIgnoreCase)
                && !wantedTables.Contains(section));
            foreach (var (table, value) in config.Tables)
                PatchSection(lines, $"{table}.feature", new Dictionary<string, string>
                {
                    ["allow"] = Join(value.Feature.Allow), ["hide"] = Join(value.Feature.Hide)
                });

            RemoveSections(lines, section => section.StartsWith("role.", StringComparison.OrdinalIgnoreCase)
                || section.StartsWith("apikey.", StringComparison.OrdinalIgnoreCase));
            lines.RemoveAll(line => line.Trim() == HeaderComment);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            var sb = new StringBuilder(string.Join(Environment.NewLine, lines));
            if (lines.Count > 0) sb.AppendLine().AppendLine();
            AppendAuth(sb, config);
            AtomicWrite(config.SourcePath, sb.ToString());
        }
        finally { Gate.Release(); }
    }

    private static void PatchSection(List<string> lines, string section, IReadOnlyDictionary<string, string> values)
    {
        var start = FindSection(lines, section);
        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            foreach (var pair in values) lines.Add($"{pair.Key}={pair.Value}");
            return;
        }

        var end = start + 1;
        while (end < lines.Count && !IsSection(lines[end])) end++;
        var remaining = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        for (var i = start + 1; i < end; i++)
        {
            var equals = lines[i].IndexOf('=');
            if (equals <= 0) continue;
            var key = lines[i][..equals].Trim();
            if (!remaining.Remove(key, out var value)) continue;
            lines[i] = $"{key}={value}";
        }
        foreach (var pair in remaining) lines.Insert(end++, $"{pair.Key}={pair.Value}");
    }

    private static void RemoveSections(List<string> lines, Func<string, bool> remove)
    {
        for (var i = lines.Count - 1; i >= 0;)
        {
            if (!IsSection(lines[i])) { i--; continue; }
            var section = lines[i].Trim()[1..^1].Trim();
            if (!remove(section)) { i--; continue; }
            var end = i + 1;
            while (end < lines.Count && !IsSection(lines[end])) end++;
            lines.RemoveRange(i, end - i);
            i--;
        }
    }

    private static int FindSection(List<string> lines, string section)
        => lines.FindIndex(line => IsSection(line) &&
            string.Equals(line.Trim()[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase));

    private static bool IsSection(string line)
    {
        var value = line.Trim();
        return value.StartsWith('[') && value.EndsWith(']');
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static void AtomicWrite(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, fullPath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
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

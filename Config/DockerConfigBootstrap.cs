using PgDataHub.Auth;

namespace PgDataHub.Config;

/// <summary>Applies explicit container environment overrides and bootstraps one admin key.</summary>
internal static class DockerConfigBootstrap
{
    public static void Apply(PgDataHubConfig config)
    {
        var changed = false;
        changed |= Set("PGDATAHUB_DB_HOST", value => config.Database.Host = value);
        changed |= SetInt("PGDATAHUB_DB_PORT", value => config.Database.Port = value);
        changed |= Set("PGDATAHUB_DB_NAME", value => config.Database.Database = value);
        changed |= Set("PGDATAHUB_DB_USER", value => config.Database.User = value);
        changed |= Set("PGDATAHUB_DB_PASSWORD", value => config.Database.Password = value);

        var adminKey = Environment.GetEnvironmentVariable("PGDATAHUB_ADMIN_KEY");
        if (!string.IsNullOrWhiteSpace(adminKey))
        {
            if (adminKey.Length < 16)
                throw new InvalidOperationException("PGDATAHUB_ADMIN_KEY must be at least 16 characters.");

            config.Auth.Enabled = true;
            config.Auth.ApiKey.Enabled = true;
            if (string.IsNullOrWhiteSpace(config.Auth.AdminRole)) config.Auth.AdminRole = "admin";

            if (!config.Roles.Any(role => Eq(role.Name, config.Auth.AdminRole)))
            {
                config.Roles.Add(new AuthRoleConfig
                {
                    Name = config.Auth.AdminRole,
                    Description = "Full access",
                    CollectionsAllow = ["*"],
                    FunctionsAllow = ["*"]
                });
                changed = true;
            }

            var hash = AuthService.HashKey(adminKey);
            var staleDockerKeys = config.ApiKeys.RemoveAll(key =>
                Eq(key.Owner ?? "", "docker-compose") &&
                Eq(key.Name, "Docker admin") &&
                !Eq(key.Hash, hash));
            changed |= staleDockerKeys > 0;

            if (!config.ApiKeys.Any(key => Eq(key.Hash, hash)))
            {
                config.ApiKeys.Add(new AuthApiKeyConfig
                {
                    Id = config.ApiKeys.Count == 0 ? 1 : config.ApiKeys.Max(key => key.Id) + 1,
                    Name = "Docker admin",
                    Owner = "docker-compose",
                    Description = "Bootstrapped from PGDATAHUB_ADMIN_KEY",
                    Hash = hash,
                    Role = config.Auth.AdminRole,
                    Enabled = true
                });
                changed = true;
            }
        }

        if (changed) IniAuthWriter.SaveAll(config);
    }

    private static bool Set(string name, Action<string> setter)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        setter(value);
        return true;
    }

    private static bool SetInt(string name, Action<int> setter)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!int.TryParse(raw, out var value))
            throw new InvalidOperationException($"{name} must be an integer.");
        setter(value);
        return true;
    }

    private static bool Eq(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

namespace PgDataHub.Config;

public sealed class PgDataHubConfig
{
    public static PgDataHubConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration file not found: {path}");
        }

        var config = new PgDataHubConfig { SourcePath = path };

        string? section = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            var idx = line.IndexOf('=');

            if (idx < 0 || section is null)
                continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            Parse(config, section, key, value);
        }

        return config;
    }

    private static void Parse(
        PgDataHubConfig config,
        string section,
        string key,
        string value)
    {
        var lower = section.ToLowerInvariant();

        if (lower.StartsWith("role."))
        {
            ParseRole(config, section["role.".Length..], key, value);
            return;
        }

        if (lower.StartsWith("apikey."))
        {
            ParseApiKeyData(config, section["apikey.".Length..], key, value);
            return;
        }

        switch (lower)
        {
            case "database":
                ParseDatabase(config.Database, key, value);
                break;

            case "collections":
                ParseCollections(config.Collections, key, value);
                break;

            case "feature":
                ParseFeature(config.Feature, key, value);
                break;

            case "function":
                ParseFunction(config.Function, key, value);
                break;

            case "auth":
                ParseAuth(config.Auth, key, value);
                break;

            case "auth.apikey":
                ParseApiKey(config.Auth.ApiKey, key, value);
                break;

            case "auth.jwt":
                ParseJwt(config.Auth.Jwt, key, value);
                break;

            default:
                ParseCollectionOverride(config, section, key, value);
                break;
        }
    }

    private static void ParseDatabase(
        DatabaseConfig db,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "host":
                db.Host = value;
                break;

            case "port":
                if (int.TryParse(value, out var port))
                    db.Port = port;
                break;

            case "database":
                db.Database = value;
                break;

            case "user":
                db.User = value;
                break;

            case "password":
                db.Password = value;
                break;

            case "minpoolsize":
                if (int.TryParse(value, out var minPool))
                    db.MinPoolSize = minPool;
                break;

            case "maxpoolsize":
                if (int.TryParse(value, out var maxPool))
                    db.MaxPoolSize = maxPool;
                break;

            case "commandtimeout":
                if (int.TryParse(value, out var timeout))
                    db.CommandTimeout = timeout;
                break;
        }
    }

    private static void ParseCollections(
        CollectionsConfig collections,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
         

            case "include":
                collections.Include = Split(value);
                break;

            case "exclude":
                collections.Exclude = Split(value);
                break;
        }
    }

    private static void ParseFeature(
        FeatureConfig feature,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "defaultlimit":
                if (int.TryParse(value, out var defaultLimit))
                    feature.DefaultLimit = defaultLimit;
                break;

            case "maxlimit":
                if (int.TryParse(value, out var maxLimit))
                    feature.MaxLimit = maxLimit;
                break;

       
        }
    }

    private static void ParseFunction(
        FunctionConfig function,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "include":
                function.Include = Split(value);
                break;

            case "exclude":
                function.Exclude = Split(value);
                break;

            case "volatileinclude":
                function.VolatileInclude = Split(value);
                break;

            

            case "defaulttimeout":
                if (int.TryParse(value, out var timeout))
                    function.DefaultTimeout = timeout;
                break;

            case "maxrows":
                if (int.TryParse(value, out var maxRows))
                    function.MaxRows = maxRows;
                break;
        }
    }

    private static void ParseCollectionOverride(
        PgDataHubConfig config,
        string section,
        string key,
        string value)
    {
        if (!section.EndsWith(
                ".feature",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var table = section[..^8];

        if (!config.Tables.TryGetValue(
                table,
                out var collection))
        {
            collection = new CollectionConfig();
            config.Tables[table] = collection;
        }

        switch (key.ToLowerInvariant())
        {
            

            case "allow":
                collection.Feature.Allow = Split(value);
                break;

            case "hide":
                collection.Feature.Hide = Split(value);
                break;
        }
    }

    private static void ParseAuth(
        AuthConfig auth,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "enabled":
                auth.Enabled = ParseBool(value, auth.Enabled);
                break;

            case "defaultrole":
                auth.DefaultRole = value;
                break;

            case "adminrole":
                auth.AdminRole = value;
                break;
        }
    }

    private static void ParseApiKey(
        ApiKeyAuthConfig apiKey,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "enabled":
                apiKey.Enabled = ParseBool(value, apiKey.Enabled);
                break;

            case "header":
                apiKey.Header = value;
                break;
        }
    }

    private static void ParseJwt(
        JwtAuthConfig jwt,
        string key,
        string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "enabled":
                jwt.Enabled = ParseBool(value, jwt.Enabled);
                break;

            case "authority":
                jwt.Authority = value;
                break;

            case "audience":
                jwt.Audience = value;
                break;

            case "roleclaim":
                jwt.RoleClaim = value;
                break;
        }
    }

    private static void ParseRole(
        PgDataHubConfig config,
        string roleName,
        string key,
        string value)
    {
        var role = config.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));

        if (role is null)
        {
            role = new AuthRoleConfig { Name = roleName };
            config.Roles.Add(role);
        }

        var k = key.ToLowerInvariant();

        switch (k)
        {
            case "description":
                role.Description = value;
                return;
            case "collections.allow":
                role.CollectionsAllow = Split(value);
                return;
            case "collections.deny":
                role.CollectionsDeny = Split(value);
                return;
            case "functions.allow":
                role.FunctionsAllow = Split(value);
                return;
            case "functions.deny":
                role.FunctionsDeny = Split(value);
                return;
            case "functions.volatile.allow":
                role.FunctionsVolatileAllow = Split(value);
                return;
            case "functions.volatile.deny":
                role.FunctionsVolatileDeny = Split(value);
                return;
            case "rows":
                role.RowRules = System.Text.Json.JsonSerializer.Deserialize<List<PgDataHub.Auth.RowRule>>(value,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new FormatException("Row rules must be a JSON array.");
                PgDataHub.Auth.RowRule.Validate(role.RowRules);
                return;
        }

        // columns.<pattern>.allow | columns.<pattern>.hide
        if (k.StartsWith("columns."))
        {
            var rest = key["columns.".Length..];
            var lastDot = rest.LastIndexOf('.');
            if (lastDot <= 0)
                return;

            var pattern = rest[..lastDot];
            var kind = rest[(lastDot + 1)..].ToLowerInvariant();

            if (!role.ColumnRules.TryGetValue(pattern, out var rule))
            {
                rule = new AuthColumnRuleConfig();
                role.ColumnRules[pattern] = rule;
            }

            if (kind == "allow")
                rule.Allow = Split(value);
            else if (kind == "hide")
                rule.Hide = Split(value);
        }
    }

    private static void ParseApiKeyData(
        PgDataHubConfig config,
        string idText,
        string key,
        string value)
    {
        if (!long.TryParse(idText, out var id))
            return;

        var apiKey = config.ApiKeys.FirstOrDefault(k => k.Id == id);

        if (apiKey is null)
        {
            apiKey = new AuthApiKeyConfig { Id = id };
            config.ApiKeys.Add(apiKey);
        }

        switch (key.ToLowerInvariant())
        {
            case "name":
                apiKey.Name = value;
                break;
            case "owner":
                apiKey.Owner = value;
                break;
            case "description":
                apiKey.Description = value;
                break;
            case "hash":
                apiKey.Hash = value;
                break;
            case "role":
                apiKey.Role = value;
                break;
            case "enabled":
                apiKey.Enabled = ParseBool(value, apiKey.Enabled);
                break;
            case "expires":
                apiKey.Expires = ParseDate(value);
                break;
            case "lastused":
                apiKey.LastUsed = ParseDate(value);
                break;
        }
    }

    private static DateTimeOffset? ParseDate(string value)
        => DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var d)
            ? d
            : null;

    private static bool ParseBool(string value, bool fallback)
        => value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback
        };

    private static List<string> Split(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public DatabaseConfig Database { get; set; } = new();

    public CollectionsConfig Collections { get; set; } = new();

    public FeatureConfig Feature { get; set; } = new();

    public FunctionConfig Function { get; set; } = new();

    public Dictionary<string, CollectionConfig> Tables { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public AuthConfig Auth { get; set; } = new();

    /// <summary>Roles and their permissions (auth data, stored in this INI).</summary>
    public List<AuthRoleConfig> Roles { get; set; } = [];

    /// <summary>API keys (auth data, stored in this INI; only hashes are kept).</summary>
    public List<AuthApiKeyConfig> ApiKeys { get; set; } = [];

    /// <summary>Path of the INI this config was loaded from (used for write-back).</summary>
    public string SourcePath { get; set; } = "";
}

public sealed class AuthRoleConfig
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> CollectionsAllow { get; set; } = [];
    public List<string> CollectionsDeny { get; set; } = [];
    public List<string> FunctionsAllow { get; set; } = [];
    public List<string> FunctionsDeny { get; set; } = [];
    public List<string> FunctionsVolatileAllow { get; set; } = [];
    public List<string> FunctionsVolatileDeny { get; set; } = [];
    public List<PgDataHub.Auth.RowRule> RowRules { get; set; } = [];

    /// <summary>Per-pattern column rules, keyed by collection glob pattern.</summary>
    public Dictionary<string, AuthColumnRuleConfig> ColumnRules { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AuthColumnRuleConfig
{
    public List<string> Allow { get; set; } = [];
    public List<string> Hide { get; set; } = [];
}

public sealed class AuthApiKeyConfig
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Owner { get; set; }
    public string? Description { get; set; }
    public string Hash { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? Expires { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
}

public sealed class AuthConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Role applied to unauthenticated requests. Empty = deny (401).</summary>
    public string DefaultRole { get; set; } = "";

    /// <summary>Role required to use the API key management endpoints.</summary>
    public string AdminRole { get; set; } = "admin";

    public ApiKeyAuthConfig ApiKey { get; set; } = new();

    public JwtAuthConfig Jwt { get; set; } = new();
}

public sealed class ApiKeyAuthConfig
{
    public bool Enabled { get; set; } = false;

    public string Header { get; set; } = "X-API-Key";
}

public sealed class JwtAuthConfig
{
    public bool Enabled { get; set; } = false;

    public string Authority { get; set; } = "";

    public string Audience { get; set; } = "";

    /// <summary>Claim type whose values are role names.</summary>
    public string RoleClaim { get; set; } = "roles";
}

public sealed class DatabaseConfig
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = "";

    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    public int MinPoolSize { get; set; } = 5;

    public int MaxPoolSize { get; set; } = 100;

    public int CommandTimeout { get; set; } = 30;
}

public sealed class CollectionsConfig
{
    public List<string> Include { get; set; } = ["*"];

    public List<string> Exclude { get; set; } = [];
}

public sealed class FeatureConfig
{
    public int DefaultLimit { get; set; } = 1000;

    public int MaxLimit { get; set; } = 10000;

}

public sealed class FunctionConfig
{
    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];

    /// <summary>Explicit opt-in for VOLATILE functions; execution also requires a separate role permission.</summary>
    public List<string> VolatileInclude { get; set; } = [];


    public int DefaultTimeout { get; set; } = 30;

    public int MaxRows { get; set; } = 10000;
}

public sealed class CollectionConfig
{
    public FeatureCollectionConfig Feature { get; set; } = new();
}

public sealed class FeatureCollectionConfig
{

    public List<string> Allow { get; set; } = [];

    public List<string> Hide { get; set; } = [];
}

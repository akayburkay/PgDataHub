using PgDataHub.Auth;
using PgDataHub.Metadata;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace PgDataHub.Config;

internal static class ConfigMutationCoordinator
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}

public sealed class ConfigService
{
    private readonly PgDataHubConfig _config;
    private readonly AuthService _auth;
    private readonly MetadataService _metadata;
    private readonly IOptionsMonitorCache<JwtBearerOptions> _jwtOptions;

    public ConfigService(PgDataHubConfig config, AuthService auth, MetadataService metadata,
        IOptionsMonitorCache<JwtBearerOptions> jwtOptions)
    {
        _config = config;
        _auth = auth;
        _metadata = metadata;
        _jwtOptions = jwtOptions;
    }

    public ConfigView Get() => ConfigView.From(_config);

    public async Task<ConfigSaveResult> SaveAsync(ConfigUpdate request, CancellationToken ct)
    {
        Validate(request);
        await ConfigMutationCoordinator.Gate.WaitAsync(ct);
        try
        {
            var databaseChanged = DatabaseChanged(request.Database);
            var nextDatabase = new DatabaseConfig
            {
                Host = request.Database.Host.Trim(), Port = request.Database.Port,
                Database = request.Database.Database.Trim(), User = request.Database.User.Trim(),
                Password = request.Database.Password ?? _config.Database.Password,
                MinPoolSize = request.Database.MinPoolSize, MaxPoolSize = request.Database.MaxPoolSize,
                CommandTimeout = request.Database.CommandTimeout
            };
            if (databaseChanged) await Db.TestAsync(nextDatabase, ct);
            _config.Database.Host = request.Database.Host.Trim();
            _config.Database.Port = request.Database.Port;
            _config.Database.Database = request.Database.Database.Trim();
            _config.Database.User = request.Database.User.Trim();
            if (request.Database.Password is not null) _config.Database.Password = request.Database.Password;
            _config.Database.MinPoolSize = request.Database.MinPoolSize;
            _config.Database.MaxPoolSize = request.Database.MaxPoolSize;
            _config.Database.CommandTimeout = request.Database.CommandTimeout;
            _config.Collections.Include = Clean(request.Collections.Include);
            _config.Collections.Exclude = Clean(request.Collections.Exclude);
            _config.Feature.DefaultLimit = request.Feature.DefaultLimit;
            _config.Feature.MaxLimit = request.Feature.MaxLimit;
            _config.Function.Include = Clean(request.Function.Include);
            _config.Function.Exclude = Clean(request.Function.Exclude);
            _config.Function.DefaultTimeout = request.Function.DefaultTimeout;
            _config.Function.MaxRows = request.Function.MaxRows;
            _config.Auth.Enabled = request.Auth.Enabled;
            _config.Auth.DefaultRole = request.Auth.DefaultRole.Trim();
            _config.Auth.AdminRole = request.Auth.AdminRole.Trim();
            _config.Auth.ApiKey.Enabled = request.Auth.ApiKey.Enabled;
            _config.Auth.ApiKey.Header = request.Auth.ApiKey.Header.Trim();
            _config.Auth.Jwt.Enabled = request.Auth.Jwt.Enabled;
            _config.Auth.Jwt.Authority = request.Auth.Jwt.Authority.Trim();
            _config.Auth.Jwt.Audience = request.Auth.Jwt.Audience.Trim();
            _config.Auth.Jwt.RoleClaim = request.Auth.Jwt.RoleClaim.Trim();
            _config.Tables = request.Tables.ToDictionary(item => item.Collection.Trim(), item => new CollectionConfig
            {
                Feature = new FeatureCollectionConfig { Allow = Clean(item.Allow), Hide = Clean(item.Hide) }
            }, StringComparer.OrdinalIgnoreCase);

            IniAuthWriter.SaveAll(_config);
            if (databaseChanged) await Db.ReloadAsync(_config.Database, ct);
            _jwtOptions.TryRemove(JwtBearerDefaults.AuthenticationScheme);
            await _auth.RefreshAsync(ct);
            await _metadata.RefreshAsync(ct);
            return new(false, "Saved and applied immediately.");
        }
        finally { ConfigMutationCoordinator.Gate.Release(); }
    }

    private bool DatabaseChanged(DatabaseUpdate v) => !Eq(_config.Database.Host, v.Host) ||
        _config.Database.Port != v.Port || !Eq(_config.Database.Database, v.Database) ||
        !Eq(_config.Database.User, v.User) || v.Password is not null ||
        _config.Database.MinPoolSize != v.MinPoolSize || _config.Database.MaxPoolSize != v.MaxPoolSize ||
        _config.Database.CommandTimeout != v.CommandTimeout;

    private bool AuthPipelineChanged(AuthUpdate v) => _config.Auth.Enabled != v.Enabled ||
        _config.Auth.ApiKey.Enabled != v.ApiKey.Enabled || !Eq(_config.Auth.ApiKey.Header, v.ApiKey.Header) ||
        _config.Auth.Jwt.Enabled != v.Jwt.Enabled || !Eq(_config.Auth.Jwt.Authority, v.Jwt.Authority) ||
        !Eq(_config.Auth.Jwt.Audience, v.Jwt.Audience) || !Eq(_config.Auth.Jwt.RoleClaim, v.Jwt.RoleClaim);

    private static void Validate(ConfigUpdate v)
    {
        Required(v.Database.Host, "database.host"); Required(v.Database.Database, "database.database");
        Required(v.Database.User, "database.user"); Range(v.Database.Port, 1, 65535, "database.port");
        Range(v.Database.MinPoolSize, 0, int.MaxValue, "database.minPoolSize");
        Range(v.Database.MaxPoolSize, 1, int.MaxValue, "database.maxPoolSize");
        if (v.Database.MinPoolSize > v.Database.MaxPoolSize) throw new ArgumentException("database.minPoolSize cannot exceed maxPoolSize.");
        Range(v.Database.CommandTimeout, 1, int.MaxValue, "database.commandTimeout");
        Range(v.Feature.DefaultLimit, 0, int.MaxValue, "feature.defaultLimit");
        Range(v.Feature.MaxLimit, 0, int.MaxValue, "feature.maxLimit");
        if (v.Feature.DefaultLimit > v.Feature.MaxLimit) throw new ArgumentException("feature.defaultLimit cannot exceed maxLimit.");
        Range(v.Function.DefaultTimeout, 1, int.MaxValue, "function.defaultTimeout");
        Range(v.Function.MaxRows, 0, int.MaxValue, "function.maxRows");
        Required(v.Auth.AdminRole, "auth.adminRole"); Required(v.Auth.ApiKey.Header, "auth.apiKey.header");
        if (v.Auth.Jwt.Enabled)
        {
            Required(v.Auth.Jwt.Authority, "auth.jwt.authority"); Required(v.Auth.Jwt.Audience, "auth.jwt.audience");
            Required(v.Auth.Jwt.RoleClaim, "auth.jwt.roleClaim");
            if (!Uri.TryCreate(v.Auth.Jwt.Authority, UriKind.Absolute, out _)) throw new ArgumentException("auth.jwt.authority must be an absolute URL.");
        }
        var duplicate = v.Tables.GroupBy(x => x.Collection, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate table override: {duplicate.Key}.");
        foreach (var table in v.Tables) Required(table.Collection, "tables.collection");
        var strings = new[] { v.Database.Host, v.Database.Database, v.Database.User, v.Database.Password,
            v.Auth.DefaultRole, v.Auth.AdminRole, v.Auth.ApiKey.Header, v.Auth.Jwt.Authority,
            v.Auth.Jwt.Audience, v.Auth.Jwt.RoleClaim }.Concat(v.Collections.Include).Concat(v.Collections.Exclude)
            .Concat(v.Function.Include).Concat(v.Function.Exclude)
            .Concat(v.Tables.SelectMany(t => new[] { t.Collection }.Concat(t.Allow).Concat(t.Hide)));
        if (strings.Any(s => s?.IndexOfAny(['\r', '\n']) >= 0)) throw new ArgumentException("Configuration values cannot contain line breaks.");
    }

    private static void Required(string? value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required."); }
    private static void Range(int value, int min, int max, string name) { if (value < min || value > max) throw new ArgumentException($"{name} must be between {min} and {max}."); }
    private static bool Eq(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);
    private static List<string> Clean(IEnumerable<string> values) => values.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record ConfigSaveResult(bool RestartRequired, string Message);
public sealed class ConfigView
{
    public required DatabaseView Database { get; init; }
    public required CollectionsUpdate Collections { get; init; }
    public required FeatureUpdate Feature { get; init; }
    public required FunctionUpdate Function { get; init; }
    public required AuthUpdate Auth { get; init; }
    public required List<TableOverrideUpdate> Tables { get; init; }
    internal static ConfigView From(PgDataHubConfig c) => new()
    {
        Database = new(c.Database.Host, c.Database.Port, c.Database.Database, c.Database.User, c.Database.Password.Length > 0, c.Database.MinPoolSize, c.Database.MaxPoolSize, c.Database.CommandTimeout),
        Collections = new([.. c.Collections.Include], [.. c.Collections.Exclude]),
        Feature = new(c.Feature.DefaultLimit, c.Feature.MaxLimit),
        Function = new([.. c.Function.Include], [.. c.Function.Exclude], c.Function.DefaultTimeout, c.Function.MaxRows),
        Auth = AuthUpdate.From(c.Auth),
        Tables = c.Tables.Select(x => new TableOverrideUpdate(x.Key, [.. x.Value.Feature.Allow], [.. x.Value.Feature.Hide])).ToList()
    };
}
public sealed record DatabaseView(string Host, int Port, string Database, string User, bool PasswordConfigured, int MinPoolSize, int MaxPoolSize, int CommandTimeout);
public sealed record DatabaseUpdate(string Host, int Port, string Database, string User, string? Password, int MinPoolSize, int MaxPoolSize, int CommandTimeout);
public sealed record CollectionsUpdate(List<string> Include, List<string> Exclude);
public sealed record FeatureUpdate(int DefaultLimit, int MaxLimit);
public sealed record FunctionUpdate(List<string> Include, List<string> Exclude, int DefaultTimeout, int MaxRows);
public sealed record ApiKeyAuthUpdate(bool Enabled, string Header);
public sealed record JwtAuthUpdate(bool Enabled, string Authority, string Audience, string RoleClaim);
public sealed record AuthUpdate(bool Enabled, string DefaultRole, string AdminRole, ApiKeyAuthUpdate ApiKey, JwtAuthUpdate Jwt)
{
    internal static AuthUpdate From(AuthConfig c) => new(c.Enabled, c.DefaultRole, c.AdminRole, new(c.ApiKey.Enabled, c.ApiKey.Header), new(c.Jwt.Enabled, c.Jwt.Authority, c.Jwt.Audience, c.Jwt.RoleClaim));
}
public sealed record TableOverrideUpdate(string Collection, List<string> Allow, List<string> Hide);
public sealed record ConfigUpdate(DatabaseUpdate Database, CollectionsUpdate Collections, FeatureUpdate Feature, FunctionUpdate Function, AuthUpdate Auth, List<TableOverrideUpdate> Tables);

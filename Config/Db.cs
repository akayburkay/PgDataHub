using Npgsql;
using Npgsql.NetTopologySuite;

namespace PgDataHub.Config;

public static class Db
{
    private static NpgsqlDataSource? _dataSource;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CancellationTokenSource _changed = new();

    public static void Initialize(
        DatabaseConfig config)
    {
        if (_dataSource is not null)
            return;
        _dataSource = Build(config);
    }

    public static async Task ReloadAsync(DatabaseConfig config, CancellationToken ct = default)
    {
        var replacement = Build(config);
        await using (var test = await replacement.OpenConnectionAsync(ct)) { }
        await Gate.WaitAsync(ct);
        NpgsqlDataSource? previous;
        try
        {
            previous = _dataSource;
            _dataSource = replacement;
            var oldSignal = _changed;
            _changed = new CancellationTokenSource();
            oldSignal.Cancel();
            oldSignal.Dispose();
        }
        finally { Gate.Release(); }
        if (previous is not null) await previous.DisposeAsync();
    }

    public static async Task TestAsync(DatabaseConfig config, CancellationToken ct = default)
    {
        await using var candidate = Build(config);
        await using var connection = await candidate.OpenConnectionAsync(ct);
    }

    private static NpgsqlDataSource Build(DatabaseConfig config)
    {
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port,
            Database = config.Database,
            Username = config.User,
            Password = config.Password,

            Pooling = true,

            MinPoolSize = config.MinPoolSize,
            MaxPoolSize = config.MaxPoolSize,

            CommandTimeout = config.CommandTimeout,

            Timeout = 15,

            ConnectionIdleLifetime = 300
        };

        var builder =
            new NpgsqlDataSourceBuilder(
                csb.ConnectionString);

        builder.UseNetTopologySuite();

        return builder.Build();
    }

    public static ValueTask<NpgsqlConnection>
        OpenConnectionAsync(
            CancellationToken cancellationToken = default)
    {
        if (_dataSource is null)
        {
            throw new InvalidOperationException(
                "Database is not initialized.");
        }

        return _dataSource.OpenConnectionAsync(
            cancellationToken);
    }

    public static NpgsqlDataSource DataSource =>
        _dataSource ??
        throw new InvalidOperationException(
            "Database is not initialized.");

    public static CancellationToken ChangeToken => _changed.Token;
}
